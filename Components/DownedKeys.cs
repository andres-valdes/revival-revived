using System.Collections.Generic;

namespace ReviveAllies.Components;

/// <summary>
/// ZDO field keys and routed-RPC names for the downed/revive feature. Field
/// hashes are precomputed once (Valheim's <c>GetStableHashCode</c>) and read/
/// written through the vanilla ZDO primitives (<c>GetBool</c>/<c>Set</c> etc.).
/// The "RevivalRevived_" prefix keeps them clear of vanilla and other mods.
/// </summary>
public static class DownedKeys {
    // --- Routed RPC names -------------------------------------------------
    /// <summary>Broadcast: play the downed poof at the player (state is ZDO-driven).</summary>
    public const string RpcOnDowned = "RevivalRevived_OnDowned";
    /// <summary>Reviver -> owner: "I am channeling a revive." The owner accumulates progress and revives itself.</summary>
    public const string RpcChannel = "RevivalRevived_Channel";

    // --- Player ZDO fields (owner-written) --------------------------------
    /// <summary>The replicated downed flag: the whole state transition.</summary>
    public static readonly int Downed = "RevivalRevived_downed".GetStableHashCode();
    /// <summary>World-time the player went down; shifted forward to pause the window.</summary>
    public static readonly int DownedTime = "RevivalRevived_downedTime".GetStableHashCode();
    /// <summary>Set when a downed player dies: the real grave should replace the marker (no drop-in pop).</summary>
    public static readonly int GraveReplacePending = "RevivalRevived_graveReplacePending".GetStableHashCode();
    /// <summary>Where the marker stood when the downed player died; the real grave spawns exactly there.</summary>
    public static readonly int GraveReplacePos = "RevivalRevived_graveReplacePos".GetStableHashCode();
    /// <summary>Owner-authoritative revive channel progress 0-1.</summary>
    public static readonly int ReviveProgress = "RevivalRevived_reviveProgress".GetStableHashCode();
    /// <summary>Cross-link to this player's green marker (ZDOID _u/_i pair).</summary>
    public static readonly KeyValuePair<int, int> Marker = ZDO.GetHashZDOID("RevivalRevived_markerZDOID");

    // --- Marker ZDO fields ------------------------------------------------
    /// <summary>Distinguishes the revive marker from a real loot grave.</summary>
    public static readonly int IsDownedMarker = "RevivalRevived_isDownedMarker".GetStableHashCode();
    /// <summary>Stable PlayerID of the downed player (survives logout).</summary>
    public static readonly int OwnerPlayerId = "RevivalRevived_ownerPlayerID".GetStableHashCode();
    /// <summary>The real grave has spawned in this marker's place.</summary>
    public static readonly int ReplacedByGrave = "RevivalRevived_replacedByGrave".GetStableHashCode();
    /// <summary>Cross-link back to the downed player's character ZDO (ZDOID _u/_i pair).</summary>
    public static readonly KeyValuePair<int, int> MarkerPlayer = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");
    /// <summary>Vanilla world-text key, shared with real graves.</summary>
    public static readonly int OwnerName = ZDOVars.s_ownerName;
}
