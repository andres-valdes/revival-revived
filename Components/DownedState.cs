using System.Collections.Generic;
using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Static manager tracking which players are downed and managing
/// the ragdoll ↔ player linkage.
///
/// Networking model: all state lives on ZDO fields, so it replicates to every
/// client. The *owner* of a downed player's ZDO (the machine simulating that
/// player) is the single authority: it enters/clears the downed state, runs the
/// revive timer, and revives. Other clients only read ZDO state and send revive
/// *requests* via routed RPC -- they never mutate the downed player's ZDO
/// directly (non-owner ZDO writes don't replicate and get reverted on sync).
/// </summary>
public static class DownedState {
    /// <summary>Cached ragdoll references for fast per-frame position sync.</summary>
    private static readonly Dictionary<Player, Ragdoll> s_ragdolls = new();
    /// <summary>Revive window duration in seconds.</summary>
    public const float ReviveWindow = 30f;

    /// <summary>How long the channeled revive interaction takes.</summary>
    public const float ReviveDuration = 4f;

    /// <summary>Health percentage restored on revive (0-1).</summary>
    public const float ReviveHealthFraction = 0.25f;

    // RPC names
    public const string RPC_OnDowned = "RevivalRevived_OnDowned";
    public const string RPC_OnRevived = "RevivalRevived_OnRevived";
    public const string RPC_Channel = "RevivalRevived_Channel";

    // ZDO field hashes (prefixed to avoid collisions)
    public static readonly int s_downed = "RevivalRevived_downed".GetStableHashCode();
    public static readonly int s_downedTime = "RevivalRevived_downedTime".GetStableHashCode();
    public static readonly int s_ragdollID = "RevivalRevived_ragdollID".GetStableHashCode();
    public static readonly int s_linkedPlayerID = "RevivalRevived_linkedPlayerID".GetStableHashCode();
    public static readonly int s_linkedPlayerName = "RevivalRevived_linkedPlayerName".GetStableHashCode();

    /// <summary>Owner-authoritative ragdoll average body position, replicated to other clients.</summary>
    public static readonly int s_ragdollPos = "RevivalRevived_ragdollPos".GetStableHashCode();
    /// <summary>Revive channel progress 0-1, written by the downed player's owner for hover UI.</summary>
    public static readonly int s_reviveProgress = "RevivalRevived_reviveProgress".GetStableHashCode();

    // ZDOID is stored as a pair of fields (long + uint), matching Valheim's convention
    public static readonly KeyValuePair<int, int> s_ragdollZDOID = ZDO.GetHashZDOID("RevivalRevived_ragdollZDOID");
    public static readonly KeyValuePair<int, int> s_playerZDOID = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");

    /// <summary>
    /// Enter the downed state for a player. Called from CheckDeath prefix on the
    /// owner. Spawns a ragdoll, hides the player visual, and writes ZDO fields.
    /// </summary>
    public static void EnterDownedState(Player player) {
        var nview = player.m_nview;
        var zdo = nview.GetZDO();

        // Mark downed on player ZDO (replicates to all clients)
        zdo.Set(s_downed, true);
        zdo.Set(s_downedTime, (float)ZNet.instance.GetTimeSeconds());
        zdo.Set(s_reviveProgress, 0f);

        // Add the owner-authoritative revive controller.
        if (player.GetComponent<Revivable>() == null) {
            player.gameObject.AddComponent<Revivable>();
        }

        // Hide the player visual on every client (same idea as vanilla RPC_OnDeath)
        nview.InvokeRPC(ZNetView.Everybody, RPC_OnDowned);

        // Spawn ragdoll using the player's death effects
        SpawnDownedRagdoll(player);

        // Disable collision and freeze the player body
        player.m_collider.enabled = false;
        player.m_body.isKinematic = true;

        // Notify the downed player
        player.Message(MessageHud.MessageType.Center, "You are downed!");

        Plugin.Logger.LogInfo($"{player.GetPlayerName()} entered downed state (owner)");
    }

    /// <summary>
    /// Revive a downed player. MUST run on the owner of the player's ZDO.
    /// Called by the owner's <see cref="Revivable"/> when the channel completes.
    /// </summary>
    public static void Revive(Player downedPlayer, long reviverId = 0L) {
        if (downedPlayer == null || !downedPlayer.m_nview.IsValid()) return;
        if (!downedPlayer.m_nview.IsOwner()) {
            Plugin.Logger.LogWarning("Revive called on non-owner; ignoring");
            return;
        }

        var zdo = downedPlayer.m_nview.GetZDO();

        // Clear downed state
        zdo.Set(s_downed, false);
        zdo.Set(s_reviveProgress, 0f);

        // Re-enable collision and physics
        downedPlayer.m_collider.enabled = true;
        downedPlayer.m_body.isKinematic = false;
        s_ragdolls.Remove(downedPlayer);

        // Clean up Revivable component
        var revivable = downedPlayer.GetComponent<Revivable>();
        if (revivable != null) Object.Destroy(revivable);

        // Restore health
        var maxHp = downedPlayer.GetMaxHealth();
        downedPlayer.SetHealth(Mathf.Max(maxHp * ReviveHealthFraction, 1f));

        // Show the player visual again on every client
        downedPlayer.m_nview.InvokeRPC(ZNetView.Everybody, RPC_OnRevived);

        // Destroy the linked ragdoll
        DestroyLinkedRagdoll(zdo);

        downedPlayer.Message(MessageHud.MessageType.Center, "You have been revived!");
        var reviverName = ReviverName(reviverId);
        Plugin.Logger.LogInfo($"{downedPlayer.GetPlayerName()} was revived by {reviverName}");
    }

    private static string ReviverName(long reviverId) {
        if (reviverId == 0L) return "someone";
        var p = Player.GetPlayer(reviverId);
        return p != null ? p.GetPlayerName() : reviverId.ToString();
    }

    /// <summary>
    /// Called when the revive window expires. Clears downed state and lets
    /// vanilla death proceed on the next CheckDeath tick. Owner only.
    /// </summary>
    public static void ExpireDownedState(Player player) {
        if (player == null || !player.m_nview.IsValid()) return;

        var zdo = player.m_nview.GetZDO();
        zdo.Set(s_downed, false);
        zdo.Set(s_reviveProgress, 0f);

        // Re-enable collision and physics
        player.m_collider.enabled = true;
        player.m_body.isKinematic = false;
        s_ragdolls.Remove(player);

        // Clean up Revivable component
        var revivable = player.GetComponent<Revivable>();
        if (revivable != null) Object.Destroy(revivable);

        // Destroy the linked ragdoll (vanilla will spawn its own on real death)
        DestroyLinkedRagdoll(zdo);

        Plugin.Logger.LogInfo($"{player.GetPlayerName()} revive window expired, proceeding to death");
    }

    /// <summary>
    /// Check if a player is currently in the downed state. Reads replicated ZDO,
    /// so it is correct on every client (owner or not).
    /// </summary>
    public static bool IsDowned(Player player) {
        if (player?.m_nview == null || !player.m_nview.IsValid()) return false;
        return player.m_nview.GetZDO().GetBool(s_downed);
    }

    /// <summary>
    /// Check if the revive window has expired for a downed player.
    /// </summary>
    public static bool IsReviveWindowExpired(Player player) {
        var zdo = player.m_nview.GetZDO();
        var downedTime = zdo.GetFloat(s_downedTime);
        var now = (float)ZNet.instance.GetTimeSeconds();
        return now - downedTime > ReviveWindow;
    }

    /// <summary>Remaining revive seconds, readable on any client.</summary>
    public static float GetRemainingTime(Player player) {
        if (player?.m_nview == null || !player.m_nview.IsValid()) return 0f;
        var zdo = player.m_nview.GetZDO();
        var downedTime = zdo.GetFloat(s_downedTime);
        var now = (float)ZNet.instance.GetTimeSeconds();
        return Mathf.Max(0f, ReviveWindow - (now - downedTime));
    }

    /// <summary>Revive channel progress 0-1, readable on any client.</summary>
    public static float GetReviveProgress(Player player) {
        if (player?.m_nview == null || !player.m_nview.IsValid()) return 0f;
        return player.m_nview.GetZDO().GetFloat(s_reviveProgress);
    }

    /// <summary>
    /// Find the Player instance associated with a ragdoll's ZDO. Works on any
    /// client; returns null if the linked player isn't instantiated locally.
    /// </summary>
    public static Player? FindLinkedPlayer(ZDO ragdollZdo) {
        var playerZdoId = ragdollZdo.GetZDOID(s_playerZDOID);
        if (playerZdoId == ZDOID.None) return null;

        var playerZdo = ZDOMan.instance.GetZDO(playerZdoId);
        if (playerZdo == null) return null;

        var nview = ZNetScene.instance.FindInstance(playerZdo);
        if (nview == null) return null;

        return nview.GetComponent<Player>();
    }

    private static void SpawnDownedRagdoll(Player player) {
        // Use the player's death effects to spawn the ragdoll
        var effects = player.m_deathEffects.Create(
            player.transform.position,
            player.transform.rotation,
            player.transform
        );

        foreach (var go in effects) {
            var ragdoll = go.GetComponent<Ragdoll>();
            if (ragdoll == null) continue;

            // Set up ragdoll physics from player velocity
            var velocity = player.m_body.linearVelocity;
            ragdoll.Setup(velocity, 0f, 0f, 0f, null);

            // Link ragdoll → player via ZDO (must happen before Start so the patch picks it up)
            var ragdollZdo = ragdoll.m_nview.GetZDO();
            var playerZdo = player.m_nview.GetZDO();

            ragdollZdo.Set(s_playerZDOID, playerZdo.m_uid);
            ragdollZdo.Set(s_linkedPlayerName, player.GetPlayerName());
            ragdollZdo.Set(s_downedTime, (float)ZNet.instance.GetTimeSeconds());
            ragdollZdo.Set(s_ragdollPos, ragdoll.GetAverageBodyPosition());

            // Link player → ragdoll via ZDO
            playerZdo.Set(s_ragdollZDOID, ragdollZdo.m_uid);

            // Cache for per-frame position sync
            s_ragdolls[player] = ragdoll;

            Plugin.Logger.LogInfo($"Spawned downed ragdoll for {player.GetPlayerName()}, ZDOID: {ragdollZdo.m_uid}");
            break; // Only need the first ragdoll
        }
    }

    /// <summary>
    /// Sync a downed player's transform to their ragdoll. Called from the owner's
    /// LateUpdate patch. The player position is itself ZDO-replicated by Valheim,
    /// so this keeps every client's downed player co-located with the ragdoll.
    /// </summary>
    public static void SyncPlayerToRagdoll(Player player) {
        if (!s_ragdolls.TryGetValue(player, out var ragdoll) || ragdoll == null) return;

        var ragPos = ragdoll.GetAverageBodyPosition();
        player.transform.position = ragPos;
        player.m_body.position = ragPos;
    }

    private static void DestroyLinkedRagdoll(ZDO playerZdo) {
        var ragdollZdoId = playerZdo.GetZDOID(s_ragdollZDOID);
        if (ragdollZdoId == ZDOID.None) return;

        // Clear the link
        playerZdo.Set(s_ragdollZDOID, ZDOID.None);

        // FindInstance(ZDOID) returns GameObject
        var ragdollGo = ZNetScene.instance.FindInstance(ragdollZdoId);
        if (ragdollGo != null) {
            var nview = ragdollGo.GetComponent<ZNetView>();
            if (nview != null) {
                nview.Destroy();
            }
        }
    }
}
