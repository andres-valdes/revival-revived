using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevivalRevived.E2E;

/// <summary>
/// Autonomous end-to-end test harness for RevivalRevived.
///
/// Enabled only when the environment variable <c>RR_E2E=1</c> is set. When
/// active it:
///   1. Drives FejdStartup to auto-create a single-player world (no human input).
///   2. Waits for the local player to spawn into the world.
///   3. Runs a series of scenario tests against the live mod (downed state,
///      revive, expiry-to-death) using the real game code paths.
///   4. Writes a machine-readable result file and quits the game.
///
/// The whole run is bounded by a hard timeout so it can never hang a CI job.
/// </summary>
public class E2ERunner : MonoBehaviour {
    private const string WorldName = "e2e_world";
    private const string ProfileName = "e2e_tester";
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
        Plugin.Logger.LogInfo("E2E: harness bootstrapped (RR_E2E=1)");
    }

    private void Start() {
        StartCoroutine(RunAll());
    }

    private void Update() {
        _elapsed += Time.deltaTime;
        if (_elapsed > HardTimeoutSeconds && _started) {
            Log("E2E: HARD TIMEOUT reached, aborting");
            Finish(false, "hard-timeout");
        }
    }

    private IEnumerator RunAll() {
        _started = true;
        Log("E2E: run starting");

        // --- Phase 1: get into a world ---------------------------------------
        yield return StartCoroutine(AutoStartWorld());

        // --- Phase 2: wait for the local player ------------------------------
        yield return StartCoroutine(WaitForPlayerInWorld());

        var player = Player.m_localPlayer;
        if (player == null) {
            Finish(false, "no-local-player");
            yield break;
        }
        Log($"E2E: local player ready: {player.GetPlayerName()} hp={player.GetHealth()}/{player.GetMaxHealth()}");

        // give the world/physics a moment to settle after spawn
        yield return new WaitForSeconds(2f);

        // --- Phase 3: scenario tests -----------------------------------------
        yield return StartCoroutine(Test_LethalDamageDowns());
        yield return StartCoroutine(Test_DownedConstraints());
        yield return StartCoroutine(Test_ReviveRestores());
        yield return StartCoroutine(Test_ExpiryKills());

        // --- Done ------------------------------------------------------------
        bool allPass = true;
        foreach (var r in _results) allPass &= r.pass;
        Finish(allPass, allPass ? "all-pass" : "failures");
    }

    // ---------------------------------------------------------------------
    //  Phase 1: auto-start a single-player world from the main menu.
    // ---------------------------------------------------------------------
    private IEnumerator AutoStartWorld() {
        Log("E2E: waiting for FejdStartup (main menu)...");
        float waited = 0f;
        while (FejdStartup.instance == null && Game.instance == null) {
            waited += Time.deltaTime;
            if (waited > 120f) { Log("E2E: FejdStartup never appeared"); yield break; }
            yield return null;
        }

        // Already in a game scene? (shouldn't happen on a clean boot)
        if (Game.instance != null) {
            Log("E2E: Game already running, skipping menu auto-start");
            yield break;
        }

        // Let the menu finish its own Start() (profile/world list load).
        yield return new WaitForSeconds(3f);

        if (_worldStartIssued) yield break;
        _worldStartIssued = true;

        Exception err = null;
        try {
            var fejd = FejdStartup.instance;

            // Ensure a player profile exists and is selected.
            var profiles = SaveSystem.GetAllPlayerProfiles();
            PlayerProfile profile = profiles.Count > 0 ? profiles[0] : null;
            if (profile == null) {
                Log("E2E: creating new player profile");
                profile = new PlayerProfile(ProfileName, FileHelpers.FileSource.Local);
                profile.SetName("E2ETester");
            }
            // Skip the intro/valkyrie cinematic so the player is controllable immediately.
            profile.m_firstSpawn = false;
            profile.Save();
            Game.SetProfile(profile.GetFilename(), profile.m_fileSource);
            Log($"E2E: using profile '{profile.GetFilename()}' ({profile.GetName()})");

            // Create/load the world and configure ZNet as a local host.
            var world = World.GetCreateWorld(WorldName, FileHelpers.FileSource.Local);
            ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
            ZNet.SetServer(server: true, openServer: false, publicServer: false,
                           world.m_name, "", world);
            ZNet.ResetServerHost();
            Log($"E2E: world '{world.m_name}' seed='{world.m_seedName}', starting main scene");

            // Mark the menu as "starting a world" and load the game scene.
            Traverse.Create(fejd).Field("m_startingWorld").SetValue(true);
            Traverse.Create(fejd).Method("LoadMainScene").GetValue();
        } catch (Exception e) {
            err = e;
        }
        if (err != null) Log("E2E: AutoStartWorld error: " + err);
    }

    // ---------------------------------------------------------------------
    //  Phase 2: wait until the local player has spawned into the world.
    // ---------------------------------------------------------------------
    private IEnumerator WaitForPlayerInWorld() {
        Log("E2E: waiting for player to spawn in world...");
        float waited = 0f;
        while (true) {
            var p = Player.m_localPlayer;
            if (p != null && p.GetComponent<ZNetView>() != null
                && p.GetComponent<ZNetView>().IsValid()
                && ZNetScene.instance != null) {
                yield return null;
                yield break;
            }
            waited += Time.deltaTime;
            if (waited > 240f) { Log("E2E: timed out waiting for player spawn"); yield break; }
            if (Mathf.FloorToInt(waited) % 5 == 0) {
                // periodic heartbeat
            }
            yield return null;
        }
    }

    // ---------------------------------------------------------------------
    //  Test 1: lethal damage should down the player, not kill them.
    // ---------------------------------------------------------------------
    private IEnumerator Test_LethalDamageDowns() {
        const string T = "lethal_damage_downs";
        Log($"E2E[{T}]: dropping player health to 0");
        var player = Player.m_localPlayer;

        player.SetHealth(0f);

        // Wait for Character.Update -> CheckDeath -> our prefix to run.
        float waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) {
            waited += Time.deltaTime;
            yield return null;
        }

        bool downed = DownedState.IsDowned(player);
        bool notDead = !player.IsDead();
        bool hasRevivable = player.GetComponent<Revivable>() != null;
        bool visualHidden = player.m_visual != null && !player.m_visual.activeSelf;
        bool ragdollExists = FindLinkedRagdoll(player) != null;

        bool pass = downed && notDead && hasRevivable && ragdollExists;
        Record(T, pass,
            $"downed={downed} notDead={notDead} revivable={hasRevivable} visualHidden={visualHidden} ragdoll={ragdollExists}");
        yield return null;
    }

    // ---------------------------------------------------------------------
    //  Test 2: while downed, the player cannot move and the ragdoll exposes
    //  a working revive interactable.
    // ---------------------------------------------------------------------
    private IEnumerator Test_DownedConstraints() {
        const string T = "downed_constraints";
        var player = Player.m_localPlayer;

        if (!DownedState.IsDowned(player)) {
            Record(T, false, "player not downed at start of test (precondition failed)");
            yield break;
        }

        bool cannotMove = !player.CanMove();
        bool kinematic = player.m_body != null && player.m_body.isKinematic;

        var ragdoll = FindLinkedRagdoll(player);
        bool interactableFound = false;
        bool hoverOk = false;
        if (ragdoll != null) {
            var interactable = ragdoll.GetComponentInChildren<ReviveInteractable>();
            interactableFound = interactable != null;
            if (interactable != null) {
                var hover = interactable.GetHoverText();
                hoverOk = !string.IsNullOrEmpty(hover) && hover.IndexOf("Revive", StringComparison.OrdinalIgnoreCase) >= 0;
                Log($"E2E[{T}]: hover text = \"{hover.Replace("\n", " | ")}\"");
            }
        }

        bool pass = cannotMove && kinematic && interactableFound && hoverOk;
        Record(T, pass,
            $"cannotMove={cannotMove} kinematic={kinematic} interactable={interactableFound} hoverOk={hoverOk}");
        yield return null;
    }

    // ---------------------------------------------------------------------
    //  Test 3: reviving restores the player and cleans up the ragdoll.
    // ---------------------------------------------------------------------
    private IEnumerator Test_ReviveRestores() {
        const string T = "revive_restores";
        var player = Player.m_localPlayer;

        if (!DownedState.IsDowned(player)) {
            Record(T, false, "player not downed at start of test (precondition failed)");
            yield break;
        }

        var ragdollBefore = FindLinkedRagdoll(player);

        // Drive the real revive path. (Single-process E2E: the reviver is the
        // same player object, which exercises DownedState.Revive end to end.)
        DownedState.Revive(player, player);

        yield return new WaitForSeconds(0.5f);

        bool notDowned = !DownedState.IsDowned(player);
        bool revivableGone = player.GetComponent<Revivable>() == null;
        bool healthy = player.GetHealth() > 0f;
        bool canMove = player.CanMove();
        bool visualBack = player.m_visual != null && player.m_visual.activeSelf;
        bool collider = player.m_collider != null && player.m_collider.enabled;
        bool notKinematic = player.m_body != null && !player.m_body.isKinematic;

        // Ragdoll should be destroyed (allow a frame for ZNetView.Destroy).
        yield return new WaitForSeconds(0.5f);
        bool ragdollGone = ragdollBefore == null || ragdollBefore == null || !ragdollBefore;

        bool pass = notDowned && revivableGone && healthy && canMove && visualBack && collider && notKinematic && ragdollGone;
        Record(T, pass,
            $"notDowned={notDowned} revivableGone={revivableGone} hp={player.GetHealth():F0} canMove={canMove} " +
            $"visualBack={visualBack} collider={collider} notKinematic={notKinematic} ragdollGone={ragdollGone}");
        yield return null;
    }

    // ---------------------------------------------------------------------
    //  Test 4: when the revive window expires, the player actually dies.
    // ---------------------------------------------------------------------
    private IEnumerator Test_ExpiryKills() {
        const string T = "expiry_kills";
        var player = Player.m_localPlayer;

        // Make sure we start from a healthy, non-downed state.
        if (player.IsDead()) {
            Record(T, false, "player already dead before expiry test");
            yield break;
        }
        player.SetHealth(player.GetMaxHealth());
        yield return null;

        // Down the player again.
        player.SetHealth(0f);
        float waited = 0f;
        while (waited < 5f && !DownedState.IsDowned(player)) {
            waited += Time.deltaTime;
            yield return null;
        }
        if (!DownedState.IsDowned(player)) {
            Record(T, false, "could not re-enter downed state for expiry test");
            yield break;
        }

        // Force the revive window to have already expired by backdating the ZDO.
        var zdo = player.m_nview.GetZDO();
        float past = (float)ZNet.instance.GetTimeSeconds() - DownedState.ReviveWindow - 5f;
        zdo.Set(DownedState.s_downedTime, past);
        Log($"E2E[{T}]: backdated downedTime to force expiry");

        // Keep health at 0 so CheckDeath proceeds to OnDeath once downed clears.
        waited = 0f;
        while (waited < 10f && !player.IsDead()) {
            player.SetHealth(0f);
            waited += Time.deltaTime;
            yield return null;
        }

        bool dead = player.IsDead();
        bool notDowned = !DownedState.IsDowned(player);
        bool pass = dead && notDowned;
        Record(T, pass, $"dead={dead} notDowned={notDowned}");
        yield return null;
    }

    // ---------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------
    private static Ragdoll FindLinkedRagdoll(Player player) {
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

    private static void Log(string msg) => Plugin.Logger.LogInfo(msg);

    private void Finish(bool ok, string reason) {
        var sb = new StringBuilder();
        sb.AppendLine($"overall={(ok ? "PASS" : "FAIL")} reason={reason}");
        foreach (var r in _results) {
            sb.AppendLine($"{(r.pass ? "PASS" : "FAIL")}\t{r.name}\t{r.detail}");
        }
        var text = sb.ToString();
        try {
            File.WriteAllText(ResultPath, text);
            Log($"E2E: results written to {ResultPath}");
        } catch (Exception e) {
            Log("E2E: failed to write results: " + e);
        }
        Log("E2E_SUMMARY_BEGIN\n" + text + "E2E_SUMMARY_END");
        Log($"E2E: DONE overall={(ok ? "PASS" : "FAIL")} -- quitting");

        // Quit on the next frame so the log flushes.
        StartCoroutine(QuitSoon());
    }

    private IEnumerator QuitSoon() {
        yield return new WaitForSeconds(1f);
        Application.Quit();
        // Force exit in case Application.Quit is swallowed by the player loop.
        yield return new WaitForSeconds(2f);
        System.Diagnostics.Process.GetCurrentProcess().Kill();
    }
}
