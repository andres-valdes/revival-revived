using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The presentation of a downed player, on <em>every</em> client. A pure
/// reaction to the replicated downed flag: hide the visual and disable the
/// collider (so the invisible corpse neither blocks movement nor eats the hover
/// raycast), freeze the owner's body, play the poof when the player goes down,
/// and restore everything when the flag clears. No authority, no transitions --
/// that all lives in <see cref="DownedStateMachine"/>.
///
/// Because the downed transition is replicated ZDO state, each client plays the
/// poof itself when it observes the change -- there is no poof RPC.
/// </summary>
public class DownedView : MonoBehaviour {
    private Player m_player = null!;
    private ZNetView m_nview = null!;
    private bool m_wasDowned;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Start() {
        // Don't poof for a player who was already downed when we streamed them in
        // -- only for a genuine not-downed -> downed transition we observe.
        m_wasDowned = m_player.IsDowned();
    }

    private void Update() {
        if (!m_nview.IsValid()) return;

        bool downed = m_player.IsDowned();
        if (downed) {
            if (!m_wasDowned) m_player.PlayDownedPoof(); // once, on going down
            if (m_player.m_visual != null && m_player.m_visual.activeSelf) {
                m_player.m_visual.SetActive(false);
            }
            if (m_player.m_collider != null && m_player.m_collider.enabled) {
                m_player.m_collider.enabled = false;
            }
            if (m_nview.IsOwner() && m_player.m_body != null) {
                m_player.m_body.isKinematic = true;
            }
        } else if (m_wasDowned) {
            Restore();
        }
        m_wasDowned = downed;
    }

    private void Restore() {
        if (m_player.IsDead()) return; // corpse: leave it to vanilla
        if (m_player.m_visual != null) m_player.m_visual.SetActive(true);
        if (m_player.m_collider != null) m_player.m_collider.enabled = true;
        if (m_nview.IsOwner() && m_player.m_body != null) m_player.m_body.isKinematic = false;
    }
}
