namespace RevivalRevived.Components;

/// <summary>
/// Routed-RPC names for the downed/revive feature. ZDO field access is typed
/// via the ZdoTyped schemas (<see cref="DownedPlayerZdo"/> and
/// <see cref="DownedMarker.View"/>); the RPCs are plain named routes.
/// </summary>
public static class DownedKeys {
    /// <summary>Broadcast: play the downed poof at the player (state is ZDO-driven).</summary>
    public const string RpcOnDowned = "RevivalRevived_OnDowned";
    /// <summary>Reviver -> owner: "I am channeling a revive." The owner accumulates progress and revives itself.</summary>
    public const string RpcChannel = "RevivalRevived_Channel";
}
