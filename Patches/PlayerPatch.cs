using HarmonyLib;
using RevivalRevived.Components;
using UnityEngine;

namespace RevivalRevived.Patches;

/// <summary>
/// Registers our custom RPCs on Player and restores downed state on late join.
/// </summary>
[HarmonyPatch(typeof(Player), "Awake")]
static class PlayerAwakePatch {
    static void Postfix(Player __instance) {
        var nview = __instance.m_nview;

        // Transient poof effect on down; all state is ZDO-driven via Revivable.
        nview.Register(DownedKeys.RpcOnDowned, (long sender) => {
            __instance.PlayDownedPoof();
        });

        // Reviver -> owner: "still channeling" ping (pauses the bleed-out window).
        nview.Register(DownedKeys.RpcChannel, (long sender) => {
            if (!nview.IsOwner()) return;
            __instance.GetComponent<Revivable>()?.ChannelPing();
        });

        // Reviver -> owner: peer-authoritative hold completed, execute the revive.
        nview.Register(DownedKeys.RpcDoRevive, (long sender) => {
            if (!nview.IsOwner() || !__instance.IsDowned()) return;
            __instance.ReviveFromDowned(sender);
        });

        // Late join / streamed-in-while-downed: the component reflects the
        // replicated state; everything else happens in its Update.
        if (__instance.IsDowned() && __instance.GetComponent<Revivable>() == null) {
            __instance.gameObject.AddComponent<Revivable>();
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
/// ZDO-driven component attach: any player instance whose replicated ZDO says
/// downed gets a <see cref="Revivable"/>, which owns all downed-state
/// presentation and enforcement (and tears itself down when the flag clears).
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
static class PlayerLateUpdatePatch {
    static void Postfix(Player __instance) {
        if (__instance.IsDowned() && __instance.GetComponent<Revivable>() == null) {
            __instance.gameObject.AddComponent<Revivable>();
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
/// Hide the floating name/health bar for downed players -- the body is
/// invisible, so a hovering health bar over empty air (or over the marker)
/// is wrong; the green tombstone marker is the downed indicator.
/// </summary>
[HarmonyPatch(typeof(EnemyHud), "TestShow")]
static class EnemyHudHideDownedPatch {
    static void Postfix(Character c, ref bool __result) {
        if (__result && c is Player p && p.IsDowned()) {
            __result = false;
        }
    }
}

/// <summary>
/// On spawn, attach the disconnect-death check to the local player so that a
/// player who disconnected while downed dies on reconnect (see
/// <see cref="DisconnectDeathCheck"/>).
/// </summary>
[HarmonyPatch(typeof(Player), "OnSpawned")]
static class PlayerOnSpawnedPatch {
    static void Postfix(Player __instance) {
        if (__instance == Player.m_localPlayer
            && __instance.m_nview != null && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner()
            && __instance.GetComponent<DisconnectDeathCheck>() == null) {
            __instance.gameObject.AddComponent<DisconnectDeathCheck>();
        }
        // Make sure the revive progress circle exists once we're in a world.
        ReviveProgressUI.Ensure();
    }
}
