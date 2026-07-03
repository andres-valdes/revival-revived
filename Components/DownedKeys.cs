using System.Collections.Generic;

namespace RevivalRevived.Components;

/// <summary>
/// Wire-format constants for the downed/revive feature: ZDO field hashes and
/// routed-RPC names. Constants only -- behaviour lives in the components
/// (<see cref="Revivable"/>, <see cref="DownedMarker"/>,
/// <see cref="ReviveInteractable"/>) and in <see cref="PlayerDownedExtensions"/>.
/// </summary>
public static class DownedKeys {
    // --- Routed RPCs ------------------------------------------------------
    /// <summary>Broadcast: play the downed poof at the player (state is ZDO-driven).</summary>
    public const string RpcOnDowned = "RevivalRevived_OnDowned";
    /// <summary>Reviver -> owner: "someone is channeling", pauses the bleed-out window.</summary>
    public const string RpcChannel = "RevivalRevived_Channel";
    /// <summary>Reviver -> owner: the (peer-authoritative) hold completed, execute the revive.</summary>
    public const string RpcDoRevive = "RevivalRevived_DoRevive";

    // --- Player ZDO fields (owner-written) --------------------------------
    public static readonly int Downed = "RevivalRevived_downed".GetStableHashCode();
    public static readonly int DownedTime = "RevivalRevived_downedTime".GetStableHashCode();

    // --- Marker ZDO fields -------------------------------------------------
    /// <summary>Flags a tombstone as a downed marker rather than a real grave.</summary>
    public static readonly int IsDownedMarker = "RevivalRevived_isDownedMarker".GetStableHashCode();
    /// <summary>Channel progress 0-1, written peer-authoritatively by the reviving client.</summary>
    public static readonly int ReviveProgress = "RevivalRevived_reviveProgress".GetStableHashCode();
    /// <summary>Stable PlayerID of the downed player (survives logout, unlike the character ZDOID).</summary>
    public static readonly int OwnerPlayerID = "RevivalRevived_ownerPlayerID".GetStableHashCode();

    // --- ZDOID cross-links (long + uint pairs, Valheim's convention) -------
    public static readonly KeyValuePair<int, int> MarkerZdoId = ZDO.GetHashZDOID("RevivalRevived_markerZDOID");
    public static readonly KeyValuePair<int, int> PlayerZdoId = ZDO.GetHashZDOID("RevivalRevived_playerZDOID");
}
