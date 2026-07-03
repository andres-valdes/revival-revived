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
    /// <summary>Revive window duration in seconds (config, overridable via RR_E2E_WINDOW for tests).</summary>
    public static float ReviveWindow {
        get {
            if (s_windowEnvOverride > 0f) return s_windowEnvOverride;
            return Plugin.ReviveWindowCfg?.Value ?? 30f;
        }
    }

    private static readonly float s_windowEnvOverride = ReadWindowOverride();

    private static float ReadWindowOverride() {
        var s = System.Environment.GetEnvironmentVariable("RR_E2E_WINDOW");
        return float.TryParse(s, out var v) && v > 0f ? v : 0f;
    }

    /// <summary>How long the channeled revive interaction takes (config, Hold mode).</summary>
    public static float ReviveDuration => Mathf.Max(0.1f, Plugin.ReviveHoldTimeCfg?.Value ?? 4f);

    /// <summary>True when a single press (no hold) completes the revive.</summary>
    public static bool PressMode =>
        Plugin.ReviveModeCfg != null && Plugin.ReviveModeCfg.Value == ReviveModeType.Press;

    /// <summary>Health percentage restored on revive (0-1).</summary>
    public const float ReviveHealthFraction = 0.25f;

    // RPC names
    /// <summary>Broadcast: play the downed poof at the player (visuals are otherwise ZDO-driven).</summary>
    public const string RPC_OnDowned = "RevivalRevived_OnDowned";
    /// <summary>Reviver -> owner: "someone is channeling", pauses the bleed-out window.</summary>
    public const string RPC_Channel = "RevivalRevived_Channel";
    /// <summary>Reviver -> owner: the (peer-authoritative) hold completed, execute the revive.</summary>
    public const string RPC_DoRevive = "RevivalRevived_DoRevive";

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

    /// <summary>Effect objects created by the last downed poof (test hook).</summary>
    public static int LastPoofCount { get; private set; }

    /// <summary>Upward launch velocity applied to the last spawned marker (test hook).</summary>
    public static float LastMarkerPopVelY { get; private set; }

    /// <summary>
    /// When set, the next real (loot) tombstone spawned by vanilla death replaces
    /// the removed green marker seamlessly: it appears at this position with no
    /// drop-in pop (the pop already played when the marker spawned). Consumed by
    /// the TombStone.Setup patch.
    /// </summary>
    public static Vector3? ReplaceGraveAt;

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

        ReplaceGraveAt = null; // stale replace-position must not affect this cycle

        // Mark downed on player ZDO. Everything presentational (hide visual,
        // disable collider, freeze body) is enforced by the Revivable component,
        // which every client attaches in reaction to this replicated flag.
        zdo.Set(s_downed, true);
        zdo.Set(s_downedTime, (float)ZNet.instance.GetTimeSeconds());

        if (player.GetComponent<Revivable>() == null) {
            player.gameObject.AddComponent<Revivable>();
        }

        // Broadcast the downed poof (a transient effect; state is ZDO-driven).
        nview.InvokeRPC(ZNetView.Everybody, RPC_OnDowned);

        // Spawn the green tombstone marker at the death spot.
        SpawnDownedMarker(player);

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

        // Remove the green marker; vanilla OnDeath will spawn the real grave in
        // its place (no second drop-in pop).
        var linked = FindLinkedMarker(player);
        if (linked != null) ReplaceGraveAt = linked.transform.position;
        DestroyLinkedMarker(zdo);
        // Also remove any orphaned marker for this player (disgraceful disconnect).
        // On reconnect the marker is owned by the server, so claim it first.
        var orphan = FindMarkerForPlayer(player.GetPlayerID());
        if (orphan != null) ReplaceGraveAt = orphan.transform.position;
        DestroyMarkerObject(orphan);

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
    /// Play the ragdoll-despawn "poof" (smoke/particles) at the downed spot on
    /// this client. Sourced from an enemy ragdoll's remove-effect so it matches
    /// the vanilla corpse-vanish effect. Runs from the OnDowned RPC on every
    /// client. Returns the number of effect objects spawned.
    /// </summary>
    public static int PlayDownedPoof(Player player) {
        var effect = FindRagdollRemoveEffect(player);
        if (effect == null) { LastPoofCount = 0; return 0; }
        var created = effect.Create(player.GetCenterPoint(), Quaternion.identity);
        LastPoofCount = created?.Length ?? 0;
        return LastPoofCount;
    }

    private static EffectList? s_cachedRemoveEffect;
    private static bool s_removeEffectSearched;

    /// <summary>Prefab the poof effect was sourced from (test hook).</summary>
    public static string LastPoofSourceName { get; private set; } = "";

    /// <summary>
    /// The smoke/particle effect enemy ragdolls play when they despawn. The
    /// player's own ragdoll has no remove-effect, so we borrow one. Prefer the
    /// Greyling's -- it is appropriately small for a player-sized poof (the
    /// generic scan can land on huge ones like the Abomination's).
    /// </summary>
    private static EffectList? FindRagdollRemoveEffect(Player player) {
        if (s_removeEffectSearched) return s_cachedRemoveEffect;
        if (ZNetScene.instance == null) return null; // try again once scene is ready
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

        // Fallback: any ragdoll prefab with a remove-effect.
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

        // Clearing the replicated flag is the whole state transition: every
        // client's Revivable observes it and restores visual/collider locally
        // (no RPC ordering races -- the flag and the restore travel together).
        var zdo = downedPlayer.m_nview.GetZDO();
        zdo.Set(s_downed, false);

        var maxHp = downedPlayer.GetMaxHealth();
        downedPlayer.SetHealth(Mathf.Max(maxHp * ReviveHealthFraction, 1f));

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

        // Vanilla death needs the body back under physics control right now
        // (before OnDeath runs this same tick); the Revivable teardown skips
        // dead players, so do it here.
        player.m_collider.enabled = true;
        player.m_body.isKinematic = false;

        // Remove the green marker; vanilla OnDeath will spawn the real tombstone
        // in its place (no second drop-in pop).
        var marker = FindLinkedMarker(player);
        if (marker != null) ReplaceGraveAt = marker.transform.position;
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

    /// <summary>
    /// Revive channel progress 0-1. Read from the linked marker's ZDO -- written
    /// peer-authoritatively by whichever client is channeling.
    /// </summary>
    public static float GetReviveProgress(Player player) {
        var marker = FindLinkedMarker(player);
        if (marker == null) return 0f;
        var nv = marker.GetComponent<ZNetView>();
        if (nv == null || !nv.IsValid()) return 0f;
        return nv.GetZDO().GetFloat(s_reviveProgress);
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

        // Give the marker the vanilla tombstone "drop-in" pop (Setup normally does
        // this, but we never call Setup -- the marker is not a loot grave).
        var body = go.GetComponent<Rigidbody>();
        if (body != null) body.linearVelocity = new Vector3(0f, 5f, 0f);
        LastMarkerPopVelY = body != null ? body.linearVelocity.y : 0f;

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
