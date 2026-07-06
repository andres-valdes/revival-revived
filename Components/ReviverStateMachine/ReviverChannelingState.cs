namespace ReviveAllies.Components.States;

/// <summary>
/// The local player is channeling a revive. On entry it sends ONE begin edge to the
/// victim's owner; each tick it tries to show the green circle, which only appears
/// once the owner ACKs by registering the channel (the anchor comes back). On exit it
/// sends ONE end edge and tears its own circle down at once (the reviver stopped
/// reviving); the revivee's circle, by contrast, lingers and fades out. Stops when the
/// interact input lapses or the target is no longer downed.
/// </summary>
public sealed class ReviverChannelingState : IState {
    private readonly Player _p;
    private readonly ReviveRequest _req;

    public ReviverChannelingState(Player p) {
        _p = p;
        _req = p.GetComponent<ReviveRequest>();
    }

    public void Enter() => _req.SendBegin();

    public void Exit() {
        _req.SendEnd();
        _req.CloseUI();
    }

    public IState? Tick() {
        if (!_req.WantsToChannel || _req.Target == null) return new ReviverIdleState(_p);
        _req.ShowUI(); // no-op until the owner ACKs (anchor replicated back)
        return null;
    }
}
