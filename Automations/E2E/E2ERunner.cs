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
/// Machines are real vanilla pieces (chests, smelters, kilns) that our attach patch
/// turns into pipe nodes, so these tests drive genuine game behaviour -- a piped
/// smelter lights its fire and produces bars exactly as if a player loaded it.
///
/// Modes (AUTO_E2E_ROLE):
///   (unset)  single-process "factory": chest(ore+coal) -> smelter -> chest, plus a
///            chest(wood) -> kiln -> chest coal line; assert bars/coal flow and the
///            machines visibly turn on.
///   host     builds and runs the factory; a client either claims the collector
///            (flow) or reads the replicated pipe graph (wiring).
///   client   joins and asserts cross-client material flow / ZDO replication.
///
/// Bounded by a hard timeout so it can never hang a CI job.
/// </summary>
public class E2ERunner : MonoBehaviour {
    private const float HardTimeoutSeconds = 360f;
    private const string ChestPrefab = "piece_chest_wood";
    private const string SmelterPrefab = "smelter";
    private const string KilnPrefab = "charcoal_kiln";

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
        if (E2EConfig.Manual) return;
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
    //  Factory builders (real vanilla pieces)
    // =====================================================================
    private sealed class Factory {
        public Machine CopperChest = null!, CopperSmelter = null!;
        public Machine TinChest = null!, TinSmelter = null!;
        public Machine Assembler = null!, Collector = null!;
        public Machine WoodChest = null!, Kiln = null!, CoalChest = null!;
    }

    private static Machine? Spawn(string prefab, Vector3 center, Vector3 off, string tag) =>
        Machine.SpawnVanilla(prefab, center + off, Quaternion.identity, tag);

    private static void Fill(Machine? chest, string item, int n) {
        var c = chest != null ? chest.GetComponent<Container>() : null;
        var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(item) : null;
        if (c != null && prefab != null) c.GetInventory().AddItem(prefab, n);
    }

    /// <summary>
    /// Build the full demo factory from real pieces around a point:
    ///   chest(CopperOre+Coal) -> Smelter(Cu) --\
    ///                                            Assembler(Bronze) -> Collector chest
    ///   chest(TinOre+Coal)    -> Smelter(Sn) --/
    ///   chest(Wood)           -> Kiln -> chest(Coal)     [bonus: the kiln smokes]
    /// The smelters light their fires; the assembler auto-crafts its blueprint. The
    /// collector chest is tagged "collector" for cross-client discovery.
    /// </summary>
    private static Factory? BuildFactory(Vector3 center) {
        var copperChest = Spawn(ChestPrefab, center, new Vector3(-8f, 0f, 4f), "copper");
        var copperSmelter = Spawn(SmelterPrefab, center, new Vector3(-3f, 0f, 4f), "smelter");
        var tinChest = Spawn(ChestPrefab, center, new Vector3(-8f, 0f, 0f), "tin");
        var tinSmelter = Spawn(SmelterPrefab, center, new Vector3(-3f, 0f, 0f), "tinSmelter");
        var assembler = Spawn(Blueprints.PrefabName, center, new Vector3(2f, 0f, 2f), "assembler");
        var collector = Spawn(ChestPrefab, center, new Vector3(7f, 0f, 2f), "collector");
        var woodChest = Spawn(ChestPrefab, center, new Vector3(-8f, 0f, -5f), "wood");
        var kiln = Spawn(KilnPrefab, center, new Vector3(-3f, 0f, -5f), "kiln");
        var coalChest = Spawn(ChestPrefab, center, new Vector3(2f, 0f, -5f), "coal");

        if (copperChest == null || copperSmelter == null || tinChest == null || tinSmelter == null
            || assembler == null || collector == null || woodChest == null || kiln == null || coalChest == null) {
            Plugin.Logger.LogError("BuildFactory: a piece failed to spawn");
            return null;
        }

        Fill(copperChest, "CopperOre", 20);
        Fill(copperChest, "Coal", 20);
        Fill(tinChest, "TinOre", 20);
        Fill(tinChest, "Coal", 20);
        Fill(woodChest, "Wood", 25);

        // The assembler defaults to blueprint 0 (Copper + Tin -> Bronze).
        WiringTool.Link(copperChest, copperSmelter);
        WiringTool.Link(tinChest, tinSmelter);
        WiringTool.Link(copperSmelter, assembler);
        WiringTool.Link(tinSmelter, assembler);
        WiringTool.Link(assembler, collector);
        WiringTool.Link(woodChest, kiln);
        WiringTool.Link(kiln, coalChest);

        Plugin.Logger.LogInfo("BuildFactory: 9 pieces spawned, filled and wired");
        return new Factory {
            CopperChest = copperChest, CopperSmelter = copperSmelter,
            TinChest = tinChest, TinSmelter = tinSmelter,
            Assembler = assembler, Collector = collector,
            WoodChest = woodChest, Kiln = kiln, CoalChest = coalChest,
        };
    }

    private static string Dump(Machine? m) {
        if (m == null || !m.ValidView) return "null";
        var sb = new StringBuilder();
        var port = m.Port;
        if (port != null) foreach (var kv in port.Outputs()) { if (sb.Length > 0) sb.Append(' '); sb.Append(kv.Key).Append(':').Append(kv.Value); }
        var sm = m.GetComponent<Smelter>();
        string smInfo = sm != null ? $" [queue={sm.GetQueueSize()} fuel={sm.GetFuel():F0} on={(sm.m_enabledObject != null && sm.m_enabledObject.activeSelf)}]" : "";
        return $"own={(m.IsOwner ? "Y" : "n")} out={m.View.Outputs.Count} {(sb.Length == 0 ? "-" : sb.ToString())}{smInfo}";
    }

    private static bool SmelterOn(Machine? m) {
        var sm = m != null ? m.GetComponent<Smelter>() : null;
        if (sm == null) return false;
        return (sm.m_enabledObject != null && sm.m_enabledObject.activeSelf)
               || sm.GetQueueSize() > 0 || sm.GetFuel() > 0f;
    }

    // =====================================================================
    //  SINGLE PROCESS
    // =====================================================================
    private IEnumerator RunSingleProcessFactory() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) { Record("local_player", false, "no local player"); yield break; }
        Log($"E2E: player ready: {player.GetPlayerName()}");
        yield return new WaitForSecondsRealtime(2f);

        var f = BuildFactory(player.transform.position);
        Record("factory_built", f != null, f != null ? "6 pieces wired" : "spawn failed");
        if (f == null) yield break;
        yield return new WaitForSecondsRealtime(1f);

        Log("E2E: running the factory...");
        bool smelterLit = false, kilnFed = false;
        int bronze = 0, coal = 0;
        float w = 0f, nextDump = 0f;
        while (w < 180f) {
            if (SmelterOn(f.CopperSmelter) || SmelterOn(f.TinSmelter)) smelterLit = true;
            if (f.Kiln.GetComponent<Smelter>() is { } k && k.GetQueueSize() > 0) kilnFed = true;
            bronze = f.Collector.Available("Bronze");
            coal = f.CoalChest.Available("Coal");
            if (w >= nextDump) {
                nextDump = w + 12f;
                Log($"E2E-DUMP t={w:F0}: cu[{Dump(f.CopperSmelter)}] sn[{Dump(f.TinSmelter)}] " +
                    $"asm[{Dump(f.Assembler)}] collector[{Dump(f.Collector)}] kiln[{Dump(f.Kiln)}] coal[{Dump(f.CoalChest)}]");
            }
            if (bronze >= 2 && coal >= 1) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }

        Record("smelters_light_up", smelterLit, $"smelterLit={smelterLit}");
        Record("assembler_makes_bronze", bronze >= 1, $"bronzeInCollector={bronze}");
        Record("kiln_fed_and_makes_coal", kilnFed && coal >= 1, $"kilnFed={kilnFed} coalOut={coal}");

        // Cut the assembler's output pipe; the collector must stop gaining bronze.
        int before = f.Collector.Available("Bronze");
        WiringTool.ClearOutputs(f.Assembler);
        yield return new WaitForSecondsRealtime(Mathf.Max(4f, Plugin.TickInterval * 4f));
        int mid = f.Collector.Available("Bronze");
        yield return new WaitForSecondsRealtime(Mathf.Max(4f, Plugin.TickInterval * 4f));
        int after = f.Collector.Available("Bronze");
        Record("cut_pipe_stops_flow", after == mid, $"before={before} mid={mid} after={after}");
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

        Machine? probe;
        if (E2EConfig.IsWiringScenario) {
            // A tiny graph the client reads: chest(Wood) -> chest.
            var src = Spawn(ChestPrefab, player.transform.position, new Vector3(-3f, 0f, 0f), "src");
            var dst = Spawn(ChestPrefab, player.transform.position, new Vector3(0f, 0f, 0f), "dst");
            if (src == null || dst == null) { Record("host_build", false, "spawn failed"); yield break; }
            Fill(src, "Wood", 40);
            WiringTool.Link(src, dst);
            Record("host_build", true, "src(Wood) -> dst wired");
            probe = dst;
        } else {
            var f = BuildFactory(player.transform.position);
            Record("host_build", f != null, f != null ? "factory wired" : "spawn failed");
            if (f == null) yield break;
            probe = f.Collector;
        }

        Log("E2E[host]: waiting for a client to connect...");
        float waited = 0f;
        while (waited < 180f) {
            int peers = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
            if (peers >= 1 && Player.GetAllPlayers().Count >= 2) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        int peerCount = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
        bool connected = peerCount >= 1 && Player.GetAllPlayers().Count >= 2;
        Record("host_client_connected", connected, $"peers={peerCount} players={Player.GetAllPlayers().Count}");
        if (!connected) yield break;

        // Keep the factory running while the client verifies. For wiring, prove the
        // host is transferring (dst gains wood); for flow, the client owns the
        // collector so we just keep ticking.
        var dstCheck = E2EConfig.IsWiringScenario ? Machine.FindByTag("dst") : null;
        bool hostRan = false;
        waited = 0f;
        while (waited < 150f) {
            if (E2EConfig.IsWiringScenario) { if (dstCheck != null && dstCheck.Available("Wood") > 0) hostRan = true; }
            else if (SmelterOn(Machine.FindByTag("smelter"))) hostRan = true;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("host_factory_runs", hostRan, $"hostRan={hostRan}");
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

        float waited = 0f;
        while (waited < 60f && Player.GetAllPlayers().Count < 2) { waited += Time.unscaledDeltaTime; yield return null; }
        bool sawRemote = Player.GetAllPlayers().Count >= 2;
        Record("client_sees_host", sawRemote, $"players={Player.GetAllPlayers().Count}");
        if (!sawRemote) yield break;

        if (E2EConfig.IsWiringScenario) yield return StartCoroutine(RunClientWiring());
        else yield return StartCoroutine(RunClientFlow());
    }

    /// <summary>
    /// Flow: find the host's collector chest, CLAIM it (this client now owns its
    /// ZDO), and assert Copper lands in it -- proof the host's smelter ships bars to
    /// a different client over the network.
    /// </summary>
    private IEnumerator RunClientFlow() {
        Log("E2E[client]: locating the collector chest...");
        Machine? chest = null;
        float w = 0f;
        while (w < 40f && chest == null) { chest = Machine.FindByTag("collector"); w += Time.unscaledDeltaTime; yield return null; }
        Record("client_finds_collector", chest != null, chest != null ? $"zdo={chest.ZdoId}" : "not found");
        if (chest == null) yield break;

        chest.Claim();
        w = 0f;
        while (w < 5f && !chest.IsOwner) { w += Time.unscaledDeltaTime; yield return null; }
        Record("client_owns_collector", chest.IsOwner, $"owner={chest.IsOwner}");
        if (!chest.IsOwner) yield break;

        int baseline = chest.Available("Bronze");
        Log($"E2E[client]: owns collector (baseline Bronze={baseline}); waiting for cross-client flow...");
        int seen = baseline;
        w = 0f;
        while (w < 180f) {
            seen = chest.Available("Bronze");
            if (seen - baseline >= 1) break;
            if (!chest.IsOwner) chest.Claim();
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_receives_bronze_across_network", (seen - baseline) >= 1,
            $"baseline={baseline} seen={seen} delta={seen - baseline} after={w:F0}s owner={chest.IsOwner}");
    }

    /// <summary>
    /// Wiring: assert the host's pipe (source ZDO's Outputs) and the destination
    /// chest's contents both replicate to this client and keep updating.
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
        Record("client_sees_machines", src != null && dst != null, $"src={(src != null)} dst={(dst != null)}");
        if (src == null || dst == null) yield break;

        bool pipeReplicated = false;
        w = 0f;
        while (w < 20f && !pipeReplicated) { pipeReplicated = src.View.HasOutput(dst.ZdoId); w += Time.unscaledDeltaTime; yield return null; }
        Record("client_sees_pipe", pipeReplicated, $"src.Outputs contains dst={pipeReplicated}");

        int first = dst.Available("Wood");
        yield return new WaitForSecondsRealtime(Mathf.Max(8f, Plugin.TickInterval * 6f));
        int later = dst.Available("Wood");
        Record("client_sees_buffer_growth", later > first && later > 0, $"woodInDst {first} -> {later}");
    }

    // =====================================================================
    //  MANUAL
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
        while (true) yield return null;
    }

    // =====================================================================
    //  Shared plumbing (start into a world, waits, results)
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
            if (p.GetFilename() == filename) { p.m_firstSpawn = false; p.Save(); return p; }
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
