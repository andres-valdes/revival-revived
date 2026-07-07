using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Automations.Components;
using Automations.Domain;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace Automations.E2E;

/// <summary>
/// Autonomous end-to-end test harness for Automations.
///
/// Modes, selected by AUTO_E2E_ROLE:
///   (unset)  single-process "factory": build a full Stockpile->Kiln->Smelter->
///            Assembler->Chest chain and assert Bronze accumulates end to end.
///   host     multiplayer host: builds and runs the factory; a joining client
///            takes over the collector chest (flow) or reads the replicated graph
///            (wiring).
///   client   multiplayer client: joins the host and asserts cross-client material
///            flow (RPC handoff to a client-owned chest) or ZDO replication of the
///            pipe graph and buffers.
///
/// Bounded by a hard timeout so it can never hang a CI job.
/// </summary>
public class E2ERunner : MonoBehaviour {
    private const float HardTimeoutSeconds = 360f;

    private static string ResultPath =>
        Environment.GetEnvironmentVariable("AUTO_E2E_RESULTS")
        ?? Path.Combine(Paths.GameRootPath ?? ".", "e2e-results.txt");

    private readonly List<(string name, bool pass, string detail)> _results = new();
    private bool _started;
    private bool _worldStartIssued;
    private float _elapsed;

    public static void Bootstrap() {
        var go = new GameObject("Automations_E2ERunner");
        DontDestroyOnLoad(go);
        go.AddComponent<E2ERunner>();
        Plugin.Logger.LogInfo($"E2E: harness bootstrapped (role='{E2EConfig.Role}' scenario='{E2EConfig.Scenario}')");
    }

    private void Start() => StartCoroutine(RunAll());

    private void Update() {
        if (E2EConfig.Manual) return; // human is playing; never time out or quit
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed > HardTimeoutSeconds && _started) {
            Log("E2E: HARD TIMEOUT reached, aborting");
            Finish(false, "hard-timeout");
        }
    }

    private IEnumerator RunAll() {
        _started = true;
        Log($"E2E: run starting (role='{E2EConfig.Role}' scenario='{E2EConfig.Scenario}' manual={E2EConfig.Manual})");

        if (E2EConfig.Manual) { yield return StartCoroutine(RunManual()); yield break; }

        if (E2EConfig.IsHost) yield return StartCoroutine(RunHost());
        else if (E2EConfig.IsClient) yield return StartCoroutine(RunClient());
        else yield return StartCoroutine(RunSingleProcessFactory());

        bool allPass = _results.Count > 0;
        foreach (var r in _results) allPass &= r.pass;
        Finish(allPass, allPass ? "all-pass" : "failures");
    }

    // =====================================================================
    //  The factory: shared builder used by every scenario
    // =====================================================================
    /// <summary>
    /// Build the demo factory around a point and wire it into a working chain:
    ///   Stockpile(Wood)  -> Kiln --------\
    ///   Stockpile(Copper)-> Smelter(Cu) --> Assembler(Bronze) -> Collector Chest
    ///   Stockpile(Tin)   -> Smelter(Sn) -/
    /// (the Kiln's Coal feeds both smelters). Returns the collector chest, tagged
    /// "collector", or null if any machine failed to spawn.
    /// </summary>
    private static Machine? BuildFactory(Vector3 center) {
        Machine? Make(MachineKind kind, Vector3 off, string tag) =>
            Machine.Spawn(kind, center + off, Quaternion.identity, tag);

        var wood = Make(MachineKind.Stockpile, new Vector3(-4f, 0f, 3f), "wood");
        var copper = Make(MachineKind.Stockpile, new Vector3(-4f, 0f, 0f), "copper");
        var tin = Make(MachineKind.Stockpile, new Vector3(-4f, 0f, -3f), "tin");
        var kiln = Make(MachineKind.Kiln, new Vector3(-1f, 0f, 3f), "kiln");
        var copperSmelter = Make(MachineKind.Smelter, new Vector3(-1f, 0f, 1f), "copperSmelter");
        var tinSmelter = Make(MachineKind.Smelter, new Vector3(-1f, 0f, -2f), "tinSmelter");
        var assembler = Make(MachineKind.Assembler, new Vector3(2f, 0f, 0f), "assembler");
        var chest = Make(MachineKind.Chest, new Vector3(5f, 0f, 0f), "collector");

        if (wood == null || copper == null || tin == null || kiln == null
            || copperSmelter == null || tinSmelter == null || assembler == null || chest == null) {
            Plugin.Logger.LogError("BuildFactory: a machine failed to spawn");
            return null;
        }

        // Blueprints: Stockpiles produce Wood / CopperOre / TinOre; smelters make
        // Copper / Tin; the assembler makes Bronze.
        wood.SetBlueprint(0);          // Wood
        copper.SetBlueprint(1);        // CopperOre
        tin.SetBlueprint(2);           // TinOre
        copperSmelter.SetBlueprint(0); // CopperOre + Coal -> Copper
        tinSmelter.SetBlueprint(1);    // TinOre + Coal -> Tin
        assembler.SetBlueprint(0);     // Copper + Tin -> Bronze

        WiringTool.Link(wood, kiln);
        WiringTool.Link(kiln, copperSmelter);
        WiringTool.Link(kiln, tinSmelter);
        WiringTool.Link(copper, copperSmelter);
        WiringTool.Link(tin, tinSmelter);
        WiringTool.Link(copperSmelter, assembler);
        WiringTool.Link(tinSmelter, assembler);
        WiringTool.Link(assembler, chest);

        Plugin.Logger.LogInfo("BuildFactory: 8 machines spawned and wired");
        return chest;
    }

    // =====================================================================
    //  SINGLE PROCESS: build the factory locally, assert end-to-end flow
    // =====================================================================
    private IEnumerator RunSingleProcessFactory() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) { Record("local_player", false, "no local player"); yield break; }
        Log($"E2E: player ready: {player.GetPlayerName()}");
        yield return new WaitForSecondsRealtime(2f);

        var chest = BuildFactory(player.transform.position);
        Record("factory_built", chest != null, chest != null ? "8 machines wired" : "spawn failed");
        if (chest == null) yield break;

        // Let the factory run. Poll each stage as evidence flows downstream.
        Log("E2E: running the factory...");
        var kiln = Machine.FindByTag("kiln");
        var cs = Machine.FindByTag("copperSmelter");
        var ts = Machine.FindByTag("tinSmelter");
        var asm = Machine.FindByTag("assembler");

        bool sawCoal = false, sawCopper = false, sawTin = false, sawBronze = false;
        int bronzeInChest = 0;
        float w = 0f;
        while (w < 90f) {
            if (kiln != null && kiln.BufferCount("Coal") > 0) sawCoal = true;
            if (cs != null && cs.BufferCount("Copper") > 0) sawCopper = true;
            if (ts != null && ts.BufferCount("Tin") > 0) sawTin = true;
            if (asm != null && asm.BufferCount("Bronze") > 0) sawBronze = true;
            bronzeInChest = chest.BufferCount("Bronze");
            if (bronzeInChest >= 5) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }

        Record("kiln_makes_coal", sawCoal, $"sawCoal={sawCoal}");
        Record("smelter_makes_copper", sawCopper, $"sawCopper={sawCopper}");
        Record("smelter_makes_tin", sawTin, $"sawTin={sawTin}");
        Record("assembler_makes_bronze", sawBronze, $"sawBronze={sawBronze}");
        Record("collector_fills_with_bronze", bronzeInChest >= 5,
            $"bronzeInChest={bronzeInChest} after={w:F0}s");

        // Second assertion: pipe removal stops flow. Clear the assembler's output
        // and confirm the chest stops gaining bronze.
        if (asm != null) {
            int before = chest.BufferCount("Bronze");
            WiringTool.ClearOutputs(asm);
            yield return new WaitForSecondsRealtime(Mathf.Max(3f, Plugin.TickInterval * 3f));
            int mid = chest.BufferCount("Bronze");
            yield return new WaitForSecondsRealtime(Mathf.Max(3f, Plugin.TickInterval * 3f));
            int after = chest.BufferCount("Bronze");
            bool stopped = after == mid; // no growth once the pipe is cut
            Record("cut_pipe_stops_flow", stopped, $"before={before} mid={mid} after={after}");
        }
    }

    // =====================================================================
    //  MULTIPLAYER: HOST
    // =====================================================================
    private IEnumerator RunHost() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) { Record("host_spawn", false, "no local player"); yield break; }
        Record("host_spawn", true, $"name={player.GetPlayerName()}");

        Machine? chest;
        if (E2EConfig.IsWiringScenario) {
            // A tiny graph the client will read: Stockpile(Wood) -> Chest.
            var src = Machine.Spawn(MachineKind.Stockpile, player.transform.position + new Vector3(-3f, 0f, 0f), Quaternion.identity, "src");
            var dst = Machine.Spawn(MachineKind.Chest, player.transform.position + new Vector3(0f, 0f, 0f), Quaternion.identity, "dst");
            if (src == null || dst == null) { Record("host_build", false, "spawn failed"); yield break; }
            src.SetBlueprint(0); // Wood
            WiringTool.Link(src, dst);
            Record("host_build", true, "src(Wood) -> dst wired");
            chest = dst;
        } else {
            chest = BuildFactory(player.transform.position);
            Record("host_build", chest != null, chest != null ? "factory wired" : "spawn failed");
            if (chest == null) yield break;
        }

        // Wait for the client to connect.
        Log("E2E[host]: waiting for a client to connect...");
        float waited = 0f;
        while (waited < 180f) {
            int peers = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
            int players = Player.GetAllPlayers().Count;
            if (peers >= 1 && players >= 2) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        int peerCount = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
        int playerCount = Player.GetAllPlayers().Count;
        bool connected = peerCount >= 1 && playerCount >= 2;
        Record("host_client_connected", connected, $"peers={peerCount} players={playerCount}");
        if (!connected) yield break;

        // The host just keeps the factory running while the client does its checks.
        // Prove, host-side, that the factory actually produces (host owns the
        // upstream machines).
        var asm = Machine.FindByTag(E2EConfig.IsWiringScenario ? "src" : "assembler");
        string produceItem = E2EConfig.IsWiringScenario ? "Wood" : "Bronze";
        bool hostProduced = false;
        waited = 0f;
        while (waited < 150f) {
            if (asm != null && asm.BufferCount(produceItem) > 0) hostProduced = true;
            // For wiring, the src ships Wood to dst; for flow, the client owns the
            // chest so we only assert upstream production here.
            if (hostProduced) { /* keep running for the client */ }
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("host_factory_runs", hostProduced, $"produced {produceItem}={hostProduced}");
    }

    // =====================================================================
    //  MULTIPLAYER: CLIENT
    // =====================================================================
    private IEnumerator RunClient() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var me = Player.m_localPlayer;
        if (me == null) { Record("client_spawn", false, "no local player"); yield break; }
        Record("client_spawn", true, $"name={me.GetPlayerName()}");

        // See the host player.
        Log("E2E[client]: waiting to see the host player...");
        float waited = 0f;
        while (waited < 60f && Player.GetAllPlayers().Count < 2) { waited += Time.unscaledDeltaTime; yield return null; }
        bool sawRemote = Player.GetAllPlayers().Count >= 2;
        Record("client_sees_host", sawRemote, $"players={Player.GetAllPlayers().Count}");
        if (!sawRemote) yield break;

        if (E2EConfig.IsWiringScenario) yield return StartCoroutine(RunClientWiring());
        else yield return StartCoroutine(RunClientFlow());
    }

    /// <summary>
    /// Flow scenario: find the host's collector chest, CLAIM it (so this client owns
    /// its ZDO), then assert Bronze lands in it -- proving the host's assembler ships
    /// items across the network via a routed RPC to us, the new owner.
    /// </summary>
    private IEnumerator RunClientFlow() {
        Log("E2E[client]: locating the collector chest...");
        Machine? chest = null;
        float w = 0f;
        while (w < 40f && chest == null) {
            chest = Machine.FindByTag("collector");
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_finds_collector", chest != null, chest != null ? $"zdo={chest.ZdoId}" : "not found");
        if (chest == null) yield break;

        // Take ownership of the chest so transfers route to us.
        chest.Claim();
        w = 0f;
        while (w < 5f && !chest.IsOwner) { w += Time.unscaledDeltaTime; yield return null; }
        Record("client_owns_collector", chest.IsOwner, $"owner={chest.IsOwner}");
        if (!chest.IsOwner) yield break;

        int baseline = chest.BufferCount("Bronze");
        Log($"E2E[client]: owns collector (baseline Bronze={baseline}); waiting for cross-client flow...");

        int seen = baseline;
        w = 0f;
        while (w < 150f) {
            seen = chest.BufferCount("Bronze");
            if (seen - baseline >= 3) break;
            // If ownership is stolen back, re-claim (keeps the test meaningful).
            if (!chest.IsOwner) chest.Claim();
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        bool flowed = (seen - baseline) >= 3;
        Record("client_receives_bronze_across_network", flowed,
            $"baseline={baseline} seen={seen} delta={seen - baseline} after={w:F0}s owner={chest.IsOwner}");
    }

    /// <summary>
    /// Wiring scenario: assert the host's pipe graph and buffers replicate to this
    /// client -- the pipe (source ZDO's Outputs) and the destination's buffer both
    /// arrive over the wire, and the buffer keeps growing as the host transfers.
    /// </summary>
    private IEnumerator RunClientWiring() {
        Log("E2E[client]: locating replicated machines...");
        Machine? src = null, dst = null;
        float w = 0f;
        while (w < 40f && (src == null || dst == null)) {
            src ??= Machine.FindByTag("src");
            dst ??= Machine.FindByTag("dst");
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_sees_machines", src != null && dst != null,
            $"src={(src != null)} dst={(dst != null)}");
        if (src == null || dst == null) yield break;

        // The pipe must replicate: src's Outputs should contain dst's ZDOID.
        bool pipeReplicated = false;
        w = 0f;
        while (w < 20f && !pipeReplicated) {
            pipeReplicated = src.View.HasOutput(dst.ZdoId);
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_sees_pipe", pipeReplicated, $"src.Outputs contains dst={pipeReplicated}");

        // The destination buffer must replicate AND keep growing as the host ticks.
        int first = dst.BufferCount("Wood");
        yield return new WaitForSecondsRealtime(Mathf.Max(6f, Plugin.TickInterval * 5f));
        int later = dst.BufferCount("Wood");
        Record("client_sees_buffer_growth", later > first && later > 0,
            $"woodInDst {first} -> {later}");
    }

    // =====================================================================
    //  MANUAL (AUTO_E2E_MANUAL=1): build the factory, then the human plays.
    // =====================================================================
    private IEnumerator RunManual() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());
        var player = Player.m_localPlayer;
        if (player != null) {
            yield return new WaitForSecondsRealtime(2f);
            BuildFactory(player.transform.position + player.transform.forward * 6f);
            Log("E2E: manual mode -- factory built in front of you; hold the wiring key to see the pipes.");
        }
        while (true) yield return null; // idle; the human closes the game
    }

    // =====================================================================
    //  Shared: auto-start the game into a world (role-aware)
    // =====================================================================
    private IEnumerator AutoStart() {
        Log("E2E: waiting for FejdStartup (main menu)...");
        float waited = 0f;
        while (FejdStartup.instance == null && Game.instance == null) {
            waited += Time.unscaledDeltaTime;
            if (waited > 120f) { Log("E2E: FejdStartup never appeared"); yield break; }
            yield return null;
        }
        if (Game.instance != null) { Log("E2E: Game already running"); yield break; }

        yield return new WaitForSecondsRealtime(3f);
        if (_worldStartIssued) yield break;
        _worldStartIssued = true;

        Exception? err = null;
        try {
            var fejd = FejdStartup.instance;

            if (E2EConfig.IsHost) {
                var profile = GetOrCreateProfile(
                    Environment.GetEnvironmentVariable("AUTO_E2E_PROFILE") ?? "auto_host",
                    Environment.GetEnvironmentVariable("AUTO_E2E_CHARNAME") ?? "AutoHost");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var world = World.GetCreateWorld(E2EConfig.WorldName, FileHelpers.FileSource.Local);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: true, openServer: true, publicServer: false, world.m_name, "", world);
                ZNet.ResetServerHost();
                Log($"E2E[host]: world '{world.m_name}', CustomSocket port {E2EConfig.Port}, loading scene");
            } else if (E2EConfig.IsClient) {
                var profile = GetOrCreateProfile(
                    Environment.GetEnvironmentVariable("AUTO_E2E_PROFILE") ?? "auto_client",
                    Environment.GetEnvironmentVariable("AUTO_E2E_CHARNAME") ?? "AutoClient");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: false, openServer: false, publicServer: false, "", "", null);
                ZNet.m_serverHost = E2EConfig.ServerHost;
                ZNet.m_serverHostPort = E2EConfig.Port;
                Log($"E2E[client]: connecting to {E2EConfig.ServerHost}:{E2EConfig.Port}, loading scene");
            } else {
                var profile = GetOrCreateProfile("auto_solo", "AutoSolo");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var soloWorldName = Environment.GetEnvironmentVariable("AUTO_E2E_WORLD") ?? "auto_world";
                var world = World.GetCreateWorld(soloWorldName, FileHelpers.FileSource.Local);
                ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
                ZNet.SetServer(server: true, openServer: false, publicServer: false, world.m_name, "", world);
                ZNet.ResetServerHost();
                Log($"E2E[solo]: world '{world.m_name}', loading scene");
            }

            Traverse.Create(fejd).Field("m_startingWorld").SetValue(true);
            Traverse.Create(fejd).Method("LoadMainScene").GetValue();
        } catch (Exception e) {
            err = e;
        }
        if (err != null) Log("E2E: AutoStart error: " + err);
    }

    private static PlayerProfile GetOrCreateProfile(string filename, string charName) {
        foreach (var p in SaveSystem.GetAllPlayerProfiles()) {
            if (p.GetFilename() == filename) {
                p.m_firstSpawn = false;
                p.Save();
                return p;
            }
        }
        var np = new PlayerProfile(filename, FileHelpers.FileSource.Local);
        np.SetName(charName);
        np.m_firstSpawn = false;
        np.Save();
        return np;
    }

    private IEnumerator WaitForPlayerInWorld() {
        Log("E2E: waiting for player to spawn in world...");
        float waited = 0f;
        while (true) {
            var p = Player.m_localPlayer;
            if (p != null && p.GetComponent<ZNetView>() != null && p.GetComponent<ZNetView>().IsValid()
                && ZNetScene.instance != null) {
                yield return null;
                yield break;
            }
            waited += Time.unscaledDeltaTime;
            if (waited > 240f) { Log("E2E: timed out waiting for player spawn"); yield break; }
            yield return null;
        }
    }

    // =====================================================================
    //  Result plumbing
    // =====================================================================
    private void Record(string name, bool pass, string detail) {
        _results.Add((name, pass, detail));
        Log($"E2E_RESULT: {(pass ? "PASS" : "FAIL")} {name} -- {detail}");
    }

    private static void Log(string msg) => E2ELog.Write(msg);

    private void Finish(bool ok, string reason) {
        var sb = new StringBuilder();
        sb.AppendLine($"overall={(ok ? "PASS" : "FAIL")} role={E2EConfig.Role} scenario={E2EConfig.Scenario} reason={reason}");
        foreach (var r in _results) sb.AppendLine($"{(r.pass ? "PASS" : "FAIL")}\t{r.name}\t{r.detail}");
        var text = sb.ToString();
        try { File.WriteAllText(ResultPath, text); Log($"E2E: results written to {ResultPath}"); }
        catch (Exception e) { Log("E2E: failed to write results: " + e); }
        Log("E2E_SUMMARY_BEGIN\n" + text + "E2E_SUMMARY_END");
        Log($"E2E: DONE overall={(ok ? "PASS" : "FAIL")} -- quitting");
        StartCoroutine(QuitSoon());
    }

    private IEnumerator QuitSoon() {
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
        yield return new WaitForSecondsRealtime(2f);
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
