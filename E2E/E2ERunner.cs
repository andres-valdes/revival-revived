using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;

namespace RevivalRevived.E2E;

/// <summary>
/// Autonomous end-to-end test harness for RevivalRevived.
///
/// Three modes, selected by RR_E2E_ROLE:
///   (unset)  single-process: downed/constraints/revive/expiry against local code.
///   host     multiplayer host: opens a CustomSocket world, downs itself, waits
///            to be revived by a connecting client.
///   client   multiplayer client: joins the host, detects the downed remote
///            player, validates ragdoll position sync, and revives them.
///
/// Bounded by a hard timeout so it can never hang a CI job.
/// </summary>
public class E2ERunner : MonoBehaviour {
    private const float HardTimeoutSeconds = 360f;

    private static string ResultPath =>
        Environment.GetEnvironmentVariable("RR_E2E_RESULTS")
        ?? Path.Combine(Paths.GameRootPath ?? ".", "e2e-results.txt");

    private readonly List<(string name, bool pass, string detail)> _results = new();
    private bool _started;
    private bool _worldStartIssued;
    private float _elapsed;

    public static void Bootstrap() {
        var go = new GameObject("RevivalRevived_E2ERunner");
        DontDestroyOnLoad(go);
        go.AddComponent<E2ERunner>();
        Plugin.Logger.LogInfo($"E2E: harness bootstrapped (role='{E2EConfig.Role}')");
    }

    private void Start() => StartCoroutine(RunAll());

    private void Update() {
        _elapsed += Time.deltaTime;
        if (_elapsed > HardTimeoutSeconds && _started) {
            Log("E2E: HARD TIMEOUT reached, aborting");
            Finish(false, "hard-timeout");
        }
    }

    private IEnumerator RunAll() {
        _started = true;
        Log($"E2E: run starting (role='{E2EConfig.Role}')");

        if (E2EConfig.IsHost) yield return StartCoroutine(RunHost());
        else if (E2EConfig.IsClient) yield return StartCoroutine(RunClient());
        else yield return StartCoroutine(RunSingleProcess());

        bool allPass = _results.Count > 0;
        foreach (var r in _results) allPass &= r.pass;
        Finish(allPass, allPass ? "all-pass" : "failures");
    }

    // =====================================================================
    //  MULTIPLAYER: HOST (the victim)
    // =====================================================================
    private IEnumerator RunHost() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) { Record("host_spawn", false, "no local player"); yield break; }
        Record("host_spawn", true, $"name={player.GetPlayerName()}");

        // Wait for the client to connect and its player to appear.
        Log("E2E[host]: waiting for a client to connect...");
        float waited = 0f;
        while (waited < 180f) {
            int peers = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
            int players = Player.GetAllPlayers().Count;
            if (peers >= 1 && players >= 2) break;
            waited += Time.deltaTime;
            yield return null;
        }
        int peerCount = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
        int playerCount = Player.GetAllPlayers().Count;
        bool connected = peerCount >= 1 && playerCount >= 2;
        Record("host_client_connected", connected, $"peers={peerCount} players={playerCount}");
        if (!connected) yield break;

        // Give the client a moment to settle, then go down.
        yield return new WaitForSeconds(2f);
        Log("E2E[host]: downing self");
        player.SetHealth(0f);
        waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.deltaTime; yield return null; }
        Record("host_downed", DownedState.IsDowned(player), $"downed={DownedState.IsDowned(player)} dead={player.IsDead()}");
        if (!DownedState.IsDowned(player)) yield break;

        // Wait to be revived by the client (before the window expires).
        Log("E2E[host]: waiting to be revived by client...");
        waited = 0f;
        while (waited < 28f && DownedState.IsDowned(player) && !player.IsDead()) {
            waited += Time.deltaTime;
            yield return null;
        }
        bool revived = !DownedState.IsDowned(player) && !player.IsDead() && player.GetHealth() > 0f;
        Record("host_revived_by_client", revived,
            $"downed={DownedState.IsDowned(player)} dead={player.IsDead()} hp={player.GetHealth():F0}");

        // Let the client finish its assertions before we quit.
        yield return new WaitForSeconds(6f);
    }

    // =====================================================================
    //  MULTIPLAYER: CLIENT (the reviver)
    // =====================================================================
    private IEnumerator RunClient() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var me = Player.m_localPlayer;
        if (me == null) { Record("client_spawn", false, "no local player"); yield break; }
        Record("client_spawn", true, $"name={me.GetPlayerName()}");

        // Confirm we see the remote host player.
        Log("E2E[client]: waiting to see the remote host player...");
        float waited = 0f;
        while (waited < 60f && Player.GetAllPlayers().Count < 2) { waited += Time.deltaTime; yield return null; }
        bool sawRemote = Player.GetAllPlayers().Count >= 2;
        Record("client_sees_host", sawRemote, $"players={Player.GetAllPlayers().Count}");
        if (!sawRemote) yield break;

        // Wait for the host to go down (replicated via ZDO).
        Log("E2E[client]: waiting for remote player to be downed...");
        Player? downed = null;
        waited = 0f;
        while (waited < 60f) {
            downed = FindDownedRemotePlayer(me);
            if (downed != null) break;
            waited += Time.deltaTime;
            yield return null;
        }
        Record("client_detected_downed", downed != null,
            downed != null ? $"name={downed.GetPlayerName()}" : "none");
        if (downed == null) yield break;

        // --- Validate server-authoritative, network-lerped ragdoll sync ------
        yield return StartCoroutine(ValidateRagdollSync(downed));

        // --- Revive the downed remote player across the network --------------
        Log("E2E[client]: channeling revive on remote player...");
        waited = 0f;
        float interactSeconds = 0f;
        while (waited < 20f && DownedState.IsDowned(downed)) {
            var ragdoll = FindLinkedRagdoll(downed);
            if (ragdoll != null) {
                var interactable = ragdoll.GetComponentInChildren<ReviveInteractable>();
                if (interactable != null) {
                    interactable.Interact(me, hold: true, alt: false);
                    interactSeconds += Time.deltaTime;
                }
            }
            waited += Time.deltaTime;
            yield return null;
        }
        bool revivedRemote = !DownedState.IsDowned(downed);
        Record("client_revived_remote", revivedRemote,
            $"downed={DownedState.IsDowned(downed)} channelSecs={interactSeconds:F1}");
    }

    private IEnumerator ValidateRagdollSync(Player downed) {
        const string T = "client_ragdoll_sync";
        var ragdoll = FindLinkedRagdoll(downed);
        if (ragdoll == null) {
            // brief retry: the ragdoll ZDO may still be streaming in
            float w = 0f;
            while (w < 5f && ragdoll == null) { ragdoll = FindLinkedRagdoll(downed); w += Time.deltaTime; yield return null; }
        }
        if (ragdoll == null) { Record(T, false, "ragdoll not found on client"); yield break; }

        // Sample over time: the ragdoll's average body position should track the
        // authoritative synced ZDO value and stay near the (also synced) player,
        // and motion should be smooth (lerped), not teleporting.
        float maxTrackErr = 0f, maxPlayerDist = 0f, maxFrameJump = 0f;
        Vector3 prev = ragdoll.GetAverageBodyPosition();
        int samples = 0;
        float t = 0f;
        while (t < 2.5f && DownedState.IsDowned(downed)) {
            var synced = ragdoll.m_nview.GetZDO().GetVec3(DownedState.s_ragdollPos, Vector3.zero);
            var avg = ragdoll.GetAverageBodyPosition();
            if (synced != Vector3.zero) maxTrackErr = Mathf.Max(maxTrackErr, Vector3.Distance(avg, synced));
            maxPlayerDist = Mathf.Max(maxPlayerDist, Vector3.Distance(avg, downed.transform.position));
            maxFrameJump = Mathf.Max(maxFrameJump, Vector3.Distance(avg, prev));
            prev = avg;
            samples++;
            t += Time.deltaTime;
            yield return null;
        }

        // Tracking error small (converges to authority), ragdoll near player,
        // and no per-frame teleport (smooth lerp).
        bool tracks = maxTrackErr < 1.0f;
        bool nearPlayer = maxPlayerDist < 4.0f;
        bool smooth = maxFrameJump < 2.0f;
        bool pass = samples > 5 && tracks && nearPlayer && smooth;
        Record(T, pass,
            $"samples={samples} maxTrackErr={maxTrackErr:F2} maxPlayerDist={maxPlayerDist:F2} maxFrameJump={maxFrameJump:F2}");
    }

    // =====================================================================
    //  SINGLE PROCESS (original suite)
    // =====================================================================
    private IEnumerator RunSingleProcess() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) { Record("local_player", false, "no local player"); yield break; }
        Log($"E2E: local player ready: {player.GetPlayerName()} hp={player.GetHealth()}/{player.GetMaxHealth()}");
        yield return new WaitForSeconds(2f);

        yield return StartCoroutine(Test_LethalDamageDowns());
        yield return StartCoroutine(Test_DownedConstraints());
        yield return StartCoroutine(Test_ReviveRestores());
        yield return StartCoroutine(Test_ExpiryKills());
    }

    private IEnumerator Test_LethalDamageDowns() {
        const string T = "lethal_damage_downs";
        var player = Player.m_localPlayer;
        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.deltaTime; yield return null; }
        bool downed = DownedState.IsDowned(player);
        bool notDead = !player.IsDead();
        bool hasRevivable = player.GetComponent<Revivable>() != null;
        bool ragdollExists = FindLinkedRagdoll(player) != null;
        Record(T, downed && notDead && hasRevivable && ragdollExists,
            $"downed={downed} notDead={notDead} revivable={hasRevivable} ragdoll={ragdollExists}");
    }

    private IEnumerator Test_DownedConstraints() {
        const string T = "downed_constraints";
        var player = Player.m_localPlayer;
        if (!DownedState.IsDowned(player)) { Record(T, false, "precondition: not downed"); yield break; }
        bool cannotMove = !player.CanMove();
        bool kinematic = player.m_body != null && player.m_body.isKinematic;
        var ragdoll = FindLinkedRagdoll(player);
        bool interactableFound = false, hoverOk = false;
        if (ragdoll != null) {
            var interactable = ragdoll.GetComponentInChildren<ReviveInteractable>();
            interactableFound = interactable != null;
            if (interactable != null) {
                var hover = interactable.GetHoverText();
                hoverOk = !string.IsNullOrEmpty(hover) && hover.IndexOf("Revive", StringComparison.OrdinalIgnoreCase) >= 0;
                Log($"E2E[{T}]: hover = \"{hover.Replace("\n", " | ")}\"");
            }
        }
        Record(T, cannotMove && kinematic && interactableFound && hoverOk,
            $"cannotMove={cannotMove} kinematic={kinematic} interactable={interactableFound} hoverOk={hoverOk}");
    }

    private IEnumerator Test_ReviveRestores() {
        const string T = "revive_restores";
        var player = Player.m_localPlayer;
        if (!DownedState.IsDowned(player)) { Record(T, false, "precondition: not downed"); yield break; }
        var ragdollBefore = FindLinkedRagdoll(player);

        DownedState.Revive(player);
        yield return new WaitForSeconds(0.5f);

        bool canMove = false;
        float waitMove = 0f;
        while (waitMove < 3f) { canMove = player.CanMove(); if (canMove) break; waitMove += Time.deltaTime; yield return null; }

        bool notDowned = !DownedState.IsDowned(player);
        bool revivableGone = player.GetComponent<Revivable>() == null;
        bool healthy = player.GetHealth() > 0f;
        bool visualBack = player.m_visual != null && player.m_visual.activeSelf;
        bool collider = player.m_collider != null && player.m_collider.enabled;
        bool notKinematic = player.m_body != null && !player.m_body.isKinematic;
        yield return new WaitForSeconds(0.5f);
        bool ragdollGone = ragdollBefore == null || !ragdollBefore;

        Record(T, notDowned && revivableGone && healthy && canMove && visualBack && collider && notKinematic && ragdollGone,
            $"notDowned={notDowned} revivableGone={revivableGone} hp={player.GetHealth():F0} canMove={canMove} " +
            $"visualBack={visualBack} collider={collider} notKinematic={notKinematic} ragdollGone={ragdollGone}");
    }

    private IEnumerator Test_ExpiryKills() {
        const string T = "expiry_kills";
        var player = Player.m_localPlayer;
        if (player.IsDead()) { Record(T, false, "already dead"); yield break; }
        player.SetHealth(player.GetMaxHealth());
        yield return null;
        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.deltaTime; yield return null; }
        if (!DownedState.IsDowned(player)) { Record(T, false, "could not re-down"); yield break; }
        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedState.s_downedTime, (float)ZNet.instance.GetTimeSeconds() - DownedState.ReviveWindow - 5f);
        waited = 0f;
        while (waited < 10f && !player.IsDead()) { player.SetHealth(0f); waited += Time.deltaTime; yield return null; }
        Record(T, player.IsDead() && !DownedState.IsDowned(player),
            $"dead={player.IsDead()} notDowned={!DownedState.IsDowned(player)}");
    }

    // =====================================================================
    //  Shared: auto-start the game into a world (role-aware)
    // =====================================================================
    private IEnumerator AutoStart() {
        Log("E2E: waiting for FejdStartup (main menu)...");
        float waited = 0f;
        while (FejdStartup.instance == null && Game.instance == null) {
            waited += Time.deltaTime;
            if (waited > 120f) { Log("E2E: FejdStartup never appeared"); yield break; }
            yield return null;
        }
        if (Game.instance != null) { Log("E2E: Game already running"); yield break; }

        yield return new WaitForSeconds(3f);
        if (_worldStartIssued) yield break;
        _worldStartIssued = true;

        Exception err = null;
        try {
            var fejd = FejdStartup.instance;

            if (E2EConfig.IsHost) {
                var profile = GetOrCreateProfile("e2e_host", "E2EHost");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var world = World.GetCreateWorld(E2EConfig.WorldName, FileHelpers.FileSource.Local);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: true, openServer: true, publicServer: false, world.m_name, "", world);
                ZNet.ResetServerHost();
                Log($"E2E[host]: world '{world.m_name}', CustomSocket port {E2EConfig.Port}, loading scene");
            } else if (E2EConfig.IsClient) {
                var profile = GetOrCreateProfile("e2e_client", "E2EClient");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: false, openServer: false, publicServer: false, "", "", null);
                ZNet.m_serverHost = E2EConfig.ServerHost;
                ZNet.m_serverHostPort = E2EConfig.Port;
                Log($"E2E[client]: connecting to {E2EConfig.ServerHost}:{E2EConfig.Port}, loading scene");
            } else {
                var profile = GetOrCreateProfile("e2e_solo", "E2ESolo");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var world = World.GetCreateWorld("e2e_world", FileHelpers.FileSource.Local);
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
            waited += Time.deltaTime;
            if (waited > 240f) { Log("E2E: timed out waiting for player spawn"); yield break; }
            yield return null;
        }
    }

    // =====================================================================
    //  Helpers
    // =====================================================================
    private static Player? FindDownedRemotePlayer(Player me) {
        foreach (var p in Player.GetAllPlayers()) {
            if (p == null || p == me) continue;
            if (DownedState.IsDowned(p)) return p;
        }
        return null;
    }

    private static Ragdoll? FindLinkedRagdoll(Player player) {
        if (player?.m_nview == null || !player.m_nview.IsValid()) return null;
        var zdo = player.m_nview.GetZDO();
        var ragdollZdoId = zdo.GetZDOID(DownedState.s_ragdollZDOID);
        if (ragdollZdoId == ZDOID.None) return null;
        var go = ZNetScene.instance.FindInstance(ragdollZdoId);
        return go != null ? go.GetComponent<Ragdoll>() : null;
    }

    private void Record(string name, bool pass, string detail) {
        _results.Add((name, pass, detail));
        Log($"E2E_RESULT: {(pass ? "PASS" : "FAIL")} {name} -- {detail}");
    }

    private static void Log(string msg) => E2ELog.Write(msg);

    private void Finish(bool ok, string reason) {
        var sb = new StringBuilder();
        sb.AppendLine($"overall={(ok ? "PASS" : "FAIL")} role={E2EConfig.Role} reason={reason}");
        foreach (var r in _results) sb.AppendLine($"{(r.pass ? "PASS" : "FAIL")}\t{r.name}\t{r.detail}");
        var text = sb.ToString();
        try { File.WriteAllText(ResultPath, text); Log($"E2E: results written to {ResultPath}"); }
        catch (Exception e) { Log("E2E: failed to write results: " + e); }
        Log("E2E_SUMMARY_BEGIN\n" + text + "E2E_SUMMARY_END");
        Log($"E2E: DONE overall={(ok ? "PASS" : "FAIL")} -- quitting");
        StartCoroutine(QuitSoon());
    }

    private IEnumerator QuitSoon() {
        yield return new WaitForSeconds(1f);
        Application.Quit();
        yield return new WaitForSeconds(2f);
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
