using HarmonyLib;
using ReviveAllies.Components;
using UnityEngine;

namespace ReviveAllies.Patches;

/// <summary>
/// When the real (loot) tombstone spawns after a downed player dies, place it
/// exactly where the green marker stood and suppress the vanilla drop-in pop
/// (upward spawn velocity) -- that pop already played when the marker appeared,
/// so the grave should simply take the marker's place. The marker itself is
/// then flagged replaced (not destroyed): each client hides it locally once it
/// can see this grave, so the swap never shows a gap -- destroy/create packets
/// are not ordered across the network, and even locally a destroy-then-spawn
/// can straddle a rendered frame.
/// </summary>
[HarmonyPatch(typeof(TombStone), "Setup")]
static class TombStoneSetupReplacePatch {
    static void Postfix(TombStone __instance, long ownerUID) {
        // Graves are only Setup() by the dying player's own client; the replace
        // request lives on that player's ZDO (owner-written, no global state).
        var player = Player.m_localPlayer;
        if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return;
        if (ownerUID != player.GetPlayerID()) return;

        var state = player.State();
        if (!state.GraveReplacePending) return;
        state.GraveReplacePending = false; // consume

        var at = state.GraveReplacePos;
        if (at == Vector3.zero) at = __instance.transform.position; // no recorded spot
        __instance.transform.position = at;
        var body = __instance.GetComponent<Rigidbody>();
        if (body != null) {
            body.position = at;
            body.linearVelocity = Vector3.zero;
        }

        // Hand the spot over: the marker hides itself on each client as soon as
        // that client sees this grave, then its ZDO is destroyed.
        var marker = player.FindDownedMarker()
                     ?? MarkerPrefab.FindFor(player.GetPlayerID()); // reconnect path: orphan, not linked
        MarkerState.MarkReplaced(marker);
        state.Marker = ZDOID.None;
    }
}

/// <summary>
/// Vanilla CreateTombStone spawns a grave only when the inventory has items.
/// If a downed player dies with nothing to drop, the replace request survives
/// OnDeath unconsumed -- no grave will ever take the marker's place -- so the
/// marker despawns the way an emptied grave does: crumble effect, then destroy.
/// </summary>
[HarmonyPatch(typeof(Player), "OnDeath")]
static class PlayerOnDeathMarkerCrumblePatch {
    static void Postfix(Player __instance) {
        var nview = __instance.m_nview;
        if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

        var state = __instance.State();
        if (!state.GraveReplacePending) return; // grave spawned (consumed), or never downed
        state.GraveReplacePending = false;

        var marker = __instance.FindDownedMarker()
                     ?? MarkerPrefab.FindFor(__instance.GetPlayerID());
        state.Marker = ZDOID.None;
        MarkerState.Crumble(marker);
        Plugin.Logger.LogInfo($"{__instance.GetPlayerName()} died with no grave to drop; marker crumbled");
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
