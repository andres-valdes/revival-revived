namespace RevivalRevived.Components;

/// <summary>
/// Routed-RPC names for the downed/revive feature. ZDO field access is fully
/// typed via the ZdoTyped schemas (<see cref="DownedPlayerZdo"/> and
/// <see cref="DownedMarker.View"/>), which own their keys and hashes.
/// </summary>
public static class DownedKeys {
    /// <summary>Broadcast: play the downed poof at the player (state is ZDO-driven).</summary>
    public const string RpcOnDowned = "RevivalRevived_OnDowned";
    /// <summary>Reviver -> owner: "someone is channeling", pauses the bleed-out window.</summary>
    public const string RpcChannel = "RevivalRevived_Channel";
    /// <summary>Reviver -> owner: the (peer-authoritative) hold completed, execute the revive.</summary>
    public const string RpcDoRevive = "RevivalRevived_DoRevive";
}
