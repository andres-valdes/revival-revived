using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;

namespace RevivalRevived.Patches;

/// <summary>
/// Registers our custom RPCs on Player and prevents downed players
/// from acting (moving, attacking, etc.).
/// </summary>
[HarmonyPatch(typeof(Player), "Awake")]
static class PlayerAwakePatch {
    static void Postfix(Player __instance) {
        // Register RPCs for downed/revived visual sync
        __instance.m_nview.Register("RevivalRevived_OnDowned", (long sender) => {
            // Hide the player visual (same as vanilla RPC_OnDeath)
            __instance.m_visual.SetActive(false);
        });

        __instance.m_nview.Register("RevivalRevived_OnRevived", (long sender) => {
            // Restore the player visual
            __instance.m_visual.SetActive(true);
        });

        // Restore Revivable if this player was already downed (e.g. late join)
        if (DownedState.IsDowned(__instance)) {
            __instance.gameObject.AddComponent<Revivable>();
            __instance.m_visual.SetActive(false);
            __instance.m_collider.enabled = false;
            __instance.m_body.isKinematic = true;
        }
    }
}

/// <summary>
/// Prevent downed players from performing actions.
/// Player.CanMove() gates movement, attacks, dodging, etc.
/// </summary>
[HarmonyPatch(typeof(Player), "CanMove")]
static class PlayerCanMovePatch {
    static void Postfix(Player __instance, ref bool __result) {
        if (__result && DownedState.IsDowned(__instance)) {
            __result = false;
        }
    }
}

/// <summary>
/// Skip UpdateMotion while downed so the game doesn't override our position.
/// Character.UpdateMotion is called from CustomFixedUpdate (MonoUpdaters),
/// not from Unity's FixedUpdate.
/// </summary>
[HarmonyPatch(typeof(Character), "UpdateMotion")]
static class CharacterUpdateMotionPatch {
    static bool Prefix(Character __instance) {
        if (__instance is Player player && DownedState.IsDowned(player)) {
            return false; // skip all motion
        }
        return true;
    }
}

/// <summary>
/// Sync downed player position to ragdoll in LateUpdate,
/// after all physics and custom update systems have run.
/// Also re-enforce collision disable each frame.
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
static class PlayerLateUpdatePatch {
    static void Postfix(Player __instance) {
        if (DownedState.IsDowned(__instance)) {
            __instance.m_collider.enabled = false;
            __instance.m_body.isKinematic = true;
            DownedState.SyncPlayerToRagdoll(__instance);
        }
    }
}

/// <summary>
/// Suppress hover/interact while downed. UpdateHover checks IsDead()
/// but downed players aren't dead, so we skip it explicitly.
/// </summary>
[HarmonyPatch(typeof(Player), "UpdateHover")]
static class PlayerUpdateHoverPatch {
    static bool Prefix(Player __instance) {
        if (DownedState.IsDowned(__instance)) {
            return false;
        }
        return true;
    }
}

/// <summary>
/// Suppress the "you are dead" UI when actually just downed.
/// Player.IsDead() returns s_dead from ZDO -- we don't set that,
/// so it should return false. But if anything checks health <= 0
/// to show death UI, this patch handles the HUD message.
/// </summary>
[HarmonyPatch(typeof(Player), "OnRespawn")]
static class PlayerOnRespawnPatch {
    /// <summary>
    /// If a downed player somehow gets a respawn call, treat it as a revive instead.
    /// </summary>
    static bool Prefix(Player __instance) {
        if (DownedState.IsDowned(__instance)) {
            // Don't let vanilla respawn fire while downed
            return false;
        }
        return true;
    }
}
