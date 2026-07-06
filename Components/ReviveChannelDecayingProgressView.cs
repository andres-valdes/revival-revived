using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The revive-channel fields on a MARKER's ZDO, as one typed view. Rather than
/// replicate a per-frame progress value, we store the timeline as an anchor: the
/// accumulated channel seconds as of <see cref="AnchorTime"/>, and whether the
/// channel was filling (accumulating) or idle (decaying) from that point. The
/// owner writes this only when a channel starts or stops; every client computes
/// the live fraction locally from the anchor and the synced world clock, so
/// revive never ticks over the network.
///
/// Key names keep the historical "RevivalRevived_" wire prefix; the owner is the
/// sole writer.
/// </summary>
public struct ReviveChannelDecayingProgressView {
    private static readonly int kAnchorTime = "RevivalRevived_reviveAnchorTime".GetStableHashCode();
    private static readonly int kAnchorSeconds = "RevivalRevived_reviveAnchorSeconds".GetStableHashCode();
    private static readonly int kChanneling = "RevivalRevived_reviveChanneling".GetStableHashCode();

    private readonly ZDO _z;
    public ReviveChannelDecayingProgressView(ZNetView nview) : this(nview.GetZDO()) { }
    public ReviveChannelDecayingProgressView(ZDO zdo) { _z = zdo; }

    /// <summary>Synced world time of the last channel transition.</summary>
    public float AnchorTime { get => _z.GetFloat(kAnchorTime); set => _z.Set(kAnchorTime, value); }

    /// <summary>Accumulated channel seconds as of <see cref="AnchorTime"/>.</summary>
    public float AnchorSeconds { get => _z.GetFloat(kAnchorSeconds); set => _z.Set(kAnchorSeconds, value); }

    /// <summary>Whether the channel was filling (true) or decaying (false) from the anchor.</summary>
    public bool Channeling { get => _z.GetBool(kChanneling); set => _z.Set(kChanneling, value); }
}
