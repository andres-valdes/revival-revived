using UnityEngine;
using ZdoTyped;

namespace RevivalRevived.Components;

/// <summary>
/// Present on a downed Player on <em>every</em> client. Two jobs:
///
///  - Presentation (all clients): while the replicated downed flag is set, hide
///    the visual and disable the collider (so the invisible corpse neither
///    blocks movement nor eats the hover raycast) and freeze the owner's body.
///    When the flag clears -- revived or expired, observed via ZDO on ANY
///    client -- restore what it changed and destroy itself. No RPC ordering.
///
///  - Revive channel (owner only): the revive is owner-authoritative. Revivers
///    only send <see cref="DownedKeys.RpcChannel"/> pings; here, on the downed
///    player's own owner, we accumulate the hold, pause the bleed-out window,
///    publish progress onto our OWN player ZDO (single writer -> nothing to
///    race), and revive ourselves at completion.
///
/// Lifecycle is ZDO-driven: a slim LateUpdate patch attaches this component to
/// any player whose ZDO says downed; teardown is entirely local.
/// </summary>
public class Revivable : MonoBehaviour {
    private Player? m_player;
    private ZNetView? m_nview;
    private float m_lastChannelTime = -999f;
    private long m_lastChannelSender;
    /// <summary>Accumulated channel time in seconds (owner only).</summary>
    private float m_progress;

    /// <summary>Max gap between channel pings before the window resumes draining and progress decays.</summary>
    private const float ChannelPingTimeout = 0.5f;

    public string PlayerName => m_player != null ? m_player.GetPlayerName() : "Viking";
    public float RemainingTime => m_player != null ? m_player.GetDownedRemainingTime() : 0f;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid() || m_player == null) return;

        // ZDO-driven teardown: the flag cleared (revive or expiry), restore this
        // client's local changes and go away.
        if (!m_player.IsDowned()) {
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

        // --- Owner-only: window enforcement + revive channel ----------------
        if (!m_nview.IsOwner()) return;

        m_player.m_body.isKinematic = true;

        // Expiry-to-death is owned by the CheckDeath patch; keep health at 0 so
        // it fires once the window has elapsed.
        if (m_player.IsReviveWindowExpired()) {
            if (m_player.GetHealth() > 0f) m_player.SetHealth(0f);
            return;
        }

        UpdateReviveChannel();
    }

    /// <summary>
    /// Owner-authoritative revive: accumulate progress while channel pings are
    /// arriving (pausing the bleed-out window meanwhile), publish it on our own
    /// ZDO, and revive at completion. Decay back toward zero when no one is
    /// channeling.
    /// </summary>
    private void UpdateReviveChannel() {
        var zdo = m_nview!.GetZdo<DownedPlayerZdo>();
        bool channeling = Time.time - m_lastChannelTime < ChannelPingTimeout;

        if (channeling) {
            // A press-mode revive completes on the first ping.
            if (Plugin.RevivePressMode) {
                zdo.ReviveProgress = 1f;
                m_player!.ReviveFromDowned(m_lastChannelSender);
                return;
            }

            PauseWindowClock(zdo, Time.deltaTime);
            m_progress += Time.deltaTime;
            if (m_progress >= Plugin.ReviveDuration) {
                zdo.ReviveProgress = 1f;
                m_player!.ReviveFromDowned(m_lastChannelSender);
                return;
            }
        } else if (m_progress > 0f) {
            m_progress = Mathf.Max(0f, m_progress - Time.deltaTime * 2f);
        } else {
            return; // idle: nothing to publish
        }

        zdo.ReviveProgress = Mathf.Clamp01(m_progress / Plugin.ReviveDuration);
    }

    private void Restore() {
        if (m_player == null || m_player.IsDead()) return; // corpse: leave it to vanilla
        if (m_player.m_visual != null) m_player.m_visual.SetActive(true);
        if (m_player.m_collider != null) m_player.m_collider.enabled = true;
        if (m_nview != null && m_nview.IsValid() && m_nview.IsOwner() && m_player.m_body != null) {
            m_player.m_body.isKinematic = false;
        }
    }

    /// <summary>Shift the downed clock forward by dt so the window doesn't drain while channeling.</summary>
    private static void PauseWindowClock(DownedPlayerZdo zdo, float dt) => zdo.DownedTime += dt;

    /// <summary>A reviver is channeling (routed ping). Records who, for the revive message.</summary>
    public void ChannelPing(long sender) {
        m_lastChannelTime = Time.time;
        m_lastChannelSender = sender;
    }
}
