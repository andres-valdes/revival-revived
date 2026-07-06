using UnityEngine;

namespace ReviveAllies.Components.States;

/// <summary>
/// Downed and idle: nobody is channeling a revive and the player isn't giving up.
/// Watches for either, keeps health at 0 once the window has expired (so the
/// CheckDeath patch fires the death even if a downed body regens), and decays the
/// client-side give-up timer. The revive timer decays on its own (every client
/// computes it from the marker anchor). Revive/expiry clears the Downed flag; we
/// react by returning to Alive.
/// </summary>
public sealed class WaitingState : IState {
    private readonly Player _p;
    private readonly GiveUp _giveUp;
    private readonly ChannelSignal _channel;

    public WaitingState(Player p) {
        _p = p;
        _giveUp = p.GetComponent<GiveUp>();
        _channel = p.GetComponent<ChannelSignal>();
    }

    public void Enter() { }
    public void Exit() { }

    public IState? Tick() {
        if (!_p.IsDowned()) return new AliveState(_p);
        if (_giveUp.Held()) return new GivingUpState(_p);
        if (_channel.IsChanneling) return new RevivingState(_p);

        // Keep health at 0 past expiry so CheckDeath fires (WaitingState already
        // means not channeling; a downed body can otherwise slowly regen).
        if (_p.IsReviveWindowExpired() && _p.GetHealth() > 0f) _p.SetHealth(0f);

        _giveUp.Decay(Time.deltaTime); // drain leftover give-up (fires Finished at 0)
        return null;
    }
}
