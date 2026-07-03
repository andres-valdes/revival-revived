using System.Collections.Generic;
using UnityEngine;

namespace RevivalRevived.Components;

/// <summary>
/// Static manager tracking which players are downed and managing the
/// downed-marker (a green, floating tombstone) ↔ player linkage.
///
/// Networking model: all state lives on ZDO fields, so it replicates to every
/// client. The *owner* of a downed player's ZDO is the single authority: it
/// enters/clears the downed state, runs the revive timer, and revives. The
/// downed marker is a real networked tombstone object (position replicated by
/// the engine, floats on water) with its loot/despawn scripts stripped and a
/// green tint -- no ragdolls are spawned anywhere. On true death the marker is
/// removed and vanilla spawns the real (red, functional) tombstone.
/// </summary>
public static class DownedState {
    /// <summary>Revive window duration in seconds (overridable via RR_E2E_WINDOW for tests).</summary>
    public static readonly float ReviveWindow = ReadWindowOverride();

    private static float ReadWindowOverride() {
        var s = System.Environment.GetEnvironmentVariable("RR_E2E_WINDOW");
        return float.TryParse(s, out var v) && v > 0f ? v : 30f;
    }

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
    public static readonly int s_linkedPlayerName = "RevivalRevived_linkedPlayerName".GetStableHashCode();

    /// <summary>Marker (tombstone) ZDO flag: this tombstone is a downed marker, not a real grave.</summary>
    public static readonly int s_isDownedMarker = "RevivalRevived_isDownedMarker".GetStableHashCode();
    /// <summary>Revive channel progress 0-1, written by the downed player's owner for hover UI.</summary>
    public static readonly int s_reviveProgress = "RevivalRevived_reviveProgress".GetStableHashCode();
    /// <summary>Stable PlayerID of the downed player (survives logout/rejoin, unlike the character ZDOID).</summary>
    public static readonly int s_ownerPlayerID = "RevivalRevived_ownerPlayerID".GetStableHashCode();

    // ZDOID pairs (long + uint), matching Valheim's convention.
    public static readonly KeyValuePair<int, int> s_markerZDOID = ZDO.GetHashZDOID("RevivalRevived_markerZDOID");
    public static readonly KeyValuePair<int, int> s_playerZDOID = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");

    /// <summary>
    /// Enter the downed state for a player. Called from CheckDeath prefix on the
    /// owner. Spawns the green tombstone marker, hides the player visual, and
    /// writes ZDO fields. No ragdoll is spawned.
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

        // Spawn the green tombstone marker at the death spot.
        SpawnDownedMarker(player);

        // Disable collision and freeze the player body
        player.m_collider.enabled = false;
        player.m_body.isKinematic = true;

        // Notify the downed player
        player.Message(MessageHud.MessageType.Center, "You are downed!");

        Plugin.Logger.LogInfo($"{player.GetPlayerName()} entered downed state (owner)");
    }

    /// <summary>
    /// Find a downed marker in the loaded world belonging to the given stable
    /// PlayerID. Used to detect an orphaned marker left by a downed player who
    /// disconnected ungracefully, so we can complete their death on reconnect.
    /// </summary>
    public static GameObject? FindMarkerForPlayer(long playerId) {
        if (playerId == 0L) return null;
        foreach (var dm in Object.FindObjectsOfType<DownedMarker>()) {
            var nv = dm.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid()) continue;
            if (nv.GetZDO().GetLong(s_ownerPlayerID, 0L) == playerId) return dm.gameObject;
        }
        return null;
    }

    /// <summary>
    /// Complete the death of a downed player: remove the green marker and run
    /// vanilla death so the real (looted) tombstone spawns and the player is
    /// marked dead. Used when a downed player disconnects or reconnects after a
    /// disconnect while downed. Owner only.
    /// </summary>
    public static void KillDowned(Player player) {
        if (player == null || !player.m_nview.IsValid() || !player.m_nview.IsOwner()) return;

        var zdo = player.m_nview.GetZDO();
        zdo.Set(s_downed, false);
        zdo.Set(s_reviveProgress, 0f);

        // Remove the green marker; vanilla OnDeath will spawn the real grave.
        DestroyLinkedMarker(zdo);
        // Also remove any orphaned marker for this player (disgraceful disconnect).
        // On reconnect the marker is owned by the server, so claim it first.
        DestroyMarkerObject(FindMarkerForPlayer(player.GetPlayerID()));

        var rev = player.GetComponent<Revivable>();
        if (rev != null) Object.Destroy(rev);

        // Restore control so vanilla death runs cleanly.
        if (player.m_collider != null) player.m_collider.enabled = true;
        if (player.m_body != null) player.m_body.isKinematic = false;
        if (player.m_visual != null) player.m_visual.SetActive(true);

        // Guard: OnDeath dereferences m_lastHit before spawning the grave.
        if (player.m_lastHit == null) {
            player.m_lastHit = new HitData { m_hitType = HitData.HitType.Self };
        }
        player.SetHealth(0f);

        // Invoke vanilla death directly (spawns the real tombstone, sets s_dead).
        HarmonyLib.Traverse.Create(player).Method("OnDeath").GetValue();
        Plugin.Logger.LogInfo($"{player.GetPlayerName()} died from being downed at disconnect");
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

        // Clean up Revivable component
        var revivable = downedPlayer.GetComponent<Revivable>();
        if (revivable != null) Object.Destroy(revivable);

        // Restore health
        var maxHp = downedPlayer.GetMaxHealth();
        downedPlayer.SetHealth(Mathf.Max(maxHp * ReviveHealthFraction, 1f));

        // Show the player visual again on every client
        downedPlayer.m_nview.InvokeRPC(ZNetView.Everybody, RPC_OnRevived);

        // Destroy the linked marker
        DestroyLinkedMarker(zdo);

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
    /// Called when the revive window expires. Clears downed state and removes the
    /// green marker; the CheckDeath patch then lets vanilla OnDeath fire, which
    /// spawns the real (red, functional) tombstone. Owner only.
    /// </summary>
    public static void ExpireDownedState(Player player) {
        if (player == null || !player.m_nview.IsValid()) return;

        var zdo = player.m_nview.GetZDO();
        zdo.Set(s_downed, false);
        zdo.Set(s_reviveProgress, 0f);

        // Re-enable collision and physics
        player.m_collider.enabled = true;
        player.m_body.isKinematic = false;

        // Clean up Revivable component
        var revivable = player.GetComponent<Revivable>();
        if (revivable != null) Object.Destroy(revivable);

        // Destroy the linked marker (vanilla will spawn the real tombstone on death)
        DestroyLinkedMarker(zdo);

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
    /// Find the Player linked to a downed-marker's ZDO. Works on any client.
    /// </summary>
    public static Player? FindLinkedPlayer(ZDO markerZdo) {
        var playerZdoId = markerZdo.GetZDOID(s_playerZDOID);
        if (playerZdoId == ZDOID.None) return null;

        var playerZdo = ZDOMan.instance.GetZDO(playerZdoId);
        if (playerZdo == null) return null;

        var nview = ZNetScene.instance.FindInstance(playerZdo);
        if (nview == null) return null;

        return nview.GetComponent<Player>();
    }

    /// <summary>
    /// Find the downed-marker tombstone linked to a player, on any client.
    /// </summary>
    public static GameObject? FindLinkedMarker(Player player) {
        if (player?.m_nview == null || !player.m_nview.IsValid()) return null;
        var markerId = player.m_nview.GetZDO().GetZDOID(s_markerZDOID);
        if (markerId == ZDOID.None) return null;
        return ZNetScene.instance.FindInstance(markerId);
    }

    private static void SpawnDownedMarker(Player player) {
        var prefab = player.m_tombstone;
        if (prefab == null) {
            Plugin.Logger.LogError("SpawnDownedMarker: player has no tombstone prefab");
            return;
        }

        var go = Object.Instantiate(prefab, player.GetCenterPoint(), player.transform.rotation);
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) {
            Plugin.Logger.LogError("SpawnDownedMarker: tombstone has no valid ZNetView");
            return;
        }

        var markerZdo = nview.GetZDO();
        var playerZdo = player.m_nview.GetZDO();

        // Flag it as a downed marker so every client converts it (green, no loot).
        markerZdo.Set(s_isDownedMarker, true);
        markerZdo.Set(s_playerZDOID, playerZdo.m_uid);
        markerZdo.Set(s_ownerPlayerID, player.GetPlayerID()); // stable across rejoin
        markerZdo.Set(ZDOVars.s_ownerName, player.GetPlayerName()); // world text
        markerZdo.Set(s_downedTime, (float)ZNet.instance.GetTimeSeconds());

        // Link player → marker.
        playerZdo.Set(s_markerZDOID, markerZdo.m_uid);

        // Convert immediately on the owner (TombStone.Start postfix also converts
        // on every client, incl. this one; DownedMarker.Convert is idempotent).
        var tomb = go.GetComponent<TombStone>();
        if (tomb != null) DownedMarker.Convert(tomb);

        Plugin.Logger.LogInfo($"Spawned downed marker (tombstone) for {player.GetPlayerName()}, ZDOID {markerZdo.m_uid}");
    }

    private static void DestroyLinkedMarker(ZDO playerZdo) {
        var markerId = playerZdo.GetZDOID(s_markerZDOID);
        if (markerId == ZDOID.None) return;

        playerZdo.Set(s_markerZDOID, ZDOID.None);
        DestroyMarkerObject(ZNetScene.instance.FindInstance(markerId));
    }

    /// <summary>
    /// Destroy a marker tombstone. Claims ownership first: after a downed player
    /// disconnects, ownership of their marker transfers to the server, so a
    /// reconnecting (non-owner) client must claim it before Destroy will replicate.
    /// </summary>
    public static void DestroyMarkerObject(GameObject? go) {
        if (go == null) return;
        var nview = go.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid()) return;
        if (!nview.IsOwner()) nview.ClaimOwnership();
        nview.Destroy();
    }
}
