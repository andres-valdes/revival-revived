using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Revive controller present on a downed Player on <em>every</em> client.
///
/// Its lifecycle is driven entirely by the replicated <c>s_downed</c> ZDO flag:
/// a LateUpdate patch attaches this component to any player the ZDO reports as
/// downed (owner and remotes alike), and the component destroys itself once the
/// ZDO reports the player is no longer downed. This keeps all revive logic
/// compartmentalized in one place instead of split between RPC handlers.
///
/// Behaviour splits by ZDO ownership:
///   - Owner: authoritative. Runs the revive timer, expiry check, writes
///     progress to the ZDO, and performs the revive. Accepts channel input
///     either locally (a co-located reviver) or via routed RPC from a remote
///     reviver.
///   - Remote: passive. <see cref="RequestRevive"/> forwards a channel RPC to
///     the owner; nothing is mutated locally.
/// </summary>
public class Revivable : MonoBehaviour {
    private Player? m_player;
    private ZNetView? m_nview;
    private float m_holdTimer;
    private float m_lastChannelTime = -999f;
    private long m_lastReviverId;

    /// <summary>Max gap between channel messages before the reviver is considered to have stopped.</summary>
    private const float ChannelTimeout = 0.4f;

    public string PlayerName {
        get {
            if (m_player != null) return m_player.GetPlayerName();
            return "Viking";
        }
    }

    public float RemainingTime => m_player != null ? DownedState.GetRemainingTime(m_player) : 0f;
    public float Progress => m_player != null ? DownedState.GetReviveProgress(m_player) : 0f;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid() || m_player == null) return;

        // ZDO-driven lifecycle: if the player is no longer downed (revived or
        // expired by the owner, replicated to us), tear ourselves down.
        if (!DownedState.IsDowned(m_player)) {
            Destroy(this);
            return;
        }

        // Only the owner runs the authoritative timer/expiry/revive.
        if (!m_nview.IsOwner()) return;

        // Expiry-to-death is owned by the CheckDeath patch: while the player is
        // downed and the window has expired, CheckDeath clears downed and lets
        // OnDeath fire. We just keep health at 0 so CheckDeath runs even if the
        // downed player's health drifted upward.
        if (DownedState.IsReviveWindowExpired(m_player)) {
            if (m_player.GetHealth() > 0f) m_player.SetHealth(0f);
            return;
        }

        bool channeling = Time.time - m_lastChannelTime < ChannelTimeout;

        // Press mode: any channel input completes the revive immediately.
        if (DownedState.PressMode) {
            if (channeling) DownedState.Revive(m_player, m_lastReviverId);
            return;
        }

        if (channeling) {
            m_holdTimer += Time.deltaTime;
        } else {
            m_holdTimer = Mathf.Max(0f, m_holdTimer - Time.deltaTime * 2f);
        }

        // Publish progress for the progress UI / hover text on every client.
        m_nview.GetZDO().Set(DownedState.s_reviveProgress,
            Mathf.Clamp01(m_holdTimer / DownedState.ReviveDuration));

        if (m_holdTimer >= DownedState.ReviveDuration) {
            DownedState.Revive(m_player, m_lastReviverId);
            // Revive() destroys this component.
        }
    }

    /// <summary>
    /// Request a unit of revive "hold" from a reviver. Owner-local reviver feeds
    /// the timer directly; a remote reviver routes a channel RPC to the owner.
    /// Safe to call every frame while the reviver holds Use.
    /// </summary>
    public void RequestRevive(Player reviver) {
        if (m_nview == null || !m_nview.IsValid()) return;
        if (m_nview.IsOwner()) {
            ChannelRevive(reviver.GetPlayerID());
        } else {
            m_nview.InvokeRPC(DownedState.RPC_Channel);
        }
    }

    /// <summary>
    /// Feed the authoritative timer. Called on the owner -- either directly by a
    /// co-located reviver or from the routed <see cref="DownedState.RPC_Channel"/> handler.
    /// </summary>
    public void ChannelRevive(long reviverId) {
        m_lastReviverId = reviverId;
        m_lastChannelTime = Time.time;
    }
}
