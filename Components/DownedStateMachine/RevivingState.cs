namespace ReviveAllies.Components.States;

/// <summary>
/// A reviver is actively channeling. All the accumulation, decay, window-pause and
/// the revive-at-completion live in the marker's <see cref="ReviveChannelDecayingProgress"/>
/// timer: this state just anchors the channel on enter/exit and watches for the
/// channel stopping, the player choosing to give up (give-up wins), or the revive
/// completing (the Downed flag clears -> back to Alive).
/// </summary>
public sealed class RevivingState : IState {
    private readonly Player _p;
    private readonly GiveUp _giveUp;
    private readonly ChannelSignal _channel;

    public RevivingState(Player p) {
        _p = p;
        _giveUp = p.GetComponent<GiveUp>();
        _channel = p.GetComponent<ChannelSignal>();
    }

    // Begin/End write the ZDO anchor once each; no per-frame network writes. We
    // also show the downed player their OWN green circle while being revived --
    // this state only ticks on the owner, so it's the revivee's screen (the
    // reviver gets theirs from ReviveRequest; bystanders get none). We do NOT close
    // it on exit: when the channel stops it lingers and fades out on the timer's
    // Finished (decay), so the revivee watches the built-up progress ebb away.
    public void Enter() {
        var timer = Timer();
        timer?.Begin(_channel.LastChanneler);
        if (timer != null) ProgressUI.Create(timer, DownedMarker.ReviveGreen, isGiveUp: false);
    }

    public void Exit() => Timer()?.End();

    public IState? Tick() {
        if (!_p.IsDowned()) return new AliveState(_p);
        if (_giveUp.Held()) return new GivingUpState(_p);
        if (!_channel.IsChanneling) return new WaitingState(_p);
        return null; // the timer accumulates and completes the revive on its own
    }

    /// <summary>The revive timer on this player's marker, or null if the marker isn't present.</summary>
    private ReviveChannelDecayingProgress? Timer() =>
        _p.FindDownedMarker()?.GetComponent<ReviveChannelDecayingProgress>();
}
