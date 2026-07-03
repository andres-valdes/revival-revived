using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Downed-state domain operations as extensions on <see cref="Player"/>.
///
/// Reads work on every client (they only touch replicated ZDO fields); the
/// state transitions (<see cref="EnterDownedState"/>, <see cref="ReviveFromDowned"/>,
/// <see cref="ExpireDownedState"/>) are owner-only -- the caller is the
/// CheckDeath patch or an owner-routed RPC handler.
/// </summary>
public static class PlayerDownedExtensions {
    // -------------------------------------------------------------------
    //  Reads (any client)
    // -------------------------------------------------------------------
    public static bool IsDowned(this Player? player) {
        if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return false;
        return player.m_nview.GetZDO().GetBool(DownedKeys.Downed);
    }

    public static bool IsReviveWindowExpired(this Player player) {
        var downedTime = player.m_nview.GetZDO().GetFloat(DownedKeys.DownedTime);
        return (float)ZNet.instance.GetTimeSeconds() - downedTime > Plugin.ReviveWindow;
    }

    /// <summary>Seconds left in the revive window.</summary>
    public static float GetDownedRemainingTime(this Player? player) {
        if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return 0f;
        var downedTime = player.m_nview.GetZDO().GetFloat(DownedKeys.DownedTime);
        return Mathf.Max(0f, Plugin.ReviveWindow - ((float)ZNet.instance.GetTimeSeconds() - downedTime));
    }

    /// <summary>
    /// Revive channel progress 0-1, read from the linked marker's ZDO (written
    /// peer-authoritatively by whichever client is channeling).
    /// </summary>
    public static float GetReviveProgress(this Player? player) {
        var marker = player.FindDownedMarker();
        if (marker == null) return 0f;
        var nv = marker.GetComponent<ZNetView>();
        if (nv == null || !nv.IsValid()) return 0f;
        return nv.GetZDO().GetFloat(DownedKeys.ReviveProgress);
    }

    /// <summary>The green marker tombstone linked to this player, on any client.</summary>
    public static GameObject? FindDownedMarker(this Player? player) {
        if (player == null || player.m_nview == null || !player.m_nview.IsValid()) return null;
        var markerId = player.m_nview.GetZDO().GetZDOID(DownedKeys.MarkerZdoId);
        if (markerId == ZDOID.None) return null;
        return ZNetScene.instance.FindInstance(markerId);
    }

    // -------------------------------------------------------------------
    //  Transitions (owner only)
    // -------------------------------------------------------------------

    /// <summary>
    /// Enter the downed state: set the replicated flags, spawn the green marker,
    /// broadcast the poof. All presentation is enforced by <see cref="Revivable"/>,
    /// which every client attaches in reaction to the replicated flag.
    /// </summary>
    public static void EnterDownedState(this Player player) {
        var zdo = player.m_nview.GetZDO();

        DownedMarker.ReplaceGraveAt = null; // stale replace-position must not leak in

        zdo.Set(DownedKeys.Downed, true);
        zdo.Set(DownedKeys.DownedTime, (float)ZNet.instance.GetTimeSeconds());

        if (player.GetComponent<Revivable>() == null) {
            player.gameObject.AddComponent<Revivable>();
        }

        player.m_nview.InvokeRPC(ZNetView.Everybody, DownedKeys.RpcOnDowned);
        DownedMarker.Spawn(player);

        player.Message(MessageHud.MessageType.Center, "You are downed!");
        Plugin.Logger.LogInfo($"{player.GetPlayerName()} entered downed state (owner)");
    }

    /// <summary>
    /// Revive: clearing the replicated flag IS the state transition -- every
    /// client's Revivable observes it and restores visual/collider locally (no
    /// RPC ordering races).
    /// </summary>
    public static void ReviveFromDowned(this Player player, long reviverId = 0L) {
        if (player == null || !player.m_nview.IsValid()) return;
        if (!player.m_nview.IsOwner()) {
            Plugin.Logger.LogWarning("ReviveFromDowned called on non-owner; ignoring");
            return;
        }

        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedKeys.Downed, false);

        player.SetHealth(Mathf.Max(player.GetMaxHealth() * Plugin.ReviveHealthFraction, 1f));

        DownedMarker.DestroyLinkedMarker(zdo);

        player.Message(MessageHud.MessageType.Center, "You have been revived!");
        Plugin.Logger.LogInfo($"{player.GetPlayerName()} was revived by {ReviverName(reviverId)}");
    }

    /// <summary>
    /// The window ran out: clear the flag and remove the marker; the CheckDeath
    /// patch then lets vanilla OnDeath spawn the real grave in its place.
    /// </summary>
    public static void ExpireDownedState(this Player player) {
        if (player == null || !player.m_nview.IsValid()) return;

        var zdo = player.m_nview.GetZDO();
        zdo.Set(DownedKeys.Downed, false);

        // Vanilla death needs the body back under physics control right now
        // (OnDeath runs this same tick); the Revivable teardown skips dead
        // players, so do it here.
        player.m_collider.enabled = true;
        player.m_body.isKinematic = false;

        var marker = player.FindDownedMarker();
        if (marker != null) DownedMarker.ReplaceGraveAt = marker.transform.position;
        DownedMarker.DestroyLinkedMarker(zdo);

        Plugin.Logger.LogInfo($"{player.GetPlayerName()} revive window expired, proceeding to death");
    }

    private static string ReviverName(long reviverId) {
        if (reviverId == 0L) return "someone";
        var p = Player.GetPlayer(reviverId);
        return p != null ? p.GetPlayerName() : reviverId.ToString();
    }

    // -------------------------------------------------------------------
    //  Downed poof (transient effect, any client)
    // -------------------------------------------------------------------

    /// <summary>Effect objects created by the last downed poof (test hook).</summary>
    public static int LastPoofCount { get; private set; }

    /// <summary>Prefab the poof effect was sourced from (test hook).</summary>
    public static string LastPoofSourceName { get; private set; } = "";

    private static EffectList? s_cachedRemoveEffect;
    private static bool s_removeEffectSearched;

    /// <summary>
    /// Play the corpse-vanish "poof" (smoke/particles) at the player on this
    /// client. Runs from the OnDowned RPC everywhere.
    /// </summary>
    public static int PlayDownedPoof(this Player player) {
        var effect = FindRagdollRemoveEffect();
        if (effect == null) { LastPoofCount = 0; return 0; }
        var created = effect.Create(player.GetCenterPoint(), Quaternion.identity);
        LastPoofCount = created?.Length ?? 0;
        return LastPoofCount;
    }

    /// <summary>
    /// The effect enemy ragdolls play when they despawn. The player's own
    /// ragdoll has no remove-effect, so we borrow one -- prefer the Greyling's,
    /// which is appropriately small (a generic scan can land on huge ones).
    /// </summary>
    private static EffectList? FindRagdollRemoveEffect() {
        if (s_removeEffectSearched) return s_cachedRemoveEffect;
        if (ZNetScene.instance == null) return null; // retry once the scene is ready
        s_removeEffectSearched = true;

        foreach (var name in new[] { "Greyling_ragdoll", "Greydwarf_ragdoll" }) {
            var prefab = ZNetScene.instance.GetPrefab(name);
            var ragdoll = prefab != null ? prefab.GetComponent<Ragdoll>() : null;
            if (ragdoll != null && ragdoll.m_removeEffect != null
                && ragdoll.m_removeEffect.m_effectPrefabs.Length > 0) {
                s_cachedRemoveEffect = ragdoll.m_removeEffect;
                LastPoofSourceName = prefab!.name;
                Plugin.Logger.LogInfo($"Downed poof: using remove-effect from '{prefab.name}'");
                return s_cachedRemoveEffect;
            }
        }

        foreach (var prefab in ZNetScene.instance.m_prefabs) {
            if (prefab == null) continue;
            var ragdoll = prefab.GetComponent<Ragdoll>();
            if (ragdoll != null && ragdoll.m_removeEffect != null
                && ragdoll.m_removeEffect.m_effectPrefabs.Length > 0) {
                s_cachedRemoveEffect = ragdoll.m_removeEffect;
                LastPoofSourceName = prefab.name;
                Plugin.Logger.LogInfo($"Downed poof: using remove-effect from '{prefab.name}' (fallback)");
                break;
            }
        }
        if (s_cachedRemoveEffect == null) {
            Plugin.Logger.LogWarning("Downed poof: no ragdoll prefab with a remove-effect found");
        }
        return s_cachedRemoveEffect;
    }
}
