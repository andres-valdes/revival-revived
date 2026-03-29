using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;

namespace RevivalRevived.Patches;

/// <summary>
/// When a ragdoll starts, check if it's a downed-player ragdoll and
/// add ReviveInteractable components. Runs for both initial spawn
/// (ZDO fields written between Awake and Start) and late-join
/// (ZDO fields already present from sync).
/// </summary>
[HarmonyPatch(typeof(Ragdoll), "Start")]
static class RagdollStartPatch {
    static void Postfix(Ragdoll __instance) {
        var nview = __instance.GetComponent<ZNetView>();
        if (nview == null || nview.GetZDO() == null) return;

        // Check if this ragdoll is linked to a downed player
        var playerZdoId = nview.GetZDO().GetZDOID(DownedState.s_playerZDOID);
        if (playerZdoId == ZDOID.None) return;

        // Cancel the TTL self-destruct (we manage lifetime)
        __instance.CancelInvoke("DestroyNow");

        // Move colliders to a layer in the interact mask so hover raycast can hit them
        int characterLayer = LayerMask.NameToLayer("character");
        int count = 0;
        foreach (var col in __instance.GetComponentsInChildren<Collider>()) {
            col.gameObject.layer = characterLayer;
            col.gameObject.AddComponent<ReviveInteractable>();
            count++;
        }

        // Also set root and all children to the same layer
        foreach (var t in __instance.GetComponentsInChildren<Transform>(true)) {
            t.gameObject.layer = characterLayer;
        }

        Plugin.Logger.LogInfo($"Set up ReviveInteractable on ragdoll {nview.GetZDO().m_uid}, {count} colliders, layer={characterLayer} ({LayerMask.LayerToName(characterLayer)})");
    }
}
