using HarmonyLib;
using RevivalRevived.Components;

namespace RevivalRevived.Patches;

/// <summary>
/// Converts any tombstone flagged as a downed marker into the green revive
/// marker. Runs on every client: the owner instantiates the tombstone and sets
/// the ZDO flag, remotes instantiate it from the streamed ZDO. Because Unity
/// defers Start by a frame, the owner's flag (set right after Instantiate) is in
/// place before this postfix runs.
/// </summary>
[HarmonyPatch(typeof(TombStone), "Start")]
static class TombStoneStartMarkerPatch {
    static void Postfix(TombStone __instance) {
        var nview = __instance.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) return;
        if (!nview.GetZDO().GetBool(DownedState.s_isDownedMarker)) return;
        DownedMarker.Convert(__instance);
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
