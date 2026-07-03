using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Present on a downed Player on <em>every</em> client, and the single place
/// that manages the downed player's local presentation: while the replicated
/// <c>s_downed</c> flag is set it hides the visual, disables the collider (so
/// the invisible corpse neither blocks movement nor eats the hover raycast) and
/// freezes the owner's body; when the flag clears -- revived or expired,
/// observed via ZDO on ANY client, no RPC ordering races -- it restores what it
/// changed and destroys itself.
///
/// Lifecycle is ZDO-driven: a slim LateUpdate patch attaches this component to
/// any player whose ZDO says downed; teardown is entirely local.
///
/// Authority split:
///   - The revive *timer* is peer-authoritative: the reviver's
///     <see cref="ReviveInteractable"/> accumulates the hold locally and sends
///     <see cref="DownedState.RPC_DoRevive"/> when complete.
///   - The owner only enforces the bleed-out window (expiry -> death) and
///     pauses it while channel pings (<see cref="DownedState.RPC_Channel"/>)
///     are arriving.
/// </summary>
public class Revivable : MonoBehaviour {
    private Player? m_player;
    private ZNetView? m_nview;
    private float m_lastChannelTime = -999f;

    /// <summary>Max gap between channel pings before the window resumes draining.</summary>
    private const float ChannelPingTimeout = 0.7f;

    public string PlayerName => m_player != null ? m_player.GetPlayerName() : "Viking";
    public float RemainingTime => m_player != null ? DownedState.GetRemainingTime(m_player) : 0f;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid() || m_player == null) return;

        // ZDO-driven teardown: the flag cleared (revive or expiry), restore this
        // client's local changes and go away.
        if (!DownedState.IsDowned(m_player)) {
            Restore();
            Destroy(this);
            return;
        }

        // --- Downed: enforce presentation on every client -------------------
        if (m_player.m_visual != null && m_player.m_visual.activeSelf) {
            m_player.m_visual.SetActive(false);
        }
        if (m_player.m_collider != null && m_player.m_collider.enabled) {
            m_player.m_collider.enabled = false;
        }

        // --- Owner-only: window enforcement ---------------------------------
        if (!m_nview.IsOwner()) return;

        m_player.m_body.isKinematic = true;

        // Expiry-to-death is owned by the CheckDeath patch; keep health at 0 so
        // it fires once the window has elapsed.
        if (DownedState.IsReviveWindowExpired(m_player)) {
            if (m_player.GetHealth() > 0f) m_player.SetHealth(0f);
            return;
        }

        // Pause the bleed-out window while a reviver is actively channeling.
        if (Time.time - m_lastChannelTime < ChannelPingTimeout) {
            PauseWindowClock(Time.deltaTime);
        }
    }

    private void Restore() {
        if (m_player == null || m_player.IsDead()) return; // corpse: leave it to vanilla
        if (m_player.m_visual != null) m_player.m_visual.SetActive(true);
        if (m_player.m_collider != null) m_player.m_collider.enabled = true;
        if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner() && m_player.m_body != null) {
            m_player.m_body.isKinematic = false;
        }
    }

    /// <summary>
    /// Shift the downed clock forward by dt -- on OUR OWN player ZDO only. The
    /// marker ZDO is written exclusively by the channeling reviver (progress);
    /// writing the clock there too made two peers fight over the marker's data
    /// revision and clobber each other's values. The marker's gradient reads the
    /// player clock instead.
    /// </summary>
    private void PauseWindowClock(float dt) {
        var zdo = m_nview!.GetZDO();
        zdo.Set(DownedState.s_downedTime, zdo.GetFloat(DownedState.s_downedTime) + dt);
    }

    /// <summary>A reviver is holding the channel (routed ping from any client).</summary>
    public void ChannelPing() => m_lastChannelTime = Time.time;
}
