namespace ReviveAllies.Components.States;

/// <summary>
/// Not downed. Clears any leftover give-up on entry, and rests here until the
/// player is downed (the CheckDeath patch sets the Downed flag; we react to it).
/// </summary>
public sealed class AliveState : IState {
    private readonly Player _p;
    public AliveState(Player p) => _p = p;

    public void Enter() => _p.GetComponent<GiveUp>().Reset();
    public IState? Tick() => _p.IsDowned() ? new WaitingState(_p) : null;
    public void Exit() { }
}
