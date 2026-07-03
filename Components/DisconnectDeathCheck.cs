using System.Collections;
using UnityEngine;
using ZdoTyped;

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
            if (m_player.IsDowned()) { Destroy(this); yield break; }

            var orphan = DownedMarker.FindFor(pid);
            if (orphan != null) {
                Plugin.Logger.LogInfo($"{m_player.GetPlayerName()} reconnected with an orphaned downed marker -> dying");
                KillDowned(m_player);
                Destroy(this);
                yield break;
            }

            t += 0.5f;
            yield return new WaitForSecondsRealtime(0.5f);
        }
        Destroy(this);
    }

    /// <summary>
    /// Complete the death of a player who disconnected while downed: remove the
    /// orphaned marker and run vanilla death so the real (looted) grave spawns
    /// in its place. Owner only.
    /// </summary>
    private static void KillDowned(Player player) {
        if (player == null || !player.m_nview.IsValid() || !player.m_nview.IsOwner()) return;

        var zdo = player.m_nview.GetZdo<DownedPlayerZdo>();
        zdo.Downed = false;

        // Remove any linked or orphaned marker; the real grave replaces it
        // in place (no second drop-in pop) via the replace fields on our own
        // player ZDO. On reconnect the marker is owned by the server, so
        // DestroyMarker claims it first.
        var linked = player.FindDownedMarker();
        var orphan = DownedMarker.FindFor(player.GetPlayerID());
        var replaceAt = orphan != null ? orphan.transform.position
                      : linked != null ? linked.transform.position
                      : (Vector3?)null;
        if (replaceAt != null) {
            zdo.GraveReplacePending = true;
            zdo.GraveReplacePos = replaceAt.Value;
        }
        DownedMarker.DestroyLinkedMarker(ref zdo);
        DownedMarker.DestroyMarker(orphan);

        var rev = player.GetComponent<Revivable>();
        if (rev != null) Destroy(rev);

        // Restore control so vanilla death runs cleanly.
        if (player.m_collider != null) player.m_collider.enabled = true;
        if (player.m_body != null) player.m_body.isKinematic = false;
        if (player.m_visual != null) player.m_visual.SetActive(true);

        // Guard: OnDeath dereferences m_lastHit before spawning the grave.
        if (player.m_lastHit == null) {
            player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
        }
        player.SetHealth(0f);

        // Invoke vanilla death directly (spawns the real tombstone, sets s_dead).
        HarmonyLib.Traverse.Create(player).Method("OnDeath").GetValue();
        Plugin.Logger.LogInfo($"{player.GetPlayerName()} died from being downed at disconnect");
    }
}
