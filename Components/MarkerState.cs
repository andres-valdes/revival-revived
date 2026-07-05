using System.Collections.Generic;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The fields on a revive-marker's ZDO, as one typed view -- the single
/// description of that ZDO's shape. Instances are the data view; the static
/// methods are the small lifecycle operations on a marker GameObject
/// (mark-replaced / crumble / destroy), which need the ZNetView for ownership
/// and the transform for the effect.
///
/// The marker carries THREE links, each for a distinct purpose (not redundant):
///  - <see cref="LinkedPlayer"/> (character ZDOID): walk marker -> player within
///    a session, and detect a disconnect (the character ZDO vanishes on logout).
///  - <see cref="OwnerPlayerId"/> (stable PlayerID): match an orphan marker to a
///    RECONNECTED player, whose new character ZDO has a different ZDOID.
///  - the player's own <see cref="DownedState.Marker"/> is the reverse link.
///
/// Key names preserve the historical wire format; the owner is the sole writer.
/// </summary>
public struct MarkerState {
    private static readonly int kIsMarker = "RevivalRevived_isDownedMarker".GetStableHashCode();
    private static readonly int kOwnerPlayerId = "RevivalRevived_ownerPlayerID".GetStableHashCode();
    private static readonly int kReplacedByGrave = "RevivalRevived_replacedByGrave".GetStableHashCode();
    private static readonly int kDownedTime = "RevivalRevived_downedTime".GetStableHashCode();
    private static readonly int kOwnerName = ZDOVars.s_ownerName;
    private static readonly KeyValuePair<int, int> kPlayer = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");

    private readonly ZDO _z;
    public MarkerState(ZNetView nview) : this(nview.GetZDO()) { }
    public MarkerState(ZDO zdo) { _z = zdo; }

    /// <summary>Distinguishes the revive marker from a real loot grave.</summary>
    public bool IsMarker { get => _z.GetBool(kIsMarker); set => _z.Set(kIsMarker, value); }

    /// <summary>Vanilla world-text key, shared with real graves.</summary>
    public string OwnerName { get => _z.GetString(kOwnerName); set => _z.Set(kOwnerName, value); }

    /// <summary>Stable PlayerID of the downed player (survives logout).</summary>
    public long OwnerPlayerId { get => _z.GetLong(kOwnerPlayerId); set => _z.Set(kOwnerPlayerId, value); }

    /// <summary>The marker -> character ZDOID link (session-scoped).</summary>
    public ZDOID LinkedPlayer { get => _z.GetZDOID(kPlayer); set => _z.Set(kPlayer, value); }

    /// <summary>Spawn-time clock; gradient fallback when the player ZDO is out of range.</summary>
    public float DownedTime { get => _z.GetFloat(kDownedTime); set => _z.Set(kDownedTime, value); }

    /// <summary>The real grave has spawned in this marker's place.</summary>
    public bool ReplacedByGrave { get => _z.GetBool(kReplacedByGrave); set => _z.Set(kReplacedByGrave, value); }

    // -------------------------------------------------------------------
    //  Lifecycle operations on a marker GameObject
    // -------------------------------------------------------------------

    /// <summary>Effect objects created by the last marker crumble (test hook).</summary>
    public static int LastCrumbleEffectCount { get; private set; }

    /// <summary>Total crumbles this session (test hook; use deltas across an action).</summary>
    public static int CrumbleEvents { get; private set; }

    /// <summary>
    /// The real grave has spawned in this marker's place: flag the marker ZDO so
    /// every client swaps its presentation locally, gap-free. The instance's
    /// Update handles hide + delayed destroy.
    /// </summary>
    public static void MarkReplaced(GameObject? marker) {
        var nview = Owned(marker);
        if (nview == null) return;
        var m = new MarkerState(nview);
        m.ReplacedByGrave = true;
    }

    /// <summary>
    /// Despawn the marker the way an emptied grave does: play the tombstone's own
    /// crumble effect (a networked vfx, so it covers the disappearance on every
    /// client) and destroy it. Used on revive, and on death when no grave spawns.
    /// </summary>
    public static void Crumble(GameObject? marker) {
        if (marker == null) return;
        var effect = GraveCrumbleEffect();
        if (effect != null) {
            var created = effect.Create(marker.transform.position, marker.transform.rotation);
            LastCrumbleEffectCount = created?.Length ?? 0;
            CrumbleEvents++;
        }
        Destroy(marker);
    }

    /// <summary>Crumble the marker linked from a player's ZDO and clear the link.</summary>
    public static void CrumbleLinked(ZNetView playerNview) {
        var player = new DownedState(playerNview);
        var markerId = player.Marker;
        if (markerId == ZDOID.None) return;
        var state = new DownedState(playerNview);
        state.Marker = ZDOID.None;
        Crumble(ZNetScene.instance.FindInstance(markerId));
    }

    /// <summary>Destroy a marker, claiming ownership first (an orphaned marker is owned by the server).</summary>
    public static void Destroy(GameObject? marker) {
        var nview = Owned(marker);
        nview?.Destroy();
    }

    /// <summary>The marker's ZNetView, ownership claimed, or null if invalid.</summary>
    private static ZNetView? Owned(GameObject? marker) {
        var nview = marker != null ? marker.GetComponent<ZNetView>() : null;
        if (nview == null || !nview.IsValid()) return null;
        if (!nview.IsOwner()) nview.ClaimOwnership();
        return nview;
    }

    private static EffectList? s_crumbleEffect;

    /// <summary>The vanilla tombstone's remove-effect (what plays when an emptied grave despawns).</summary>
    private static EffectList? GraveCrumbleEffect() {
        if (s_crumbleEffect != null) return s_crumbleEffect;
        var prefab = ZNetScene.instance != null ? MarkerPrefab.FindTombstonePrefab(ZNetScene.instance) : null;
        var tomb = prefab != null ? prefab.GetComponent<TombStone>() : null;
        if (tomb != null && tomb.m_removeEffect != null && tomb.m_removeEffect.m_effectPrefabs.Length > 0) {
            s_crumbleEffect = tomb.m_removeEffect;
        } else {
            Plugin.Logger.LogWarning("MarkerState: no grave crumble effect found");
        }
        return s_crumbleEffect;
    }
}
