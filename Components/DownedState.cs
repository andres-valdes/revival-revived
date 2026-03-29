using System.Collections.Generic;
using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Static manager tracking which players are downed and managing
/// the ragdoll ↔ player linkage.
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

    // ZDO field hashes (prefixed to avoid collisions)
    public static readonly int s_downed = "RevivalRevived_downed".GetStableHashCode();
    public static readonly int s_downedTime = "RevivalRevived_downedTime".GetStableHashCode();
    public static readonly int s_ragdollID = "RevivalRevived_ragdollID".GetStableHashCode();
    public static readonly int s_linkedPlayerID = "RevivalRevived_linkedPlayerID".GetStableHashCode();
    public static readonly int s_linkedPlayerName = "RevivalRevived_linkedPlayerName".GetStableHashCode();

    // ZDOID is stored as a pair of fields (long + uint), matching Valheim's convention
    public static readonly KeyValuePair<int, int> s_ragdollZDOID = ZDO.GetHashZDOID("RevivalRevived_ragdollZDOID");
    public static readonly KeyValuePair<int, int> s_playerZDOID = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");

    /// <summary>
    /// Enter the downed state for a player. Called from CheckDeath prefix.
    /// Spawns a ragdoll, hides the player visual, and writes ZDO fields.
    /// </summary>
    public static void EnterDownedState(Player player) {
        var nview = player.m_nview;
        var zdo = nview.GetZDO();

        // Mark downed on player ZDO
        zdo.Set(s_downed, true);
        zdo.Set(s_downedTime, (float)ZNet.instance.GetTimeSeconds());

        // Add revivable state to the player
        player.gameObject.AddComponent<Revivable>();

        // Hide the player visual (same as vanilla RPC_OnDeath)
        nview.InvokeRPC(ZNetView.Everybody, "RevivalRevived_OnDowned");

        // Spawn ragdoll using the player's death effects
        SpawnDownedRagdoll(player);

        // Disable collision and freeze the player body
        player.m_collider.enabled = false;
        player.m_body.isKinematic = true;

        // Notify the downed player
        player.Message(MessageHud.MessageType.Center, "You are downed!");

        Plugin.Logger.LogInfo($"{player.GetPlayerName()} entered downed state");
    }

    /// <summary>
    /// Revive a downed player. Called from Revivable when interaction completes.
    /// </summary>
    public static void Revive(Player downedPlayer, Player reviver) {
        if (downedPlayer == null || !downedPlayer.m_nview.IsValid()) return;

        var zdo = downedPlayer.m_nview.GetZDO();

        // Clear downed state
        zdo.Set(s_downed, false);

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

        // Show the player visual again
        downedPlayer.m_nview.InvokeRPC(ZNetView.Everybody, "RevivalRevived_OnRevived");

        // Destroy the linked ragdoll
        DestroyLinkedRagdoll(zdo);

        downedPlayer.Message(MessageHud.MessageType.Center, "You have been revived!");
        Plugin.Logger.LogInfo($"{downedPlayer.GetPlayerName()} was revived by {reviver.GetPlayerName()}");
    }

    /// <summary>
    /// Called when the revive window expires. Clears downed state and lets
    /// vanilla death proceed on the next CheckDeath tick.
    /// </summary>
    public static void ExpireDownedState(Player player) {
        if (player == null || !player.m_nview.IsValid()) return;

        var zdo = player.m_nview.GetZDO();
        zdo.Set(s_downed, false);

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
    /// Check if a player is currently in the downed state.
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

    /// <summary>
    /// Find the Player instance associated with a ragdoll's ZDO.
    /// </summary>
    public static Player? FindLinkedPlayer(ZDO ragdollZdo) {
        var playerZdoId = ragdollZdo.GetZDOID(s_playerZDOID);
        if (playerZdoId == ZDOID.None) {
            Plugin.Logger.LogWarning("FindLinkedPlayer: playerZDOID is None");
            return null;
        }

        var playerZdo = ZDOMan.instance.GetZDO(playerZdoId);
        if (playerZdo == null) {
            Plugin.Logger.LogWarning($"FindLinkedPlayer: ZDOMan has no ZDO for {playerZdoId}");
            return null;
        }

        var nview = ZNetScene.instance.FindInstance(playerZdo);
        if (nview == null) {
            Plugin.Logger.LogWarning($"FindLinkedPlayer: no instance found for ZDO {playerZdoId}");
            return null;
        }

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

            // Link player → ragdoll via ZDO
            playerZdo.Set(s_ragdollZDOID, ragdollZdo.m_uid);

            // Cache for per-frame position sync
            s_ragdolls[player] = ragdoll;

            Plugin.Logger.LogInfo($"Spawned downed ragdoll for {player.GetPlayerName()}, ZDOID: {ragdollZdo.m_uid}");
            break; // Only need the first ragdoll
        }
    }

    /// <summary>
    /// Sync a downed player's transform to their ragdoll. Called from FixedUpdate patch.
    /// </summary>
    public static void SyncPlayerToRagdoll(Player player) {
        if (!s_ragdolls.TryGetValue(player, out var ragdoll) || ragdoll == null) {
            Plugin.Logger.LogWarning($"SyncPlayerToRagdoll: no cached ragdoll for {player.GetPlayerName()}");
            return;
        }

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
