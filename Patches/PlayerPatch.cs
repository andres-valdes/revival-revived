using HarmonyLib;
using ReviveAllies.Components;
using UnityEngine;

namespace ReviveAllies.Patches;

/// <summary>
/// Attach the downed-state components to every Player, once. They are always
/// present and gate on state internally: <see cref="DownedController"/> is the
/// owner-side authority (state machine + RPC handler), <see cref="DownedView"/>
/// is the all-clients presentation.
/// </summary>
[HarmonyPatch(typeof(Player), "Awake")]
static class PlayerAwakePatch {
    static void Postfix(Player __instance) {
        if (__instance.GetComponent<DownedController>() == null) {
            __instance.gameObject.AddComponent<DownedController>();
        }
        if (__instance.GetComponent<DownedView>() == null) {
            __instance.gameObject.AddComponent<DownedView>();
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
        if (__result && __instance.IsDowned()) {
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
        if (__instance is Player player && player.IsDowned()) {
            return false; // skip all motion
        }
        return true;
    }
}

/// <summary>
/// Keep a dead player's corpse inert until respawn.
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
static class PlayerLateUpdatePatch {
    static void Postfix(Player __instance) {
        // Dead corpse (we suppress the ragdoll, so the invisible player object
        // lingers until respawn): keep it inert on every client -- no collider
        // to bump into, no physics drift. s_dead is replicated, so remotes see
        // it too. The respawned player is a fresh object with a fresh collider.
        if (__instance.IsDead()) {
            if (__instance.m_collider != null && __instance.m_collider.enabled) {
                __instance.m_collider.enabled = false;
            }
            if (__instance.m_nview != null && __instance.m_nview.IsValid()
                && __instance.m_nview.IsOwner()
                && __instance.m_body != null && !__instance.m_body.isKinematic) {
                __instance.m_body.isKinematic = true;
            }
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
        if (__instance.IsDowned()) {
            return false;
        }
        return true;
    }
}

/// <summary>
/// If a downed player somehow gets a respawn call, don't let vanilla respawn
/// fire while downed.
/// </summary>
[HarmonyPatch(typeof(Player), "OnRespawn")]
static class PlayerOnRespawnPatch {
    static bool Prefix(Player __instance) {
        if (__instance.IsDowned()) {
            return false;
        }
        return true;
    }
}

/// <summary>
/// Hide the floating name/health bar for downed AND dead players -- the body
/// is invisible in both states (vanilla TestShow never checks IsDead; the
/// ragdoll usually masks that, but we suppress ragdolls), so a nameplate over
/// empty air is wrong; the marker/grave is the indicator.
/// </summary>
[HarmonyPatch(typeof(EnemyHud), "TestShow")]
static class EnemyHudHideDownedPatch {
    static void Postfix(Character c, ref bool __result) {
        if (__result && c is Player p && (p.IsDowned() || p.IsDead())) {
            __result = false;
        }
    }
}

/// <summary>
/// Make sure the revive progress circle exists once we're in a world. (The
/// reconnect-orphan death check lives on <see cref="DownedController"/>.)
/// </summary>
[HarmonyPatch(typeof(Player), "OnSpawned")]
static class PlayerOnSpawnedPatch {
    static void Postfix(Player __instance) {
        ReviveProgressUI.Ensure();
    }
}
