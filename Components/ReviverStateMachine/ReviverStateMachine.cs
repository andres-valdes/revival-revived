using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The reviver-side state machine, present on every Player. It holds no business
/// logic: it only declares the initial state and ticks the machine. The channeling
/// behaviour lives in the states (<see cref="States.ReviverIdleState"/> /
/// <see cref="States.ReviverChannelingState"/>) and the <see cref="ReviveRequest"/>
/// component they read off the Player. Only the local player ever receives
/// requests, so only their machine leaves the idle state.
/// </summary>
public class ReviverStateMachine : StateMachine {
    protected override IState CreateInitialState() => new States.ReviverIdleState(GetComponent<Player>());

    private void Update() => Tick();
}
