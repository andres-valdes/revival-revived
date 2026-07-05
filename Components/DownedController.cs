using System.Collections;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The owner-authoritative brain for a downed player. Present on every Player
/// but only acts on the owner (the downed player's own client). It drives a
/// <see cref="StateMachine"/> whose states (<see cref="States.AliveState"/> /
/// <see cref="States.WaitingState"/> / <see cref="States.RevivingState"/> /
/// <see cref="States.GivingUpState"/>) hold all the per-frame logic; this class
/// is only the shared context they read/write: the ZDO view, the channel-ping
/// signal, the give-up input, the progress accumulators, and the terminal
/// actions (revive / expire).
///
/// Presentation lives in <see cref="DownedView"/> (every client); this class
/// never touches visuals.
/// </summary>
public class DownedController : MonoBehaviour {
    private Player m_player = null!;
    private ZNetView m_nview = null!;
    private readonly StateMachine m_machine = new();

    // Channel-ping signal (from revivers) and progress accumulators (seconds).
    private float m_lastPingTime = -999f;
    private long m_lastChanneler;
    private float m_reviveSeconds;
    private float m_giveUpSeconds;
    private bool m_simulateGiveUp;

    /// <summary>Max gap between channel pings before the window resumes draining and progress decays.</summary>
    public const float ChannelPingTimeout = 0.5f;
    /// <summary>How long the downed player must hold Use to give up and die early.</summary>
    public const float GiveUpDuration = 2f;

    /// <summary>
    /// The local downed player's give-up hold, 0-1 (0 when not downed / not
    /// holding). Read by <see cref="ReviveProgressUI"/> to draw the red circle.
    /// </summary>
    public static float LocalGiveUpFraction { get; private set; }

    // ---- context the states read/write -------------------------------------
    public Player Player => m_player;
    public DownedState Zdo => new(m_nview);
    public float Now => (float)ZNet.instance.GetTimeSeconds();
    public long LastChanneler => m_lastChanneler;
    public bool IsChanneling => Time.time - m_lastPingTime < ChannelPingTimeout;

    public float ReviveSeconds { get => m_reviveSeconds; set => m_reviveSeconds = value; }
    public float GiveUpSeconds { get => m_giveUpSeconds; set => m_giveUpSeconds = value; }

    // ---- unity lifecycle ---------------------------------------------------

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
        m_machine.Change(new States.AliveState(this));

        // Reviver -> owner: "I am channeling a revive." Registered on every
        // client; only the owner receives it (the RPC is owner-routed).
        m_nview.Register(DownedKeys.RpcChannel, (long sender) => {
            if (m_nview.IsOwner()) ChannelPing(sender);
        });
    }

    private void Start() {
        // The local player may have reconnected onto an orphan marker left by a
        // downed disconnect -- if so, finish the death.
        if (m_nview.IsValid() && m_nview.IsOwner()) StartCoroutine(ReconnectOrphanCheck());
    }

    private void Update() {
        if (!m_nview.IsValid() || !m_nview.IsOwner()) return; // authority is owner-only

        if (!m_player.IsDowned()) {
            if (!m_machine.Is<States.AliveState>()) {
                m_machine.Change(new States.AliveState(this));
                m_reviveSeconds = 0f;
                m_giveUpSeconds = 0f;
                LocalGiveUpFraction = 0f;
            }
            return;
        }

        // Authority invariant: keep health at 0 once the window has expired, so
        // the CheckDeath patch fires the death (a downed body can slowly regen).
        if (m_player.IsReviveWindowExpired() && m_player.GetHealth() > 0f) {
            m_player.SetHealth(0f);
        }

        m_machine.Tick();
    }

    private void OnDestroy() {
        if (m_player == Player.m_localPlayer) LocalGiveUpFraction = 0f;
    }

    // ---- signals -----------------------------------------------------------

    /// <summary>A reviver is channeling (routed ping). Records who, for the revive message.</summary>
    public void ChannelPing(long sender) {
        m_lastPingTime = Time.time;
        m_lastChanneler = sender;
    }

    /// <summary>Is the Use key held for give-up (ignoring menus/chat/console)?</summary>
    public bool GiveUpHeld() {
        if (m_simulateGiveUp) { m_simulateGiveUp = false; return true; } // test hook
        if (Chat.instance != null && Chat.instance.HasFocus()) return false;
        if (Console.IsVisible() || Menu.IsVisible() || InventoryGui.IsVisible() || TextInput.IsVisible()) return false;
        return ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");
    }

    /// <summary>Test hook: one frame of give-up input from the local downed player.</summary>
    public void SimulateGiveUpHold() => m_simulateGiveUp = true;

    /// <summary>Publish the red give-up progress (states call this while giving up).</summary>
    public void SetGiveUpFraction(float seconds) =>
        LocalGiveUpFraction = Mathf.Clamp01(seconds / GiveUpDuration);

    /// <summary>Publish the green revive progress onto our own replicated ZDO.</summary>
    public void PublishReviveProgress(float seconds) {
        var zdo = Zdo;
        zdo.ReviveProgress = Mathf.Clamp01(seconds / Plugin.ReviveDuration);
    }

    // ---- terminal actions --------------------------------------------------

    /// <summary>The channel completed: revive. Clears the downed flag (Update then rests the machine).</summary>
    public void Revive() {
        m_player.ReviveFromDowned(m_lastChanneler);
        m_reviveSeconds = 0f;
    }

    /// <summary>
    /// Give-up completed: end the window now. Expiring the clock routes through
    /// the normal CheckDeath expiry -> death path (marker becomes the real grave).
    /// </summary>
    public void ForceExpire() {
        var zdo = Zdo;
        zdo.DownedTime = Now - Plugin.ReviveWindow - 1f;
        if (m_player.GetHealth() > 0f) m_player.SetHealth(0f);
        m_giveUpSeconds = 0f;
        LocalGiveUpFraction = 0f;
        Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} gave up");
    }

    // ---- reconnect-orphan death --------------------------------------------

    /// <summary>Test hook: re-run the reconnect-orphan check now.</summary>
    public void RunReconnectCheck() => StartCoroutine(ReconnectOrphanCheck());

    private IEnumerator ReconnectOrphanCheck() {
        long pid = m_player.GetPlayerID();
        float t = 0f;
        // Poll a short while so the orphan marker has time to stream in near the
        // spawn (logout) point.
        while (t < 12f) {
            if (m_player == null || !m_nview.IsValid()) yield break;
            if (m_player.IsDowned()) yield break; // legitimately went down again

            var orphan = MarkerPrefab.FindFor(pid);
            if (orphan != null) {
                Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} reconnected with an orphaned downed marker -> dying");
                KillOnReconnect();
                yield break;
            }
            t += 0.5f;
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    /// <summary>
    /// Finish the death of a player who disconnected while downed: mark the
    /// grave-replace (the orphan hands off to the real grave) and run vanilla
    /// death directly. Owner only.
    /// </summary>
    private void KillOnReconnect() {
        if (!m_nview.IsValid() || !m_nview.IsOwner()) return;

        var state = m_player.State();
        state.Downed = false;

        var orphan = MarkerPrefab.FindFor(m_player.GetPlayerID());
        var linked = m_player.FindDownedMarker();
        var at = orphan != null ? orphan.transform.position
               : linked != null ? linked.transform.position
               : (Vector3?)null;
        if (at != null) {
            state.GraveReplacePending = true;
            state.GraveReplacePos = at.Value;
        }

        if (m_player.m_lastHit == null) {
            m_player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
        }
        m_player.SetHealth(0f);
        HarmonyLib.Traverse.Create(m_player).Method("OnDeath").GetValue();
        Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} died from being downed at disconnect");
    }

    // ---- CheckDeath delegation ---------------------------------------------

    /// <summary>
    /// Called by the CheckDeath patch on the owner when health &lt;= 0 and not
    /// dead. Returns whether vanilla death may proceed. This is the ONE place the
    /// down/expire decision is made.
    /// </summary>
    public bool HandleZeroHealth() {
        if (!m_player.IsDowned()) {
            // Enter the downed state (set flags, spawn the marker) and hand the
            // machine to WaitingState; suppress vanilla death.
            m_player.EnterDownedState();
            m_machine.Change(new States.WaitingState(this));
            return false;
        }

        if (m_player.IsReviveWindowExpired()) {
            // The window ran out: hand the spot to the real grave and let vanilla
            // OnDeath proceed.
            if (m_player.m_lastHit == null) {
                m_player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
            }
            m_player.ExpireDownedState();
            m_machine.Change(new States.AliveState(this));
            return true;
        }

        return false; // downed, window active -> suppress; the machine drives it
    }
}
