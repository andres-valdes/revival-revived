using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The downed-player state machine. It holds no business logic: it only declares
/// the initial state and ticks the machine on the owner. Every decision and action
/// lives in the states (<see cref="States.AliveState"/> / <see cref="States.WaitingState"/> /
/// <see cref="States.RevivingState"/> / <see cref="States.GivingUpState"/>), which
/// read their collaborators straight off the Player (<see cref="GiveUp"/>,
/// <see cref="ChannelSignal"/>, and the marker's revive timer). The down/expire
/// decision is made in the CheckDeath patch; this machine reacts to the replicated
/// Downed flag it sets.
/// </summary>
public class DownedStateMachine : StateMachine {
    private ZNetView m_nview = null!;

    protected override void Awake() {
        m_nview = GetComponent<ZNetView>();
        base.Awake();
    }

    protected override IState CreateInitialState() => new States.AliveState(GetComponent<Player>());

    private void Update() {
        if (m_nview.IsValid() && m_nview.IsOwner()) Tick(); // authority is owner-only
    }
}
