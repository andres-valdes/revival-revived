namespace ReviveAllies.Components.States;

// The reviver-side machine (hosted by ReviverStateMachine on the Player) mirrors
// the downed-side machine: a logic-free StateMachine, states that hold the
// behaviour, and a transition driven by the recency of an input signal -- here the
// local player's interact requests (recorded on the ReviveRequest component); on
// the downed side, the reviver's channel pings.

/// <summary>The local player isn't channeling a revive.</summary>
public sealed class ReviverIdleState : IState {
    private readonly Player _p;
    private readonly ReviveRequest _req;

    public ReviverIdleState(Player p) {
        _p = p;
        _req = p.GetComponent<ReviveRequest>();
    }

    public void Enter() { }
    public void Exit() { }

    public IState? Tick() =>
        _req.WantsToChannel && _req.Target != null ? new ReviverChannelingState(_p) : null;
}
