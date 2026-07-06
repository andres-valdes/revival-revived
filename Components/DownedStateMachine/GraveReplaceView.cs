using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The death-to-grave handoff fields on a PLAYER's ZDO, as one typed view. When a
/// downed player dies, this asks the real loot grave to take the marker's place
/// (or, if no grave spawns, the marker to crumble). It is deliberately separate
/// from <see cref="DownedStateMachineView"/> so that view stays about the downed/revive status
/// itself; the two are unrelated concerns that merely share the player ZDO.
///
/// Key names keep the historical "RevivalRevived_" wire prefix; the owner writes.
/// </summary>
public struct GraveReplaceView {
    private static readonly int kPending = "RevivalRevived_graveReplacePending".GetStableHashCode();
    private static readonly int kPos = "RevivalRevived_graveReplacePos".GetStableHashCode();

    private readonly ZDO _z;
    public GraveReplaceView(ZNetView nview) : this(nview.GetZDO()) { }
    public GraveReplaceView(ZDO zdo) { _z = zdo; }

    /// <summary>Set when a downed player dies: the real grave should replace the marker in place.</summary>
    public bool Pending { get => _z.GetBool(kPending); set => _z.Set(kPending, value); }

    /// <summary>Where the marker stood when the downed player died; the real grave spawns exactly there.</summary>
    public Vector3 Pos { get => _z.GetVec3(kPos, Vector3.zero); set => _z.Set(kPos, value); }
}
