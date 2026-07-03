using ZdoTyped;

namespace RevivalRevived.Components;

/// <summary>
/// Typed view over the downed-state fields on a PLAYER's ZDO (owner-written).
/// Key names match the wire format the mod has always used
/// ("RevivalRevived_downed" etc.), so saves and mixed versions stay compatible.
/// </summary>
[ZdoSchema("RevivalRevived")]
public partial struct DownedPlayerZdo {
    /// <summary>The replicated downed flag: the whole state transition.</summary>
    [ZdoField(Name = "downed")] public partial bool Downed { get; set; }

    /// <summary>World-time the player went down; shifted forward to pause the window.</summary>
    [ZdoField(Name = "downedTime")] public partial float DownedTime { get; set; }

    /// <summary>Set when a downed player dies: the real grave should replace the marker (no drop-in pop).</summary>
    [ZdoField(Name = "graveReplacePending")] public partial bool GraveReplacePending { get; set; }

    /// <summary>Where the marker stood when the downed player died; the real grave spawns exactly there.</summary>
    [ZdoField(Name = "graveReplacePos")] public partial UnityEngine.Vector3 GraveReplacePos { get; set; }

    /// <summary>Cross-link to this player's green marker.</summary>
    [ZdoField(Name = "markerZDOID")] public partial ZDOID Marker { get; set; }
}
