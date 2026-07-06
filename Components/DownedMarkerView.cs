using System.Collections.Generic;

namespace ReviveAllies.Components;

/// <summary>
/// The fields on a revive-marker's ZDO, as one typed view -- the single
/// description of that ZDO's shape. The marker's lifecycle operations
/// (mark-replaced / crumble / destroy) are statics on its component,
/// <see cref="DownedMarker"/>.
///
/// The marker carries THREE links, each for a distinct purpose (not redundant):
///  - <see cref="LinkedPlayer"/> (character ZDOID): walk marker -> player within
///    a session, and detect a disconnect (the character ZDO vanishes on logout).
///  - <see cref="OwnerPlayerId"/> (stable PlayerID): match an orphan marker to a
///    RECONNECTED player, whose new character ZDO has a different ZDOID.
///  - the player's own <see cref="DownedStateMachineView.Marker"/> is the reverse link.
///
/// Key names preserve the historical wire format; the owner is the sole writer.
/// </summary>
public struct DownedMarkerView {
    private static readonly int kIsMarker = "RevivalRevived_isDownedMarker".GetStableHashCode();
    private static readonly int kOwnerPlayerId = "RevivalRevived_ownerPlayerID".GetStableHashCode();
    private static readonly int kReplacedByGrave = "RevivalRevived_replacedByGrave".GetStableHashCode();
    private static readonly int kDownedTime = "RevivalRevived_downedTime".GetStableHashCode();
    private static readonly int kOwnerName = ZDOVars.s_ownerName;
    private static readonly KeyValuePair<int, int> kPlayer = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");

    private readonly ZDO _z;
    public DownedMarkerView(ZNetView nview) : this(nview.GetZDO()) { }
    public DownedMarkerView(ZDO zdo) { _z = zdo; }

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
}
