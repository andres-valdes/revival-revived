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
///            player, validates the replicated marker, and revives them.
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

        // Give the client a moment to settle, then go down.
        yield return new WaitForSecondsRealtime(2f);
        Log("E2E[host]: downing self");
        player.SetHealth(0f);
        waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.unscaledDeltaTime; yield return null; }
        Record("host_downed", DownedState.IsDowned(player), $"downed={DownedState.IsDowned(player)} dead={player.IsDead()}");
        if (!DownedState.IsDowned(player)) yield break;

        // Wait to be revived by the client (before the window expires).
        Log("E2E[host]: waiting to be revived by client...");
        waited = 0f;
        while (waited < 28f && DownedState.IsDowned(player) && !player.IsDead()) {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        bool revived = !DownedState.IsDowned(player) && !player.IsDead() && player.GetHealth() > 0f;
        Record("host_revived_by_client", revived,
            $"downed={DownedState.IsDowned(player)} dead={player.IsDead()} hp={player.GetHealth():F0}");

        // Let the client finish its assertions before we quit.
        yield return new WaitForSecondsRealtime(6f);
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
        float interactSeconds = 0f, maxUiFill = 0f;
        bool uiSeen = false;
        while (waited < 20f && DownedState.IsDowned(downed)) {
            // Discover the interactable the way the game does -- via the hover
            // raycast -- so a blocked/unhoverable marker fails this test instead
            // of being papered over by direct component access.
            var interactable = FindInteractableViaHoverRay(downed, me);
            if (interactable != null) {
                interactable.Interact(me, hold: true, alt: false);
                interactSeconds += Time.unscaledDeltaTime;
            }
            if (ReviveProgressUI.Visible) { uiSeen = true; maxUiFill = Mathf.Max(maxUiFill, ReviveProgressUI.Fill); }
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        bool revivedRemote = !DownedState.IsDowned(downed);
        Record("client_revived_remote", revivedRemote,
            $"downed={DownedState.IsDowned(downed)} channelSecs={interactSeconds:F1}");
        // The radial progress UI must have shown on the reviver while channeling.
        Record("client_progress_ui", uiSeen && maxUiFill > 0.3f, $"uiSeen={uiSeen} maxFill={maxUiFill:F2}");
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
        while (w < 6f && !DownedState.IsDowned(me)) { w += Time.unscaledDeltaTime; yield return null; }
        if (!DownedState.IsDowned(me)) { Record("rejoin_pre_down", false, "could not down self"); yield break; }
        yield return new WaitForSecondsRealtime(1f);
        long pid = me.GetPlayerID();
        bool hadMarker = DownedState.FindMarkerForPlayer(pid) != null;
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
            markerGone = DownedState.FindMarkerForPlayer(pid) == null;
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
                var nv = t.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && !nv.GetZDO().GetBool(DownedState.s_isDownedMarker)) { realTombstone = true; break; }
            }
            if (diedOnReconnect && markerGone) break;
            w += Time.unscaledDeltaTime;
            yield return null;
        }
        bool notDowned = Player.m_localPlayer == null || !DownedState.IsDowned(Player.m_localPlayer);
        Record("reconnect_downed_dies", diedOnReconnect && markerGone && notDowned,
            $"died={diedOnReconnect} markerGone={markerGone} realTombstone={realTombstone} notDowned={notDowned}");
    }

    private IEnumerator ValidateMarker(Player downed) {
        const string T = "client_marker_sync";
        GameObject? marker = DownedState.FindLinkedMarker(downed);
        if (marker == null) {
            // brief retry: the marker ZDO may still be streaming in
            float w = 0f;
            while (w < 6f && marker == null) { marker = DownedState.FindLinkedMarker(downed); w += Time.unscaledDeltaTime; yield return null; }
        }
        if (marker == null) { Record(T, false, "marker not found on client"); yield break; }

        // The replicated tombstone marker should: exist as a networked object,
        // be flagged as a downed marker, be converted to green, be near the
        // downed player, hold position steadily (no per-frame teleport), and have
        // NO ragdoll anywhere.
        var nview = marker.GetComponent<ZNetView>();
        bool isMarkerFlag = nview != null && nview.GetZDO().GetBool(DownedState.s_isDownedMarker);
        var dm = marker.GetComponent<DownedMarker>();
        bool green = dm != null && dm.IsGreen();
        bool hasInteractable = marker.GetComponentInChildren<ReviveInteractable>() != null;
        bool noTombScript = marker.GetComponent<TombStone>() == null;

        float maxPlayerDist = 0f, maxFrameJump = 0f;
        Vector3 prev = marker.transform.position;
        int samples = 0;
        float t = 0f;
        while (t < 2.5f && DownedState.IsDowned(downed)) {
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
        while (w < 5f && !DownedState.IsDowned(player)) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(3.5f);

        // 2) Channel the full revive: the progress circle fills over HoldTime.
        Log("DEMO: channeling revive");
        var rev = player.GetComponent<Revivable>();
        w = 0f;
        while (w < 12f && DownedState.IsDowned(player)) {
            rev?.ChannelRevive(0L);
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
        while (w < 5f && !DownedState.IsDowned(player)) { w += Time.unscaledDeltaTime; yield return null; }
        w = 0f;
        while (w < DownedState.ReviveWindow + 10f && !player.IsDead()) { w += Time.unscaledDeltaTime; yield return null; }
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
    }

    private IEnumerator Test_LethalDamageDowns() {
        const string T = "lethal_damage_downs";
        var player = Player.m_localPlayer;
        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.unscaledDeltaTime; yield return null; }

        // Drop-in pop: the marker is launched upward at spawn (sampled by
        // DownedState at the moment the velocity is applied -- reading the
        // rigidbody here races against gravity).
        float popVelY = DownedState.LastMarkerPopVelY;
        bool popped = popVelY > 3f;

        // Let the marker's deferred Start/convert run.
        yield return new WaitForSecondsRealtime(0.5f);
        bool downed = DownedState.IsDowned(player);
        bool notDead = !player.IsDead();
        bool hasRevivable = player.GetComponent<Revivable>() != null;
        var marker = DownedState.FindLinkedMarker(player);
        bool markerExists = marker != null;
        bool markerIsTombstone = marker != null && marker.GetComponent<ZNetView>() != null;
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;
        bool visualHidden = player.m_visual != null && !player.m_visual.activeSelf;
        bool poofPlayed = DownedState.LastPoofCount > 0;
        bool poofSmall = DownedState.LastPoofSourceName.IndexOf("Greyling", StringComparison.OrdinalIgnoreCase) >= 0;
        Record(T, downed && notDead && hasRevivable && markerExists && markerIsTombstone && noRagdolls && poofPlayed && poofSmall && popped,
            $"downed={downed} notDead={notDead} revivable={hasRevivable} marker={markerExists} " +
            $"tombstone={markerIsTombstone} noRagdolls={noRagdolls} visualHidden={visualHidden} " +
            $"poof={DownedState.LastPoofCount} poofSrc={DownedState.LastPoofSourceName} popVelY={popVelY:F1}");
    }

    /// <summary>
    /// The marker's accent lerps from green toward the grave's original red as
    /// the revive window elapses. Backdating the marker's clock by half the
    /// window must advance the blend. (Player still downed from earlier tests.)
    /// </summary>
    private IEnumerator Test_MarkerColorGradient() {
        const string T = "marker_color_gradient";
        var player = Player.m_localPlayer;
        var marker = DownedState.FindLinkedMarker(player);
        var dm = marker != null ? marker.GetComponent<DownedMarker>() : null;
        if (dm == null) { Record(T, false, "no DownedMarker"); yield break; }

        float blend0 = dm.CurrentBlend;

        // Simulate half the window having elapsed (visual clock lives on the marker ZDO).
        var mzdo = marker!.GetComponent<ZNetView>().GetZDO();
        mzdo.Set(DownedState.s_downedTime,
            mzdo.GetFloat(DownedState.s_downedTime) - DownedState.ReviveWindow * 0.5f);
        yield return null;
        yield return null;

        float blend1 = dm.CurrentBlend;
        bool startedGreen = blend0 < 0.25f;
        bool progressed = blend1 > blend0 + 0.25f;
        Record(T, startedGreen && progressed, $"blend0={blend0:F2} blend1={blend1:F2}");
    }

    private IEnumerator Test_DownedConstraints() {
        const string T = "downed_constraints";
        var player = Player.m_localPlayer;
        if (!DownedState.IsDowned(player)) { Record(T, false, "precondition: not downed"); yield break; }
        bool cannotMove = !player.CanMove();
        bool kinematic = player.m_body != null && player.m_body.isKinematic;
        var marker = DownedState.FindLinkedMarker(player);
        bool interactableFound = false, hoverOk = false, green = false, stripped = false, noEmbers = false;
        int disabledFx = 0;
        if (marker != null) {
            var interactable = marker.GetComponentInChildren<ReviveInteractable>();
            interactableFound = interactable != null;
            var dm = marker.GetComponent<DownedMarker>();
            green = dm != null && dm.IsGreen();
            disabledFx = dm != null ? dm.DisabledEffects : 0;
            stripped = marker.GetComponent<TombStone>() == null && marker.GetComponent<Container>() == null;
            // Ember particles/glow must be OFF while revivable (they are the
            // "truly dead" indicator, reserved for the real grave).
            noEmbers = disabledFx > 0
                && marker.GetComponentsInChildren<ParticleSystem>(false).Length == 0;
            if (interactable != null) {
                var hover = interactable.GetHoverText();
                hoverOk = !string.IsNullOrEmpty(hover) && hover.IndexOf("Revive", StringComparison.OrdinalIgnoreCase) >= 0;
                Log($"E2E[{T}]: hover = \"{hover.Replace("\n", " | ")}\"");
            }
        }
        Record(T, cannotMove && kinematic && interactableFound && hoverOk && green && stripped && noEmbers,
            $"cannotMove={cannotMove} kinematic={kinematic} interactable={interactableFound} hoverOk={hoverOk} " +
            $"green={green} stripped={stripped} noEmbers={noEmbers} disabledFx={disabledFx}");
    }

    /// <summary>
    /// Hold mode: channeling for ~1.5s should produce partial progress, show the
    /// radial progress UI, and then decay back to 0 (UI hidden) once released.
    /// (Player must be downed from the previous test.)
    /// </summary>
    private IEnumerator Test_HoldProgressAndUI() {
        const string T = "hold_progress_and_ui";
        var player = Player.m_localPlayer;
        if (!DownedState.IsDowned(player)) { Record(T, false, "precondition: not downed"); yield break; }
        var rev = player.GetComponent<Revivable>();
        if (rev == null) { Record(T, false, "no Revivable"); yield break; }

        // Channel for a while (well below the 4s hold time).
        float remainingBefore = DownedState.GetRemainingTime(player);
        float t = 0f, maxProg = 0f, maxFill = 0f;
        bool uiSeen = false;
        while (t < 1.5f) {
            rev.ChannelRevive(0L);
            maxProg = Mathf.Max(maxProg, DownedState.GetReviveProgress(player));
            if (ReviveProgressUI.Visible) { uiSeen = true; maxFill = Mathf.Max(maxFill, ReviveProgressUI.Fill); }
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        // The bleed-out window must PAUSE while channeling: ~1.5s elapsed but the
        // remaining time should be (nearly) unchanged.
        float remainingAfter = DownedState.GetRemainingTime(player);
        bool windowPaused = remainingBefore - remainingAfter < 0.6f;
        bool partial = maxProg > 0.05f && maxProg < 0.9f;
        bool stillDowned = DownedState.IsDowned(player);

        // Stop channeling; progress should decay and the UI hide.
        t = 0f;
        while (t < 3f) { t += Time.unscaledDeltaTime; yield return null; }
        bool decayed = DownedState.GetReviveProgress(player) <= 0.01f;
        bool uiHidden = !ReviveProgressUI.Visible;

        Record(T, partial && stillDowned && uiSeen && decayed && uiHidden && windowPaused,
            $"maxProg={maxProg:F2} partial={partial} stillDowned={stillDowned} uiSeen={uiSeen} " +
            $"maxFill={maxFill:F2} decayed={decayed} uiHidden={uiHidden} " +
            $"windowPaused={windowPaused} remBefore={remainingBefore:F1} remAfter={remainingAfter:F1}");
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
        while (w < 5f && !DownedState.IsDowned(player)) { w += Time.unscaledDeltaTime; yield return null; }
        if (!DownedState.IsDowned(player)) {
            Plugin.ReviveModeCfg.Value = ReviveModeType.Hold;
            Record(T, false, "could not down"); yield break;
        }
        yield return new WaitForSecondsRealtime(0.5f);

        // One press worth of channel input.
        var rev = player.GetComponent<Revivable>();
        rev?.ChannelRevive(0L);

        w = 0f;
        while (w < 3f && DownedState.IsDowned(player)) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(0.5f);

        bool revived = !DownedState.IsDowned(player) && !player.IsDead() && player.GetHealth() > 0f;
        bool markerGone = DownedState.FindMarkerForPlayer(player.GetPlayerID()) == null;

        Plugin.ReviveModeCfg.Value = ReviveModeType.Hold; // restore for later tests
        Record(T, revived && markerGone, $"revived={revived} hp={player.GetHealth():F0} markerGone={markerGone}");
    }

    private IEnumerator Test_ReviveRestores() {
        const string T = "revive_restores";
        var player = Player.m_localPlayer;
        if (!DownedState.IsDowned(player)) { Record(T, false, "precondition: not downed"); yield break; }
        var markerBefore = DownedState.FindLinkedMarker(player);

        DownedState.Revive(player);
        yield return new WaitForSecondsRealtime(0.5f);

        bool canMove = false;
        float waitMove = 0f;
        while (waitMove < 3f) { canMove = player.CanMove(); if (canMove) break; waitMove += Time.unscaledDeltaTime; yield return null; }

        bool notDowned = !DownedState.IsDowned(player);
        bool revivableGone = player.GetComponent<Revivable>() == null;
        bool healthy = player.GetHealth() > 0f;
        bool visualBack = player.m_visual != null && player.m_visual.activeSelf;
        bool collider = player.m_collider != null && player.m_collider.enabled;
        bool notKinematic = player.m_body != null && !player.m_body.isKinematic;
        yield return new WaitForSecondsRealtime(0.5f);
        bool markerGone = markerBefore == null || !markerBefore;

        Record(T, notDowned && revivableGone && healthy && canMove && visualBack && collider && notKinematic && markerGone,
            $"notDowned={notDowned} revivableGone={revivableGone} hp={player.GetHealth():F0} canMove={canMove} " +
            $"visualBack={visualBack} collider={collider} notKinematic={notKinematic} markerGone={markerGone}");
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
        while (w < 5f && !DownedState.IsDowned(player)) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSecondsRealtime(0.5f);
        var m1 = DownedState.FindLinkedMarker(player);
        if (m1 == null) { Record(T, false, "no marker after down"); yield break; }

        // Simulate reconnect: fresh character (no downed ZDO state, full health),
        // orphan marker left behind.
        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedState.s_downed, false);
        zdo.Set(DownedState.s_markerZDOID, ZDOID.None);
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
        bool markerGone = DownedState.FindMarkerForPlayer(player.GetPlayerID()) == null;
        bool realTombstone = false;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
            var nv = t.GetComponent<ZNetView>();
            if (nv != null && nv.IsValid() && !nv.GetZDO().GetBool(DownedState.s_isDownedMarker)) { realTombstone = true; break; }
        }
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;

        Record(T, dead && markerGone && realTombstone && noRagdolls,
            $"dead={dead} markerGone={markerGone} realTombstone={realTombstone} noRagdolls={noRagdolls}");

        // Let the vanilla respawn restore the player for any following tests.
        yield return new WaitForSecondsRealtime(0.5f);
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
        while (waited < 5f && !DownedState.IsDowned(player)) { waited += Time.unscaledDeltaTime; yield return null; }
        if (!DownedState.IsDowned(player)) { Record(T, false, "could not re-down"); yield break; }
        yield return new WaitForSecondsRealtime(0.5f);
        var markerBefore = DownedState.FindLinkedMarker(player);
        Vector3 markerPos = markerBefore != null ? markerBefore.transform.position : Vector3.zero;

        // Force the window to have expired.
        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedState.s_downedTime, (float)ZNet.instance.GetTimeSeconds() - DownedState.ReviveWindow - 5f);
        waited = 0f;
        while (waited < 10f && !player.IsDead()) { player.SetHealth(0f); waited += Time.unscaledDeltaTime; yield return null; }

        bool dead = player.IsDead();
        bool notDowned = !DownedState.IsDowned(player);
        bool markerGone = markerBefore == null || !markerBefore;
        // A real, non-marker tombstone should now exist -- standing exactly where
        // the marker stood, with no second drop-in pop (velocity ~ zero).
        bool realTombstone = false, inPlace = false, noPop = false, embersOnGrave = false;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<TombStone>()) {
            var nv = t.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || nv.GetZDO().GetBool(DownedState.s_isDownedMarker)) continue;
            realTombstone = true;
            inPlace = markerPos != Vector3.zero && Vector3.Distance(t.transform.position, markerPos) < 2f;
            var rb = t.GetComponent<Rigidbody>();
            noPop = rb == null || rb.linearVelocity.y < 1f;
            // The real grave keeps its embers/glow -- the "truly dead" signal.
            embersOnGrave = t.GetComponentsInChildren<ParticleSystem>(false).Length > 0;
            break;
        }
        bool noRagdolls = UnityEngine.Object.FindObjectsOfType<Ragdoll>().Length == 0;

        Record(T, dead && notDowned && markerGone && realTombstone && inPlace && noPop && embersOnGrave && noRagdolls,
            $"dead={dead} notDowned={notDowned} markerGone={markerGone} realTombstone={realTombstone} " +
            $"inPlace={inPlace} noPop={noPop} embersOnGrave={embersOnGrave} noRagdolls={noRagdolls}");
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
        var marker = DownedState.FindLinkedMarker(downed);
        if (marker == null) return null;
        var target = marker.transform.position + Vector3.up * 0.3f;
        var origin = target + new Vector3(1.8f, 1.2f, 0f);
        var hits = Physics.RaycastAll(origin, (target - origin).normalized, 6f, me.m_interactMask);
        if (hits.Length == 0) return null;
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        return hits[0].collider.GetComponentInParent<ReviveInteractable>();
    }

    private static Player? FindDownedRemotePlayer(Player me) {
        foreach (var p in Player.GetAllPlayers()) {
            if (p == null || p == me) continue;
            if (DownedState.IsDowned(p)) return p;
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
