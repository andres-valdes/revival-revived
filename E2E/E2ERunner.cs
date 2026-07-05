using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using ReviveAllies.Components;
using UnityEngine;

namespace ReviveAllies.E2E;

/// <summary>
/// Autonomous end-to-end test harness for ReviveAllies.
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
        if (E2EConfig.Manual) return; // human is playing; never time out or quit
        _elapsed += Time.unscaledDeltaTime;
        if (_elapsed > HardTimeoutSeconds && _started) {
            Log("E2E: HARD TIMEOUT reached, aborting");
            Finish(false, "hard-timeout");
        }
    }

    private IEnumerator RunAll() {
        _started = true;
        Log($"E2E: run starting (role='{E2EConfig.Role}' manual={E2EConfig.Manual})");

        if (E2EConfig.Manual) { yield return StartCoroutine(RunManual()); yield break; }

        if (Environment.GetEnvironmentVariable("RR_E2E_DEMO") == "1") yield return StartCoroutine(RunDemo());
        else if (E2EConfig.IsHost) yield return StartCoroutine(RunHost());
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
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        int peerCount = ZNet.instance != null ? ZNet.instance.GetPeerConnections() : 0;
        int playerCount = Player.GetAllPlayers().Count;
        bool connected = peerCount >= 1 && playerCount >= 2;
        Record("host_client_connected", connected, $"peers={peerCount} players={playerCount}");
        if (!connected) yield break;

        // Rejoin scenario: the host is just a stable server while the client
        // downs itself, logs out, and reconnects. Idle until the client is done.
        if (E2EConfig.IsRejoinScenario) {
            Log("E2E[host]: rejoin scenario -- idling as server");
            yield return new WaitForSecondsRealtime(160f);
            Record("host_idle_complete", true, "server stayed up for client rejoin");
            yield break;
        }

        // Vanish scenario: the roles invert -- the CLIENT downs itself and logs
        // out while WE are mid-channel reviving it.
        if (E2EConfig.IsVanishScenario) {
            yield return StartCoroutine(RunHostVanish(player));
            yield break;
        }

        // Revive-loop stress: repeated down/revive cycles hunting the leaked-
        // marker race. RR_E2E_LOOP_DOWN picks which role goes down.
        if (E2EConfig.IsReviveLoopScenario) {
            if (E2EConfig.LoopDownRole == "host") yield return StartCoroutine(RunLoopVictim(player));
            else yield return StartCoroutine(RunLoopReviver(player));
            yield break;
        }

        // Give the client a moment to settle, then go down.
        yield return new WaitForSecondsRealtime(2f);
        Log("E2E[host]: downing self");
        player.SetHealth(0f);
        waited = 0f;
        while (waited < 5f && !player.IsDowned()) { waited += Time.unscaledDeltaTime; yield return null; }
        Record("host_downed", player.IsDowned(), $"downed={player.IsDowned()} dead={player.IsDead()}");
        if (!player.IsDowned()) yield break;

        // Wait to be revived by the client (before the window expires), watching
        // the peer-authoritative progress replicate in from the marker ZDO.
        Log("E2E[host]: waiting to be revived by client...");
        waited = 0f;
        float maxSeenProgress = 0f;
        while (waited < 28f && player.IsDowned() && !player.IsDead()) {
            maxSeenProgress = Mathf.Max(maxSeenProgress, player.GetReviveProgress());
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        bool revived = !player.IsDowned() && !player.IsDead() && player.GetHealth() > 0f;
        Record("host_revived_by_client", revived,
            $"downed={player.IsDowned()} dead={player.IsDead()} hp={player.GetHealth():F0}");
        // The reviver's progress must replicate to the host (more than a single
        // stray update): we should see it climb well past the halfway point.
        Record("host_saw_progress", maxSeenProgress > 0.5f, $"maxSeenProgress={maxSeenProgress:F2}");

        // Let the client finish its assertions before we quit.
        yield return new WaitForSecondsRealtime(6f);
    }

    /// <summary>
    /// Vanish scenario, host side: channel a revive on the downed client and
    /// keep holding while the client logs out. The channel must fizzle cleanly:
    /// progress decays to zero, nothing throws, no revive fires, the orphan
    /// marker persists (it is the reconnect-death evidence), and its hover text
    /// explains the disconnect.
    /// </summary>
    private IEnumerator RunHostVanish(Player me) {
        // Wait for the client to go down.
        Log("E2E[host]: vanish scenario -- waiting for client to be downed...");
        Player? downed = null;
        float w = 0f;
        while (w < 60f) {
            downed = FindDownedRemotePlayer(me);
            if (downed != null) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("vanish_client_downed", downed != null, downed != null ? $"name={downed.GetPlayerName()}" : "none");
        if (downed == null) yield break;
        long downedPid = downed.GetPlayerID();
        yield return new WaitForSecondsRealtime(1f);

        // Channel and KEEP channeling while the client vanishes mid-hold. The
        // client leaves once it sees our progress replicate (>0.25), so a revive
        // must never complete.
        Log("E2E[host]: channeling; client will log out mid-hold...");
        float maxProg = 0f;
        bool vanished = false, revived = false;
        w = 0f;
        while (w < 40f) {
            // Vanished = instance destroyed OR its ZDO released (an invalid nview
            // makes IsDowned() false and GetHealth() fall back to max, so those
            // reads are only meaningful while the nview is valid).
            if (downed == null || downed.m_nview == null || !downed.m_nview.IsValid()) { vanished = true; break; }
            if (!downed.IsDowned() && downed.GetHealth() > 0f) { revived = true; break; }
            // Direct interactable access: this scenario tests DISCONNECT
            // semantics; hover-ray reachability is covered by client_marker_sync
            // (and is terrain-dependent on the fresh world this scenario uses).
            var interactable = FindMarkerInteractable(downed);
            interactable?.Interact(me, hold: true, alt: false);
            maxProg = Mathf.Max(maxProg, downed.GetReviveProgress());
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("vanish_mid_channel", vanished && !revived && maxProg > 0.1f,
            $"vanished={vanished} revived={revived} maxProg={maxProg:F2}");
        if (!vanished) yield break;

        // Keep "holding" a moment longer against the orphan -- must not throw,
        // must not revive anyone, and the local hold must decay to zero.
        var orphan = DownedMarker.FindFor(downedPid);
        var orphanInteractable = orphan != null ? orphan.GetComponentInChildren<ReviveInteractable>() : null;
        w = 0f;
        while (w < 1.5f) {
            orphanInteractable?.Interact(me, hold: true, alt: false);
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        yield return new WaitForSecondsRealtime(3f);

        orphan = DownedMarker.FindFor(downedPid);
        bool orphanPersists = orphan != null;
        string hover = "";
        bool interactInert = true;
        if (orphan != null) {
            var inter = orphan.GetComponentInChildren<ReviveInteractable>();
            if (inter != null) {
                // The linked player is gone: interact must be a no-op (there is
                // no revive target, so no channel ping is sent to anyone).
                interactInert = !inter.Interact(me, hold: true, alt: false);
                hover = inter.GetHoverText();
            }
        }
        bool hoverExplains = hover.IndexOf("disconnected", StringComparison.OrdinalIgnoreCase) >= 0;
        bool onlyMe = Player.GetAllPlayers().Count == 1;

        Record("vanish_channel_fizzles", orphanPersists && interactInert && hoverExplains && onlyMe,
            $"orphanPersists={orphanPersists} interactInert={interactInert} " +
            $"hoverExplains={hoverExplains} hover=\"{hover.Replace("\n", " | ")}\" onlyMe={onlyMe}");
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
        while (waited < 60f && Player.GetAllPlayers().Count < 2) { waited += Time.unscaledDeltaTime; yield return null; }
        bool sawRemote = Player.GetAllPlayers().Count >= 2;
        Record("client_sees_host", sawRemote, $"players={Player.GetAllPlayers().Count}");
        if (!sawRemote) yield break;

        if (E2EConfig.IsRejoinScenario) {
            yield return StartCoroutine(RunClientRejoin(me));
            yield break;
        }

        if (E2EConfig.IsVanishScenario) {
            yield return StartCoroutine(RunClientVanish(me));
            yield break;
        }

        if (E2EConfig.IsReviveLoopScenario) {
            if (E2EConfig.LoopDownRole == "client") yield return StartCoroutine(RunLoopVictim(me));
            else yield return StartCoroutine(RunLoopReviver(me));
            yield break;
        }

        // Wait for the host to go down (replicated via ZDO).
        Log("E2E[client]: waiting for remote player to be downed...");
        Player? downed = null;
        waited = 0f;
        while (waited < 60f) {
            downed = FindDownedRemotePlayer(me);
            if (downed != null) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_detected_downed", downed != null,
            downed != null ? $"name={downed.GetPlayerName()}" : "none");
        if (downed == null) yield break;

        // --- Validate the replicated green marker (tombstone), no ragdoll ----
        yield return StartCoroutine(ValidateMarker(downed));

        // --- Revive the downed remote player across the network --------------
        Log("E2E[client]: channeling revive on remote player...");
        waited = 0f;
        float interactSeconds = 0f, maxUiFill = 0f, lastProg = 0f, worstRegression = 0f;
        bool uiSeen = false, sawFull = false, reboundAfterFull = false;
        while (waited < 20f && downed.IsDowned()) {
            // Discover the interactable the way the game does -- via the hover
            // raycast -- so a blocked/unhoverable marker fails this test instead
            // of being papered over by direct component access.
            var interactable = FindInteractableViaHoverRay(downed, me);
            if (interactable != null) {
                interactable.Interact(me, hold: true, alt: false);
                interactSeconds += Time.unscaledDeltaTime;
            }
            // Our locally-authoritative progress must NEVER glitch backwards to a
            // stale nonzero value while continuously channeling (a replicated
            // snapshot clobbering local writes shows up here).
            var prog = downed.GetReviveProgress();
            if (prog > 0.02f && prog < lastProg - 0.01f) {
                worstRegression = Mathf.Max(worstRegression, lastProg - prog);
            }
            lastProg = prog;
            // Once the hold has completed (circle full), it must STAY full for
            // the whole revive round-trip -- with real latency the old code
            // restarted the channel from zero under a still-held key, which
            // reads as the circle overflowing past 100% and re-filling.
            if (prog >= 0.99f) sawFull = true;
            else if (sawFull && prog > 0.05f && prog < 0.9f) reboundAfterFull = true;
            if (ReviveProgressUI.Visible) { uiSeen = true; maxUiFill = Mathf.Max(maxUiFill, ReviveProgressUI.Fill); }
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        bool revivedRemote = !downed.IsDowned();
        Record("client_revived_remote", revivedRemote,
            $"downed={downed.IsDowned()} channelSecs={interactSeconds:F1}");
        // The radial progress UI must have shown on the reviver while channeling,
        // never glitched backwards to stale values, and never rebuilt from zero
        // after completing.
        bool monotonic = worstRegression < 0.05f;
        Record("client_progress_ui", uiSeen && maxUiFill > 0.3f && monotonic && !reboundAfterFull,
            $"uiSeen={uiSeen} maxFill={maxUiFill:F2} worstRegression={worstRegression:F2} monotonic={monotonic} " +
            $"sawFull={sawFull} reboundAfterFull={reboundAfterFull}");

        // Regression (manual play): after the revive, the remote player must be
        // VISIBLE and solid again on this client -- the old RPC-based restore
        // raced the ZDO flag and left the player invisible forever.
        float vw = 0f;
        bool visibleAgain = false, solidAgain = false;
        while (vw < 5f && downed != null) {
            visibleAgain = downed.m_visual != null && downed.m_visual.activeSelf;
            solidAgain = downed.m_collider != null && downed.m_collider.enabled;
            if (visibleAgain && solidAgain) break;
            vw += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_host_visible_after_revive", visibleAgain && solidAgain,
            $"visible={visibleAgain} colliderOn={solidAgain} after={vw:F1}s");

        // Regression (manual play): the marker must actually be DESTROYED on
        // the reviver's client after a revive. The reviver's post-channel
        // progress writes used to keep claiming the marker ZDO and could win
        // the revision race against the owner's destroy, leaving an immortal
        // marker slowly blending to red.
        float mw = 0f;
        bool markerDestroyed = false;
        while (mw < 6f) {
            markerDestroyed = UnityEngine.Object.FindObjectsOfType<DownedMarker>().Length == 0;
            if (markerDestroyed) break;
            mw += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("client_marker_gone_after_revive", markerDestroyed,
            $"markerDestroyed={markerDestroyed} after={mw:F1}s");
    }

    /// <summary>
    /// Vanish scenario, client side: down self, wait until the HOST's channel
    /// progress replicates in (proving it is mid-hold), then log out.
    /// </summary>
    private IEnumerator RunClientVanish(Player me) {
        Log("E2E[client]: vanish scenario -- downing self");
        me.SetHealth(0f);
        float w = 0f;
        while (w < 6f && !me.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        if (!me.IsDowned()) { Record("vanish_pre_down", false, "could not down self"); yield break; }
        Record("vanish_pre_down", true, "downed=True");

        // Wait until the host is visibly mid-channel (replicated marker progress),
        // then vanish while they are still holding.
        Log("E2E[client]: waiting for host channel progress before logging out...");
        w = 0f;
        float seenProg = 0f;
        while (w < 30f && me.IsDowned()) {
            seenProg = Mathf.Max(seenProg, me.GetReviveProgress());
            if (seenProg > 0.25f) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Record("vanish_saw_host_channel", seenProg > 0.25f && me.IsDowned(), $"seenProg={seenProg:F2}");
        if (seenProg <= 0.25f) yield break;

        Log("E2E[client]: logging out mid-channel");
        Game.instance.Logout(save: true, changeToStartScene: true);
        w = 0f;
        while (w < 60f && FejdStartup.instance == null) { w += Time.unscaledDeltaTime; yield return null; }
        Record("vanish_logged_out", FejdStartup.instance != null, "back at menu");
    }

    /// <summary>
    /// Rejoin scenario: the client downs itself, logs out, and reconnects. Because
    /// a downed player can't cheat death by disconnecting, on reconnect the
    /// orphaned marker is detected and the player dies (real tombstone spawned).
    /// </summary>
    private IEnumerator RunClientRejoin(Player me) {
        // Down self (client owns its own player ZDO).
        Log("E2E[client]: downing self before logout");
        me.SetHealth(0f);
        float w = 0f;
        while (w < 6f && !me.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        if (!me.IsDowned()) { Record("rejoin_pre_down", false, "could not down self"); yield break; }
        yield return new WaitForSecondsRealtime(1f);
        long pid = me.GetPlayerID();
        bool hadMarker = DownedMarker.FindFor(pid) != null;
        Record("rejoin_pre_down", hadMarker, $"downed=True marker={hadMarker}");
        if (!hadMarker) yield break;

        // Log out to the main menu (persistent marker survives on the server).
        Log("E2E[client]: logging out...");
        Game.instance.Logout(save: true, changeToStartScene: true);

        w = 0f;
        while (w < 60f && FejdStartup.instance == null) { w += Time.unscaledDeltaTime; yield return null; }
        if (FejdStartup.instance == null) { Record("rejoin_reconnect", false, "menu did not return"); yield break; }
        Log("E2E[client]: back at menu, reconnecting...");

        // Reconnect as client.
        _worldStartIssued = false;
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());

        var me2 = Player.m_localPlayer;
        if (me2 == null) { Record("rejoin_reconnect", false, "no local player after reconnect"); yield break; }
        Record("rejoin_reconnect", true, $"name={me2.GetPlayerName()}");

        // On reconnect the DisconnectDeathCheck should find the orphan and kill us.
        Log("E2E[client]: waiting for disconnect-death on reconnect...");
        bool diedOnReconnect = false;
        bool markerGone = false;
        bool realTombstone = false;
        w = 0f;
        while (w < 20f) {
            if (Player.m_localPlayer != null && Player.m_localPlayer.IsDead()) diedOnReconnect = true;
            markerGone = DownedMarker.FindFor(pid) == null;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
                var nv = t.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && !nv.GetZDO().GetBool(DownedKeys.IsDownedMarker)) { realTombstone = true; break; }
            }
            if (diedOnReconnect && markerGone) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        bool notDowned = Player.m_localPlayer == null || !Player.m_localPlayer.IsDowned();
        Record("reconnect_downed_dies", diedOnReconnect && markerGone && notDowned,
            $"died={diedOnReconnect} markerGone={markerGone} realTombstone={realTombstone} notDowned={notDowned}");
    }

    // =====================================================================
    //  REVIVE-LOOP STRESS: hunt the leaked-marker race
    // =====================================================================
    /// <summary>Transient resurrection self-heals within ~2s; anything alive after this is a leak.</summary>
    private const float LeakGraceSeconds = 8f;

    /// <summary>
    /// All live marker ZDOs, straight from ZDOMan -- a leak can exist at the
    /// ZDO level with no local instance, so instance scans are not enough.
    /// </summary>
    private static List<ZDO> MarkerZdos() {
        var result = new List<ZDO>();
        int hash = DownedMarker.PrefabName.GetStableHashCode();
        foreach (var kv in ZDOMan.instance.m_objectsByID) {
            if (kv.Value.GetPrefab() == hash) result.Add(kv.Value);
        }
        return result;
    }

    /// <summary>
    /// Ghost marker instances: GameObjects whose ZNetView holds a ZDO that
    /// ZDOMan no longer tracks (or tracks as a DIFFERENT object after a
    /// destroy+resurrect). These are unreachable by ZDOID lookups, invisible to
    /// destroy RPCs, and live forever -- the pure ZDO scan cannot see them.
    /// </summary>
    private static List<DownedMarker> GhostMarkers() {
        var ghosts = new List<DownedMarker>();
        foreach (var dm in UnityEngine.Object.FindObjectsOfType<DownedMarker>()) {
            var nv = dm.GetComponent<ZNetView>();
            var zdoRef = nv != null ? nv.GetZDO() : null;
            var registered = zdoRef != null ? ZDOMan.instance.GetZDO(zdoRef.m_uid) : null;
            if (zdoRef == null || !ReferenceEquals(registered, zdoRef)) ghosts.Add(dm);
        }
        return ghosts;
    }

    private void DumpLeaks(string context) {
        foreach (var zdo in MarkerZdos()) {
            var playerZdoId = zdo.GetZDOID(DownedKeys.MarkerPlayer);
            var playerZdo = playerZdoId != ZDOID.None ? ZDOMan.instance.GetZDO(playerZdoId) : null;
            bool playerDowned = playerZdo != null && playerZdo.GetBool(DownedKeys.Downed);
            var instance = ZNetScene.instance.FindInstance(zdo.m_uid);
            Log($"E2E-LEAK[{context}] zdo {zdo.m_uid}: owner={zdo.GetOwner()} mine={zdo.IsOwner()} " +
                $"ownerRev={zdo.OwnerRevision} dataRev={zdo.DataRevision} " +
                $"replaced={zdo.GetBool(DownedKeys.ReplacedByGrave)} playerZdo={(playerZdo != null ? "alive" : "gone")} " +
                $"playerDowned={playerDowned} playerProg={(playerZdo != null ? playerZdo.GetFloat(DownedKeys.ReviveProgress) : 0f):F2} " +
                $"instance={(instance != null ? "yes" : "no")}");
        }
        foreach (var ghost in GhostMarkers()) {
            var nv = ghost.GetComponent<ZNetView>();
            var zdoRef = nv != null ? nv.GetZDO() : null;
            Log($"E2E-LEAK[{context}] GHOST instance at {ghost.transform.position}: " +
                $"zdoRef={(zdoRef != null ? zdoRef.m_uid.ToString() : "null")} " +
                $"registered={(zdoRef != null && ZDOMan.instance.GetZDO(zdoRef.m_uid) != null ? "different-object" : "none")}");
        }
    }

    /// <summary>Leak verdict after one cycle: wait out transients, then count.</summary>
    private int m_loopLeaks;
    private IEnumerator LeakCheck(string context) {
        float w = 0f;
        while (w < LeakGraceSeconds && (MarkerZdos().Count > 0 || GhostMarkers().Count > 0)) {
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        int zdos = MarkerZdos().Count, ghosts = GhostMarkers().Count;
        if (zdos > 0 || ghosts > 0) {
            m_loopLeaks++;
            DumpLeaks(context);
        }
        Log($"E2E loop [{context}]: zdosLeft={zdos} ghosts={ghosts} leaksSoFar={m_loopLeaks}");
    }

    /// <summary>Victim: go down over and over; the other side revives each time.</summary>
    private IEnumerator RunLoopVictim(Player me) {
        if (E2EConfig.IsExpireLoop) {
            yield return StartCoroutine(RunLoopVictimExpire());
            yield break;
        }

        int cycles = E2EConfig.LoopCycles;
        int revives = 0;
        for (int cycle = 1; cycle <= cycles; cycle++) {
            me.SetHealth(me.GetMaxHealth());
            yield return new WaitForSecondsRealtime(1.2f);

            Log($"E2E loop {cycle}: downing self");
            me.SetHealth(0f);
            float w = 0f;
            while (w < 5f && !me.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
            if (!me.IsDowned()) { Record("reviveloop_victim", false, $"cycle {cycle}: could not down"); yield break; }

            w = 0f;
            while (w < 40f && me.IsDowned() && !me.IsDead()) { w += Time.unscaledDeltaTime; yield return null; }
            if (me.IsDowned() || me.IsDead()) {
                Record("reviveloop_victim", false, $"cycle {cycle}: not revived (dead={me.IsDead()})");
                yield break;
            }
            revives++;
            yield return StartCoroutine(LeakCheck($"victim cycle {cycle}"));
        }
        Record("reviveloop_victim", revives == cycles && m_loopLeaks == 0,
            $"revives={revives}/{cycles} leakCycles={m_loopLeaks}");
        yield return new WaitForSecondsRealtime(8f); // let the reviver finish its checks
    }

    /// <summary>
    /// Expire-mode victim: die mid-channel, every cycle. As soon as the
    /// reviver's replicated progress shows an active channel, force the window
    /// to have expired -- CheckDeath then kills us WHILE the reviver's
    /// interactable is still writing to the marker ZDO, overlapping its writes
    /// with the marker-to-grave handoff (MarkReplaced / crumble).
    /// </summary>
    private IEnumerator RunLoopVictimExpire() {
        int cycles = E2EConfig.LoopCycles;
        int deaths = 0;
        for (int cycle = 1; cycle <= cycles; cycle++) {
            var me = Player.m_localPlayer; // fresh object after each respawn
            if (me == null) { Record("reviveloop_victim", false, $"cycle {cycle}: no local player"); yield break; }
            me.SetHealth(me.GetMaxHealth());
            GiveTestItem(me); // loot -> the real grave replaces the marker
            yield return new WaitForSecondsRealtime(1.2f);

            Log($"E2E loop {cycle}: downing self (expire mode)");
            me.SetHealth(0f);
            float w = 0f;
            while (w < 5f && !me.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
            if (!me.IsDowned()) { Record("reviveloop_victim", false, $"cycle {cycle}: could not down"); yield break; }

            // Wait for the reviver's channel to be live (replicated progress).
            w = 0f;
            while (w < 30f && me.GetReviveProgress() < 0.15f && me.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
            if (!me.IsDowned()) { Record("reviveloop_victim", false, $"cycle {cycle}: revived before expiry could fire"); yield break; }

            // Die mid-channel: force the window into the past.
            var zdo = me.m_nview.GetZDO();
            zdo.Set(DownedKeys.DownedTime, (float)ZNet.instance.GetTimeSeconds() - Plugin.ReviveWindow - 5f);
            w = 0f;
            while (w < 12f && !me.IsDead()) { me.SetHealth(0f); w += Time.unscaledDeltaTime; yield return null; }
            if (!me.IsDead()) { Record("reviveloop_victim", false, $"cycle {cycle}: expiry did not kill"); yield break; }
            deaths++;

            yield return StartCoroutine(LeakCheck($"victim expire cycle {cycle}"));
            yield return StartCoroutine(WaitForAlivePlayer()); // vanilla auto-respawn
        }
        Record("reviveloop_victim", deaths == cycles && m_loopLeaks == 0,
            $"deaths={deaths}/{cycles} leakCycles={m_loopLeaks}");
        yield return new WaitForSecondsRealtime(8f);
    }

    /// <summary>
    /// Reviver: channel each down with REALISTIC input -- a mid-channel release
    /// and re-grip (forces decay publishes and a fresh ownership claim), then
    /// keep holding well past the moment the revive lands, the way a human
    /// does. The scripted revive test releases the instant IsDowned flips,
    /// which is exactly why it never reproduced the leak. Cycle count is driven
    /// by the victim; we just revive whatever goes down and hunt leaks.
    /// </summary>
    private IEnumerator RunLoopReviver(Player me) {
        if (E2EConfig.IsExpireLoop) {
            yield return StartCoroutine(RunLoopReviverExpire(me));
            yield break;
        }

        int revived = 0;
        while (true) {
            Player? downed = null;
            float w = 0f;
            while (w < 30f && downed == null) {
                downed = FindDownedRemotePlayer(me);
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            if (downed == null) break; // victim finished its cycles
            yield return new WaitForSecondsRealtime(0.5f); // marker streams in

            w = 0f;
            float overshoot = 0f;
            bool jittered = false;
            while (w < 40f && overshoot < 2f) {
                // One release+re-grip mid-channel: progress decays, then the
                // re-grip claims the marker again.
                if (!jittered && downed.GetReviveProgress() > 0.45f) {
                    jittered = true;
                    yield return new WaitForSecondsRealtime(0.65f);
                    w += 0.65f;
                    continue;
                }
                var interactable = FindInteractableViaHoverRay(downed, me)
                    ?? UnityEngine.Object.FindObjectOfType<ReviveInteractable>();
                interactable?.Interact(me, hold: true, alt: false);
                if (!downed.IsDowned()) overshoot += Time.unscaledDeltaTime; // keep holding past the revive
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            if (downed.IsDowned()) { Record("reviveloop_reviver", false, "revive did not land"); yield break; }
            revived++;
            yield return StartCoroutine(LeakCheck($"reviver after revive #{revived}"));
        }
        Record("reviveloop_reviver", revived > 0 && m_loopLeaks == 0,
            $"revived={revived} leakCycles={m_loopLeaks}");
    }

    /// <summary>
    /// Expire-mode reviver: channel the downed victim continuously and keep
    /// gripping the key while it DIES mid-channel -- the interactable's decay
    /// writes then overlap the dying client's marker-to-grave handoff.
    /// </summary>
    private IEnumerator RunLoopReviverExpire(Player me) {
        int deaths = 0;
        while (true) {
            Player? downed = null;
            float w = 0f;
            while (w < 30f && downed == null) {
                downed = FindDownedRemotePlayer(me);
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            if (downed == null) break; // victim finished its cycles
            yield return new WaitForSecondsRealtime(0.4f);

            w = 0f;
            float overshoot = 0f;
            while (w < 40f && overshoot < 2.5f) {
                var interactable = FindInteractableViaHoverRay(downed, me)
                    ?? UnityEngine.Object.FindObjectOfType<ReviveInteractable>();
                interactable?.Interact(me, hold: true, alt: false);
                if (downed.IsDead() || !downed.IsDowned()) overshoot += Time.unscaledDeltaTime;
                w += Time.unscaledDeltaTime;
                yield return null;
            }
            if (!downed.IsDead()) { Record("reviveloop_reviver", false, "victim did not die mid-channel"); yield break; }
            deaths++;
            yield return StartCoroutine(LeakCheck($"reviver expire #{deaths}"));
        }
        Record("reviveloop_reviver", deaths > 0 && m_loopLeaks == 0,
            $"deaths={deaths} leakCycles={m_loopLeaks}");
    }

    private IEnumerator ValidateMarker(Player downed) {
        const string T = "client_marker_sync";
        GameObject? marker = downed.FindDownedMarker();
        if (marker == null) {
            // brief retry: the marker ZDO may still be streaming in
            float w = 0f;
            while (w < 6f && marker == null) { marker = downed.FindDownedMarker(); w += Time.unscaledDeltaTime; yield return null; }
        }
        if (marker == null) { Record(T, false, "marker not found on client"); yield break; }

        // The replicated tombstone marker should: exist as a networked object,
        // be flagged as a downed marker, be converted to green, be near the
        // downed player, hold position steadily (no per-frame teleport), and have
        // NO ragdoll anywhere.
        var nview = marker.GetComponent<ZNetView>();
        bool isMarkerFlag = nview != null && nview.IsValid() && nview.GetZDO().GetBool(DownedKeys.IsDownedMarker);
        var dm = marker.GetComponent<DownedMarker>();
        bool green = dm != null && dm.IsGreen();
        bool hasInteractable = marker.GetComponentInChildren<ReviveInteractable>() != null;
        bool noTombScript = marker.GetComponent<TombStone>() == null;

        float maxPlayerDist = 0f, maxFrameJump = 0f;
        Vector3 prev = marker.transform.position;
        int samples = 0;
        float t = 0f;
        while (t < 2.5f && downed.IsDowned()) {
            var pos = marker.transform.position;
            maxPlayerDist = Mathf.Max(maxPlayerDist, Vector3.Distance(pos, downed.transform.position));
            maxFrameJump = Mathf.Max(maxFrameJump, Vector3.Distance(pos, prev));
            prev = pos;
            samples++;
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;

        // -- Regressions found by manual play ---------------------------------
        // 1) The downed corpse must not collide on this (remote) client.
        bool corpseColliderOff = downed.m_collider == null || !downed.m_collider.enabled;
        // 2) The marker must be REACHABLE by the real hover raycast: cast through
        //    Player.m_interactMask toward the marker; the FIRST hit must resolve
        //    to the ReviveInteractable, not the invisible player corpse
        //    (FindHoverObject stops at the first hit).
        var me = Player.m_localPlayer;
        bool hoverRayHitsMarker = false, hoverBlockedByCorpse = false, hoverTextOk = false;
        var mpos = marker.transform.position + Vector3.up * 0.3f;
        var origin = mpos + new Vector3(1.8f, 1.2f, 0f);
        var hits = Physics.RaycastAll(origin, (mpos - origin).normalized, 6f, me.m_interactMask);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        if (hits.Length > 0) {
            var first = hits[0].collider;
            hoverRayHitsMarker = first.GetComponentInParent<ReviveInteractable>() != null;
            hoverBlockedByCorpse = first.GetComponentInParent<Player>() == downed;
            var inter = first.GetComponentInParent<ReviveInteractable>();
            if (inter != null) {
                var hover = inter.GetHoverText();
                hoverTextOk = !string.IsNullOrEmpty(hover) && hover.IndexOf("Revive", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
        // 3) No floating health bar for the invisible downed player.
        bool hudHidden = true;
        if (EnemyHud.instance != null) {
            var huds = Traverse.Create(EnemyHud.instance).Field("m_huds").GetValue<System.Collections.IDictionary>();
            hudHidden = huds == null || !huds.Contains(downed);
        }

        bool pass = samples > 5 && isMarkerFlag && green && hasInteractable && noTombScript
                    && maxPlayerDist < 5f && maxFrameJump < 2f && noRagdolls
                    && corpseColliderOff && hoverRayHitsMarker && !hoverBlockedByCorpse && hoverTextOk && hudHidden;
        Record(T, pass,
            $"samples={samples} markerFlag={isMarkerFlag} green={green} interactable={hasInteractable} " +
            $"noTombScript={noTombScript} maxPlayerDist={maxPlayerDist:F2} maxFrameJump={maxFrameJump:F2} noRagdolls={noRagdolls} " +
            $"corpseColliderOff={corpseColliderOff} hoverRayHitsMarker={hoverRayHitsMarker} " +
            $"hoverBlockedByCorpse={hoverBlockedByCorpse} hoverTextOk={hoverTextOk} hudHidden={hudHidden}");
    }

    // =====================================================================
    //  MANUAL (RR_E2E_MANUAL=1): auto-host/auto-join, then the human plays.
    // =====================================================================
    private IEnumerator RunManual() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());
        Log($"E2E: manual play mode ({E2EConfig.Role}) -- no tests, game is yours");
        while (true) yield return null; // idle; the human closes the game
    }

    // =====================================================================
    //  DEMO (RR_E2E_DEMO=1): slow, camera-friendly showcase for video capture.
    // =====================================================================
    private IEnumerator RunDemo() {
        yield return StartCoroutine(AutoStart());
        yield return StartCoroutine(WaitForPlayerInWorld());
        var player = Player.m_localPlayer;
        if (player == null) { Record("demo", false, "no player"); yield break; }
        Log("E2E: local player ready (demo)"); // recording script keys on this
        StartCoroutine(DespawnRavens()); // keep Hugin out of the shot
        yield return new WaitForSecondsRealtime(3f);

        // Pull the camera back for a better shot.
        if (GameCamera.instance != null) {
            GameCamera.instance.m_maxDistance = 9f;
            Traverse.Create(GameCamera.instance).Field("m_distance").SetValue(8f);
        }
        yield return new WaitForSecondsRealtime(2f);

        // 1) Go down: poof + green tombstone appears.
        Log("DEMO: downing");
        GiveTestItem(player);
        player.SetHealth(0f);
        float w = 0f;
        while (w < 5f && !player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(3.5f);

        // 2) Channel the full revive: the progress circle fills over HoldTime.
        Log("DEMO: channeling revive");
        w = 0f;
        while (w < 12f && player.IsDowned()) {
            FindMarkerInteractable(player)?.SimulateHold();
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        yield return new WaitForSecondsRealtime(3f);

        // 3) Go down again and let the window expire NATURALLY (run the demo with
        // a short RR_E2E_WINDOW so the green->red gradient and the conversion to
        // the real tombstone play out on camera).
        Log("DEMO: downing again, letting window run out");
        player.SetHealth(0f);
        w = 0f;
        while (w < 5f && !player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        w = 0f;
        while (w < Plugin.ReviveWindow + 10f && !player.IsDead()) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(5f);

        Record("demo", true, "sequence complete");
    }

    /// <summary>Continuously despawn tutorial ravens so they don't photobomb the demo.</summary>
    private IEnumerator DespawnRavens() {
        while (true) {
            foreach (var raven in UnityEngine.Object.FindObjectsOfType<Raven>()) {
                if (raven != null && ZNetScene.instance != null) {
                    ZNetScene.instance.Destroy(raven.gameObject);
                }
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
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
        yield return new WaitForSecondsRealtime(2f);

        yield return StartCoroutine(Test_LethalDamageDowns());
        yield return StartCoroutine(Test_DownedConstraints());
        yield return StartCoroutine(Test_MarkerColorGradient());
        yield return StartCoroutine(Test_HoldProgressAndUI());
        yield return StartCoroutine(Test_ReviveRestores());
        yield return StartCoroutine(Test_PressModeRevives());
        yield return StartCoroutine(Test_DisconnectDeath());
        yield return StartCoroutine(WaitForAlivePlayer());
        yield return StartCoroutine(Test_ExpiryKills());
        yield return StartCoroutine(WaitForAlivePlayer());
        yield return StartCoroutine(Test_EmptyInventoryCrumbles());
    }

    private IEnumerator Test_LethalDamageDowns() {
        const string T = "lethal_damage_downs";
        var player = Player.m_localPlayer;
        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !player.IsDowned()) { waited += Time.unscaledDeltaTime; yield return null; }

        // Drop-in pop: the marker is launched upward at spawn (sampled by
        // DownedState at the moment the velocity is applied -- reading the
        // rigidbody here races against gravity).
        float popVelY = DownedMarker.LastPopVelY;
        bool popped = popVelY > 3f;

        // Let the marker's deferred Start/convert run.
        yield return new WaitForSecondsRealtime(0.5f);
        bool downed = player.IsDowned();
        bool notDead = !player.IsDead();
        bool hasRevivable = player.GetComponent<Revivable>() != null;
        var marker = player.FindDownedMarker();
        bool markerExists = marker != null;
        // The marker must be an instance of OUR registered prefab (not a
        // converted tombstone) with a valid network view.
        bool markerIsOurPrefab = marker != null && marker.GetComponent<ZNetView>() != null
            && marker.name.StartsWith(DownedMarker.PrefabName)
            && marker.GetComponent<TombStone>() == null;
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;
        bool visualHidden = player.m_visual != null && !player.m_visual.activeSelf;
        bool poofPlayed = PlayerDownedExtensions.LastPoofCount > 0;
        bool poofSmall = PlayerDownedExtensions.LastPoofSourceName.IndexOf("Greyling", StringComparison.OrdinalIgnoreCase) >= 0;
        Record(T, downed && notDead && hasRevivable && markerExists && markerIsOurPrefab && noRagdolls && poofPlayed && poofSmall && popped,
            $"downed={downed} notDead={notDead} revivable={hasRevivable} marker={markerExists} " +
            $"ourPrefab={markerIsOurPrefab} noRagdolls={noRagdolls} visualHidden={visualHidden} " +
            $"poof={PlayerDownedExtensions.LastPoofCount} poofSrc={PlayerDownedExtensions.LastPoofSourceName} popVelY={popVelY:F1}");
    }

    /// <summary>
    /// The marker's accent lerps from green toward the grave's original red as
    /// the revive window elapses. Backdating the marker's clock by half the
    /// window must advance the blend. (Player still downed from earlier tests.)
    /// </summary>
    private IEnumerator Test_MarkerColorGradient() {
        const string T = "marker_color_gradient";
        var player = Player.m_localPlayer;
        var marker = player.FindDownedMarker();
        var dm = marker != null ? marker.GetComponent<DownedMarker>() : null;
        if (dm == null) { Record(T, false, "no DownedMarker"); yield break; }

        float blend0 = dm.CurrentBlend;

        // Simulate half the window having elapsed. The gradient clock is the
        // PLAYER's downedTime (single-writer: the marker ZDO belongs to the
        // channeling reviver's progress only).
        var pzdo = player.m_nview.GetZDO();
        pzdo.Set(DownedKeys.DownedTime, pzdo.GetFloat(DownedKeys.DownedTime) - Plugin.ReviveWindow * 0.5f);
        yield return null;
        yield return null;

        float blend1 = dm.CurrentBlend;
        bool startedGreen = blend0 < 0.25f;
        bool progressed = blend1 > blend0 + 0.25f;
        Record(T, startedGreen && progressed, $"blend0={blend0:F2} blend1={blend1:F2}");
    }

    /// <summary>
    /// Verifies "downed + disconnect = death" via the reconnect path, without a
    /// full reconnect: down the player, then simulate the reconnected fresh
    /// character (downed ZDO state gone, health clamped back to full by vanilla
    /// Load) while the persistent green marker survives as an orphan. The
    /// DisconnectDeathCheck should find the orphan and complete the death: real
    /// tombstone spawned, green marker gone, player dead, no ragdoll.
    /// </summary>
    private IEnumerator Test_DisconnectDeath() {
        const string T = "disconnect_kills_on_reconnect";
        var player = Player.m_localPlayer;
        player.SetHealth(player.GetMaxHealth());
        GiveTestItem(player); // so the death grave has loot
        yield return null;

        // Down once -> green marker.
        player.SetHealth(0f);
        float w = 0f;
        while (w < 5f && !player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(0.5f);
        var m1 = player.FindDownedMarker();
        if (m1 == null) { Record(T, false, "no marker after down"); yield break; }

        // Simulate reconnect: fresh character (no downed ZDO state, full health),
        // orphan marker left behind.
        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedKeys.Downed, false);
        zdo.Set(DownedKeys.Marker, ZDOID.None);
        var rev = player.GetComponent<Revivable>();
        if (rev != null) UnityEngine.Object.Destroy(rev);
        player.SetHealth(player.GetMaxHealth());
        player.m_collider.enabled = true;
        player.m_body.isKinematic = false;
        if (player.m_visual != null) player.m_visual.SetActive(true);
        yield return null;

        // Run the reconnect check (as PlayerOnSpawnedPatch would).
        player.gameObject.AddComponent<DisconnectDeathCheck>();

        // Wait for it to find the orphan and kill the player.
        w = 0f;
        while (w < 8f && !player.IsDead()) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(0.5f);

        bool dead = player.IsDead();
        bool markerGone = DownedMarker.FindFor(player.GetPlayerID()) == null;
        bool realTombstone = false;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
            var nv = t.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && !nv.GetZDO().GetBool(DownedKeys.IsDownedMarker)) { realTombstone = true; break; }
        }
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;

        Record(T, dead && markerGone && realTombstone && noRagdolls,
            $"dead={dead} markerGone={markerGone} realTombstone={realTombstone} noRagdolls={noRagdolls}");

        // Let the vanilla respawn restore the player for any following tests.
        yield return new WaitForSecondsRealtime(0.5f);
    }

    private IEnumerator Test_DownedConstraints() {
        const string T = "downed_constraints";
        var player = Player.m_localPlayer;
        if (!player.IsDowned()) { Record(T, false, "precondition: not downed"); yield break; }
        bool cannotMove = !player.CanMove();
        bool kinematic = player.m_body != null && player.m_body.isKinematic;
        var marker = player.FindDownedMarker();
        bool interactableFound = false, hoverOk = false, green = false, stripped = false, noEmbers = false, nameOk = false;
        int disabledFx = 0;
        if (marker != null) {
            // The floating world text must show the player's name (vanilla grave
            // behaviour), not the prefab default "GRAVE".
            var worldText = marker.GetComponentInChildren<TMPro.TMP_Text>(true);
            nameOk = worldText != null && worldText.text == player.GetPlayerName();
            var interactable = marker.GetComponentInChildren<ReviveInteractable>();
            interactableFound = interactable != null;
            var dm = marker.GetComponent<DownedMarker>();
            green = dm != null && dm.IsGreen();
            disabledFx = DownedMarker.TemplateEffectsRemoved;
            stripped = marker.GetComponent<TombStone>() == null && marker.GetComponent<Container>() == null;
            // The marker prefab simply HAS no ember/glow objects (deleted from
            // the template at build time) -- they are the "truly dead" indicator,
            // reserved for the real grave.
            noEmbers = disabledFx > 0
                && marker.GetComponentsInChildren<ParticleSystem>(true).Length == 0;
            if (interactable != null) {
                var hover = interactable.GetHoverText();
                hoverOk = !string.IsNullOrEmpty(hover) && hover.IndexOf("Revive", StringComparison.OrdinalIgnoreCase) >= 0;
                Log($"E2E[{T}]: hover = \"{hover.Replace("\n", " | ")}\"");
            }
        }
        Record(T, cannotMove && kinematic && interactableFound && hoverOk && green && stripped && noEmbers && nameOk,
            $"cannotMove={cannotMove} kinematic={kinematic} interactable={interactableFound} hoverOk={hoverOk} " +
            $"green={green} stripped={stripped} noEmbers={noEmbers} disabledFx={disabledFx} nameOk={nameOk}");
    }

    /// <summary>
    /// Hold mode: channeling for ~1.5s should produce partial progress, show the
    /// radial progress UI, and then decay back to 0 (UI hidden) once released.
    /// (Player must be downed from the previous test.)
    /// </summary>
    private IEnumerator Test_HoldProgressAndUI() {
        const string T = "hold_progress_and_ui";
        var player = Player.m_localPlayer;
        if (!player.IsDowned()) { Record(T, false, "precondition: not downed"); yield break; }
        var interactable = FindMarkerInteractable(player);
        if (interactable == null) { Record(T, false, "no marker interactable"); yield break; }

        // Channel for a while (well below the 4s hold time). The timer is
        // peer-authoritative: it lives in the marker's ReviveInteractable on the
        // channeling client, so progress must respond with no replication lag.
        float remainingBefore = player.GetDownedRemainingTime();
        float t = 0f, maxProg = 0f, maxFill = 0f, firstProgressAt = -1f;
        bool uiSeen = false;
        while (t < 1.5f) {
            interactable.SimulateHold();
            var prog = player.GetReviveProgress();
            if (prog > 0.05f && firstProgressAt < 0f) firstProgressAt = t;
            maxProg = Mathf.Max(maxProg, prog);
            if (ReviveProgressUI.Visible) { uiSeen = true; maxFill = Mathf.Max(maxFill, ReviveProgressUI.Fill); }
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        bool responsive = firstProgressAt >= 0f && firstProgressAt < 0.6f;
        // The bleed-out window must PAUSE while channeling: ~1.5s elapsed but the
        // remaining time should be (nearly) unchanged.
        float remainingAfter = player.GetDownedRemainingTime();
        bool windowPaused = remainingBefore - remainingAfter < 0.6f;
        bool partial = maxProg > 0.05f && maxProg < 0.9f;
        bool stillDowned = player.IsDowned();

        // Stop channeling; progress should decay and the UI hide.
        t = 0f;
        while (t < 3f) { t += Time.unscaledDeltaTime; yield return null; }
        bool decayed = player.GetReviveProgress() <= 0.01f;
        bool uiHidden = !ReviveProgressUI.Visible;

        Record(T, partial && stillDowned && uiSeen && decayed && uiHidden && windowPaused && responsive,
            $"maxProg={maxProg:F2} partial={partial} stillDowned={stillDowned} uiSeen={uiSeen} " +
            $"maxFill={maxFill:F2} decayed={decayed} uiHidden={uiHidden} " +
            $"windowPaused={windowPaused} remBefore={remainingBefore:F1} remAfter={remainingAfter:F1} " +
            $"responsive={responsive} firstProgressAt={firstProgressAt:F2}");
    }

    /// <summary>
    /// Press mode (config): a single channel tick (as a single press produces)
    /// revives immediately -- no hold required.
    /// </summary>
    private IEnumerator Test_PressModeRevives() {
        const string T = "press_mode_revives";
        var player = Player.m_localPlayer;
        Plugin.ReviveModeCfg.Value = ReviveModeType.Press;

        player.SetHealth(player.GetMaxHealth());
        yield return null;
        player.SetHealth(0f);
        float w = 0f;
        while (w < 5f && !player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        if (!player.IsDowned()) {
            Plugin.ReviveModeCfg.Value = ReviveModeType.Hold;
            Record(T, false, "could not down"); yield break;
        }
        yield return new WaitForSecondsRealtime(0.5f);

        // One press worth of channel input (press mode fires DoRevive from the
        // interactable's next Update).
        FindMarkerInteractable(player)?.SimulateHold();

        w = 0f;
        while (w < 3f && player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(0.5f);

        bool revived = !player.IsDowned() && !player.IsDead() && player.GetHealth() > 0f;
        bool markerGone = DownedMarker.FindFor(player.GetPlayerID()) == null;

        Plugin.ReviveModeCfg.Value = ReviveModeType.Hold; // restore for later tests
        Record(T, revived && markerGone, $"revived={revived} hp={player.GetHealth():F0} markerGone={markerGone}");
    }

    private IEnumerator Test_ReviveRestores() {
        const string T = "revive_restores";
        var player = Player.m_localPlayer;
        if (!player.IsDowned()) { Record(T, false, "precondition: not downed"); yield break; }
        var markerBefore = player.FindDownedMarker();
        int crumblesBefore = DownedMarker.CrumbleEvents;

        player.ReviveFromDowned();
        yield return new WaitForSecondsRealtime(0.5f);

        bool canMove = false;
        float waitMove = 0f;
        while (waitMove < 3f) { canMove = player.CanMove(); if (canMove) break; waitMove += Time.unscaledDeltaTime; yield return null; }

        bool notDowned = !player.IsDowned();
        bool revivableGone = player.GetComponent<Revivable>() == null;
        bool healthy = player.GetHealth() > 0f;
        bool visualBack = player.m_visual != null && player.m_visual.activeSelf;
        bool collider = player.m_collider != null && player.m_collider.enabled;
        bool notKinematic = player.m_body != null && !player.m_body.isKinematic;
        yield return new WaitForSecondsRealtime(0.5f);
        bool markerGone = markerBefore == null || !markerBefore;
        // The marker must CRUMBLE away (grave despawn effect), not blink out.
        bool crumbled = DownedMarker.CrumbleEvents > crumblesBefore;

        Record(T, notDowned && revivableGone && healthy && canMove && visualBack && collider && notKinematic && markerGone && crumbled,
            $"notDowned={notDowned} revivableGone={revivableGone} hp={player.GetHealth():F0} canMove={canMove} " +
            $"visualBack={visualBack} collider={collider} notKinematic={notKinematic} markerGone={markerGone} crumbled={crumbled}");
    }

    private IEnumerator Test_ExpiryKills() {
        const string T = "expiry_kills_real_tombstone";
        var player = Player.m_localPlayer;
        if (player.IsDead()) { Record(T, false, "already dead"); yield break; }
        player.SetHealth(player.GetMaxHealth());

        // Give the player an item so vanilla OnDeath spawns a real (functional) tombstone.
        GiveTestItem(player);
        yield return null;

        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !player.IsDowned()) { waited += Time.unscaledDeltaTime; yield return null; }
        if (!player.IsDowned()) { Record(T, false, "could not re-down"); yield break; }
        yield return new WaitForSecondsRealtime(0.5f);
        var markerBefore = player.FindDownedMarker();
        Vector3 markerPos = markerBefore != null ? markerBefore.transform.position : Vector3.zero;

        // Force the window to have expired.
        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedKeys.DownedTime, (float)ZNet.instance.GetTimeSeconds() - Plugin.ReviveWindow - 5f);
        waited = 0f;
        while (waited < 10f && !player.IsDead()) { player.SetHealth(0f); waited += Time.unscaledDeltaTime; yield return null; }

        bool dead = player.IsDead();
        bool notDowned = !player.IsDowned();
        // The corpse lingers invisibly until respawn (no ragdoll); it must be
        // inert -- nothing to bump into.
        bool corpseInert = dead && player.m_collider != null && !player.m_collider.enabled;

        // Gap-free handoff: the marker is NOT destroyed at death -- it hides
        // once the real grave is visible here and its ZDO is destroyed after a
        // grace period. On no frame may the spot be empty: the marker must stay
        // visible until a grave stands within its handoff radius.
        bool gapless = true;
        bool markerGone = markerBefore == null || !markerBefore;
        float hw = 0f;
        while (hw < 9f && !markerGone) {
            bool visible = false;
            foreach (var r in markerBefore!.GetComponentsInChildren<Renderer>()) {
                if (r.enabled) { visible = true; break; }
            }
            if (!visible && FindRealGraveNear(markerPos, 3f) == null) gapless = false;
            hw += Time.unscaledDeltaTime;
            yield return null;
            markerGone = markerBefore == null || !markerBefore;
        }

        // A real, non-marker tombstone should now exist -- standing exactly where
        // the marker stood, with no second drop-in pop (velocity ~ zero).
        bool realTombstone = false, inPlace = false, noPop = false, embersOnGrave = false;
        var grave = FindRealGraveNear(markerPos, 999f);
        if (grave != null) {
            realTombstone = true;
            inPlace = markerPos != Vector3.zero && Vector3.Distance(grave.transform.position, markerPos) < 2f;
            var rb = grave.GetComponent<Rigidbody>();
            noPop = rb == null || rb.linearVelocity.y < 1f;
            // The real grave keeps its embers/glow -- the "truly dead" signal.
            embersOnGrave = grave.GetComponentsInChildren<ParticleSystem>(false).Length > 0;
        }
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;

        Record(T, dead && notDowned && corpseInert && markerGone && gapless && realTombstone && inPlace && noPop && embersOnGrave && noRagdolls,
            $"dead={dead} notDowned={notDowned} corpseInert={corpseInert} markerGone={markerGone} gapless={gapless} " +
            $"realTombstone={realTombstone} inPlace={inPlace} noPop={noPop} embersOnGrave={embersOnGrave} noRagdolls={noRagdolls}");
    }

    /// <summary>Nearest real (non-marker) tombstone within maxDist of pos, or null.</summary>
    private static TombStone? FindRealGraveNear(Vector3 pos, float maxDist) {
        TombStone? best = null;
        float bestDist = maxDist;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
            var nv = t.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) continue;
            if (nv != null && nv.IsValid() && nv.GetZDO().GetBool(DownedKeys.IsDownedMarker)) continue;
            float d = Vector3.Distance(t.transform.position, pos);
            if (d <= bestDist) { bestDist = d; best = t; }
        }
        return best;
    }

    /// <summary>
    /// A downed player who dies with an empty inventory gets no grave at all
    /// (vanilla CreateTombStone requires items) -- the marker must not simply
    /// vanish: it despawns the way an emptied grave does, crumble effect and all.
    /// </summary>
    private IEnumerator Test_EmptyInventoryCrumbles() {
        const string T = "empty_death_crumbles_marker";
        var player = Player.m_localPlayer;
        if (player == null || player.IsDead()) { Record(T, false, "no alive player"); yield break; }
        player.SetHealth(player.GetMaxHealth());
        player.GetInventory().RemoveAll(); // nothing to drop -> vanilla spawns no grave
        yield return null;

        player.SetHealth(0f);
        float w = 0f;
        while (w < 5f && !player.IsDowned()) { w += Time.unscaledDeltaTime; yield return null; }
        if (!player.IsDowned()) { Record(T, false, "could not down"); yield break; }
        yield return new WaitForSecondsRealtime(0.5f);
        var marker = player.FindDownedMarker();
        if (marker == null) { Record(T, false, "no marker"); yield break; }
        Vector3 markerPos = marker.transform.position;
        int gravesBefore = CountRealGraves();

        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedKeys.DownedTime, (float)ZNet.instance.GetTimeSeconds() - Plugin.ReviveWindow - 5f);
        w = 0f;
        while (w < 10f && !player.IsDead()) { player.SetHealth(0f); w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(1f);

        bool dead = player.IsDead();
        bool markerGone = marker == null || !marker;
        bool crumbled = DownedMarker.LastCrumbleEffectCount > 0;
        bool noNewGrave = CountRealGraves() == gravesBefore;
        bool pendingCleared = !player.m_nview.GetZDO().GetBool(DownedKeys.GraveReplacePending);

        Record(T, dead && markerGone && crumbled && noNewGrave && pendingCleared,
            $"dead={dead} markerGone={markerGone} crumbleEffects={DownedMarker.LastCrumbleEffectCount} " +
            $"noNewGrave={noNewGrave} (before={gravesBefore}) pendingCleared={pendingCleared}");
    }

    private static int CountRealGraves() {
        int n = 0;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
            var nv = t.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) continue;
            if (nv != null && nv.IsValid() && nv.GetZDO().GetBool(DownedKeys.IsDownedMarker)) continue;
            n++;
        }
        return n;
    }

    private static void GiveTestItem(Player player) {
        try {
            if (ObjectDB.instance == null) return;
            var prefab = ObjectDB.instance.GetItemPrefab("Wood");
            if (prefab == null) return;
            player.GetInventory().AddItem(prefab, 1);
        } catch (Exception e) {
            Log("E2E: GiveTestItem failed: " + e.Message);
        }
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

        Exception err = null;
        try {
            var fejd = FejdStartup.instance;

            if (E2EConfig.IsHost) {
                var profile = GetOrCreateProfile(
                    Environment.GetEnvironmentVariable("RR_E2E_PROFILE") ?? "e2e_host",
                    Environment.GetEnvironmentVariable("RR_E2E_CHARNAME") ?? "E2EHost");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var world = World.GetCreateWorld(E2EConfig.WorldName, FileHelpers.FileSource.Local);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: true, openServer: true, publicServer: false, world.m_name, "", world);
                ZNet.ResetServerHost();
                Log($"E2E[host]: world '{world.m_name}', CustomSocket port {E2EConfig.Port}, loading scene");
            } else if (E2EConfig.IsClient) {
                var profile = GetOrCreateProfile(
                    Environment.GetEnvironmentVariable("RR_E2E_PROFILE") ?? "e2e_client",
                    Environment.GetEnvironmentVariable("RR_E2E_CHARNAME") ?? "E2EClient");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                ZNet.m_onlineBackend = OnlineBackendType.CustomSocket;
                ZNet.SetServer(server: false, openServer: false, publicServer: false, "", "", null);
                ZNet.m_serverHost = E2EConfig.ServerHost;
                ZNet.m_serverHostPort = E2EConfig.Port;
                Log($"E2E[client]: connecting to {E2EConfig.ServerHost}:{E2EConfig.Port}, loading scene");
            } else {
                var profile = GetOrCreateProfile("e2e_solo", "E2ESolo");
                Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
                var soloWorldName = Environment.GetEnvironmentVariable("RR_E2E_WORLD") ?? "e2e_world";
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

    /// <summary>Wait until the local player is alive again (after a death/respawn).</summary>
    private IEnumerator WaitForAlivePlayer() {
        float w = 0f;
        while (w < 30f) {
            var p = Player.m_localPlayer;
            if (p != null && p.m_nview != null && p.m_nview.IsValid() && !p.IsDead() && p.GetHealth() > 0f) {
                yield return new WaitForSecondsRealtime(0.5f);
                yield break;
            }
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        Log("E2E: WaitForAlivePlayer timed out");
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
    //  Helpers
    // =====================================================================
    /// <summary>
    /// Locate the downed player's revive interactable the way the real game
    /// does: a raycast through Player.m_interactMask toward the marker, taking
    /// the FIRST hit (Player.FindHoverObject stops at the first hit, so anything
    /// blocking -- like a still-active corpse collider -- makes this return null).
    /// </summary>
    private static ReviveInteractable? FindInteractableViaHoverRay(Player downed, Player me) {
        var marker = downed.FindDownedMarker();
        if (marker == null) return null;
        var target = marker.transform.position + Vector3.up * 0.3f;
        var origin = target + new Vector3(1.8f, 1.2f, 0f);
        var hits = Physics.RaycastAll(origin, (target - origin).normalized, 6f, me.m_interactMask);
        if (hits.Length == 0) return null;
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        return hits[0].collider.GetComponentInParent<ReviveInteractable>();
    }

    /// <summary>Marker interactable for a downed player (single-process tests/demo).</summary>
    private static ReviveInteractable? FindMarkerInteractable(Player downed) {
        var marker = downed.FindDownedMarker();
        return marker != null ? marker.GetComponentInChildren<ReviveInteractable>() : null;
    }

    private static Player? FindDownedRemotePlayer(Player me) {
        foreach (var p in Player.GetAllPlayers()) {
            if (p == null || p == me) continue;
            if (p.IsDowned()) return p;
        }
        return null;
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
        yield return new WaitForSecondsRealtime(1f);
        Application.Quit();
        yield return new WaitForSecondsRealtime(2f);
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
