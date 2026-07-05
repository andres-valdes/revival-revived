using System.Collections.Generic;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The downed-state fields on a PLAYER's ZDO, as one typed view. This is the
/// single description of that ZDO's shape -- every reader/writer goes through
/// here instead of loose <c>GetZDO().GetBool(hash)</c> calls. It is a thin value
/// wrapper over the live ZDO: reads and writes hit the networked object directly.
///
/// Key names preserve the historical "RevivalRevived_" wire format for save and
/// network compatibility. Owner-only for writes (as with any ZDO).
///
/// Writes go through a local (a struct rvalue can't be assigned into):
/// <code>var s = player.State(); s.Downed = true;</code>
/// </summary>
public struct DownedState {
    private static readonly int kDowned = "RevivalRevived_downed".GetStableHashCode();
    private static readonly int kDownedTime = "RevivalRevived_downedTime".GetStableHashCode();
    private static readonly int kReviveProgress = "RevivalRevived_reviveProgress".GetStableHashCode();
    private static readonly int kGravePending = "RevivalRevived_graveReplacePending".GetStableHashCode();
    private static readonly int kGravePos = "RevivalRevived_graveReplacePos".GetStableHashCode();
    private static readonly KeyValuePair<int, int> kMarker = ZDO.GetHashZDOID("RevivalRevived_markerZDOID");

    private readonly ZDO _z;
    public DownedState(ZNetView nview) : this(nview.GetZDO()) { }
    public DownedState(ZDO zdo) { _z = zdo; }

    /// <summary>The replicated downed flag: the whole state transition.</summary>
    public bool Downed { get => _z.GetBool(kDowned); set => _z.Set(kDowned, value); }

    /// <summary>World-time the player went down; shifted forward to pause the window.</summary>
    public float DownedTime { get => _z.GetFloat(kDownedTime); set => _z.Set(kDownedTime, value); }

    /// <summary>Owner-authoritative revive channel progress 0-1.</summary>
    public float ReviveProgress { get => _z.GetFloat(kReviveProgress); set => _z.Set(kReviveProgress, value); }

    /// <summary>Set when a downed player dies: the real grave should replace the marker in place.</summary>
    public bool GraveReplacePending { get => _z.GetBool(kGravePending); set => _z.Set(kGravePending, value); }

    /// <summary>Where the marker stood when the downed player died; the real grave spawns exactly there.</summary>
    public Vector3 GraveReplacePos { get => _z.GetVec3(kGravePos, Vector3.zero); set => _z.Set(kGravePos, value); }

    /// <summary>The player -> marker link (the canonical direction).</summary>
    public ZDOID Marker { get => _z.GetZDOID(kMarker); set => _z.Set(kMarker, value); }
}
