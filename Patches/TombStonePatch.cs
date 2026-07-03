using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;

namespace RevivalRevived.Patches;

/// <summary>
/// When the real (loot) tombstone spawns after a downed player dies, place it
/// exactly where the green marker stood and suppress the vanilla drop-in pop
/// (upward spawn velocity) -- that pop already played when the marker appeared,
/// so the grave should simply take the marker's place.
/// </summary>
[HarmonyPatch(typeof(TombStone), "Setup")]
static class TombStoneSetupReplacePatch {
    static void Postfix(TombStone __instance) {
        var at = DownedMarker.ReplaceGraveAt;
        if (at == null) return;
        DownedMarker.ReplaceGraveAt = null;

        __instance.transform.position = at.Value;
        var body = __instance.GetComponent<Rigidbody>();
        if (body != null) {
            body.position = at.Value;
            body.linearVelocity = Vector3.zero;
        }
    }
}

/// <summary>
/// Suppress player ragdolls entirely. Player deaths route through
/// Player.CreateDeathEffects (the only place a player ragdoll is spawned); we
/// skip it so no ragdoll is ever created -- the green marker (while downed) and
/// the real tombstone (on true death) are the only corpses.
/// </summary>
[HarmonyPatch(typeof(Player), "CreateDeathEffects")]
static class SuppressPlayerRagdollPatch {
    static bool Prefix() {
        return false; // never spawn the death ragdoll/effects for players
    }
}
