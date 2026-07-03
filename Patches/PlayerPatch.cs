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

        // Visual sync: hide/show the player model on every client, and play the
        // ragdoll-despawn poof (smoke/particles) where the player went down.
        nview.Register(DownedState.RPC_OnDowned, (long sender) => {
            __instance.m_visual.SetActive(false);
            DownedState.PlayDownedPoof(__instance);
        });
        nview.Register(DownedState.RPC_OnRevived, (long sender) => {
            __instance.m_visual.SetActive(true);
        });

        // Revive channel: routed to the owner of this player's ZDO. Feed the
        // owner-authoritative Revivable.
        nview.Register(DownedState.RPC_Channel, (long sender) => {
            if (!nview.IsOwner()) return;
            var rev = __instance.GetComponent<Revivable>();
            if (rev == null && DownedState.IsDowned(__instance)) {
                rev = __instance.gameObject.AddComponent<Revivable>();
            }
            rev?.ChannelRevive(sender);
        });

        // Late join / streamed-in-while-downed: reflect the replicated state.
        if (DownedState.IsDowned(__instance)) {
            if (__instance.GetComponent<Revivable>() == null) {
                __instance.gameObject.AddComponent<Revivable>();
            }
            __instance.m_visual.SetActive(false);
            if (nview.IsOwner()) {
                __instance.m_collider.enabled = false;
                __instance.m_body.isKinematic = true;
            }
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
/// Per-frame, ZDO-driven downed-state enforcement for every player instance
/// (owner and remote). This is the single place that reconciles a player's
/// local components/visual with the replicated <c>s_downed</c> flag:
///   - attach a <see cref="Revivable"/> if the ZDO says downed and we lack one
///     (the Revivable destroys itself once the ZDO says not-downed);
///   - keep the model hidden on every client;
///   - on the owner, keep the body frozen in place at the death spot.
/// </summary>
[HarmonyPatch(typeof(Player), "LateUpdate")]
static class PlayerLateUpdatePatch {
    static void Postfix(Player __instance) {
        if (!DownedState.IsDowned(__instance)) return;

        if (__instance.GetComponent<Revivable>() == null) {
            __instance.gameObject.AddComponent<Revivable>();
        }

        // Enforce hidden visual on all clients (robust against a missed RPC).
        if (__instance.m_visual != null && __instance.m_visual.activeSelf) {
            __instance.m_visual.SetActive(false);
        }

        // The corpse must not collide on ANY client: a live collider makes the
        // invisible body block movement and eats the hover raycast before it can
        // reach the marker (Player.FindHoverObject stops at the first hit).
        if (__instance.m_collider != null && __instance.m_collider.enabled) {
            __instance.m_collider.enabled = false;
        }

        if (__instance.m_nview != null && __instance.m_nview.IsValid() && __instance.m_nview.IsOwner()) {
            // Keep the downed player frozen in place at the death spot; the green
            // tombstone marker is a separate, self-syncing networked object.
            // (Remote bodies are already kinematic via ZSyncTransform.)
            __instance.m_body.isKinematic = true;
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
/// If a downed player somehow gets a respawn call, don't let vanilla respawn
/// fire while downed.
/// </summary>
[HarmonyPatch(typeof(Player), "OnRespawn")]
static class PlayerOnRespawnPatch {
    static bool Prefix(Player __instance) {
        if (DownedState.IsDowned(__instance)) {
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
        if (__result && c is Player p && DownedState.IsDowned(p)) {
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
