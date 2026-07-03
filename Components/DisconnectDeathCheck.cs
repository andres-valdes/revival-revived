using System.Collections;
using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Enforces "downed + disconnect = death". Added to the local player when it
/// spawns. If a player was downed when they disconnected (gracefully or not),
/// their character ZDO is gone but the green tombstone marker -- a persistent
/// world object -- survives on the server as an orphan. On reconnect we detect
/// that orphan (matched by the stable PlayerID) and complete the death, so a
/// disconnect can never be used to cheat the downed state or relog for a free
/// revive (vanilla clamps saved health back to full on load).
/// </summary>
public class DisconnectDeathCheck : MonoBehaviour {
    private Player? m_player;

    private void Awake() => m_player = GetComponent<Player>();

    private void Start() => StartCoroutine(Check());

    private IEnumerator Check() {
        if (m_player == null || m_player.m_nview == null || !m_player.m_nview.IsValid()
            || !m_player.m_nview.IsOwner()) {
            Destroy(this);
            yield break;
        }

        long pid = m_player.GetPlayerID();
        float t = 0f;
        // Poll for a short while so the orphan marker has time to stream in near
        // the spawn (logout) point.
        while (t < 12f) {
            if (m_player == null || !m_player.m_nview.IsValid()) { Destroy(this); yield break; }
            // If we legitimately went down again after spawning, stop.
            if (DownedState.IsDowned(m_player)) { Destroy(this); yield break; }

            var orphan = DownedState.FindMarkerForPlayer(pid);
            if (orphan != null) {
                Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} reconnected with an orphaned downed marker -> dying");
                DownedState.KillDowned(m_player);
                Destroy(this);
                yield break;
            }

            t += 0.5f;
            yield return new WaitForSecondsRealtime(0.5f);
        }
        Destroy(this);
    }

}
