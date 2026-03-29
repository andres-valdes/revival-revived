using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Added to the Player GameObject when they enter the downed state.
/// Owns the revive timer, expiry check, and state transitions.
/// </summary>
public class Revivable : MonoBehaviour {
    private Player? m_player;
    private ZNetView? m_nview;
    private float m_holdTimer;
    private Player? m_currentReviver;

    public string PlayerName => m_player?.GetPlayerName() ?? "Viking";
    public float HoldTimer => m_holdTimer;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Update() {
        if (m_nview == null || !m_nview.IsValid()) return;

        // Check if revive window expired (only the owner checks)
        if (m_nview.IsOwner()) {
            if (DownedState.IsReviveWindowExpired(m_player!)) {
                DownedState.ExpireDownedState(m_player!);
                Destroy(this);
                return;
            }
        }

        // Decay hold timer if nobody is actively interacting this frame
        if (m_currentReviver != null) {
            m_holdTimer -= Time.deltaTime * 2f;
            if (m_holdTimer <= 0f) {
                m_currentReviver = null;
                m_holdTimer = 0f;
            }
        }
    }

    public float GetRemainingTime() {
        if (m_nview == null || !m_nview.IsValid()) return 0f;
        var downedTime = m_nview.GetZDO().GetFloat(DownedState.s_downedTime);
        var now = (float)ZNet.instance.GetTimeSeconds();
        return Mathf.Max(0f, DownedState.ReviveWindow - (now - downedTime));
    }

    /// <summary>
    /// Called by ReviveInteractable when a player holds interact on the ragdoll.
    /// Returns true if the interaction was consumed.
    /// </summary>
    public bool TryRevive(Player reviver) {
        if (m_player == null || reviver == m_player) return false;

        m_currentReviver = reviver;
        m_holdTimer += Time.deltaTime * 3f; // 3x because Update decays at 2x

        if (m_holdTimer >= DownedState.ReviveDuration) {
            DownedState.Revive(m_player, reviver);
            m_holdTimer = 0f;
            m_currentReviver = null;
            Destroy(this);
        }

        return true;
    }
}
