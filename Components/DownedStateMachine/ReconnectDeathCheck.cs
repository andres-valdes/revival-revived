using System.Collections;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// A player who disconnects while downed must die on reconnect -- the orphan
/// marker they left behind is the evidence. This owner-only component, present on
/// every Player alongside <see cref="DownedStateMachine"/>, polls briefly on spawn
/// for an orphan marker matching this player's stable id and, if found, finishes
/// the death (handing the spot to the real grave). Split out of DownedStateMachine
/// so the controller stays purely the state-machine host.
/// </summary>
public class ReconnectDeathCheck : MonoBehaviour {
    private Player m_player = null!;
    private ZNetView m_nview = null!;

    private void Awake() {
        m_player = GetComponent<Player>();
        m_nview = GetComponent<ZNetView>();
    }

    private void Start() {
        if (m_nview.IsValid() && m_nview.IsOwner()) StartCoroutine(Check());
    }

    /// <summary>Test hook: re-run the reconnect-orphan check now.</summary>
    public void RunReconnectCheck() => StartCoroutine(Check());

    private IEnumerator Check() {
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
                Kill();
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
    private void Kill() {
        if (!m_nview.IsValid() || !m_nview.IsOwner()) return;

        var state = m_player.State();
        state.Downed = false;

        var orphan = MarkerPrefab.FindFor(m_player.GetPlayerID());
        var linked = m_player.FindDownedMarker();
        var at = orphan != null ? orphan.transform.position
               : linked != null ? linked.transform.position
               : (Vector3?)null;
        if (at != null) {
            var grave = m_player.GraveReplace();
            grave.Pending = true;
            grave.Pos = at.Value;
        }

        if (m_player.m_lastHit == null) {
            m_player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
        }
        m_player.SetHealth(0f);
        HarmonyLib.Traverse.Create(m_player).Method("OnDeath").GetValue();
        Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} died from being downed at disconnect");
    }
}
