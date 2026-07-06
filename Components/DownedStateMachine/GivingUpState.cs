using UnityEngine;

namespace ReviveAllies.Components.States;

/// <summary>
/// The downed player is holding Use to give up: the client-side give-up timer
/// fills, and at completion we end the window early (routing through the normal
/// expiry -> death path). Releasing -> back to Waiting (the timer decays there).
/// The red circle is spawned on entry and despawns on the timer's Finished.
/// </summary>
public sealed class GivingUpState : IState {
    private readonly Player _p;
    private readonly GiveUp _giveUp;

    public GivingUpState(Player p) {
        _p = p;
        _giveUp = p.GetComponent<GiveUp>();
    }

    public void Enter() => _giveUp.ShowUI();
    public void Exit() { }

    public IState? Tick() {
        if (!_p.IsDowned()) return new AliveState(_p);
        if (!_giveUp.Held()) return new WaitingState(_p);

        if (_giveUp.Channel(Time.deltaTime)) {
            // Give-up complete: end the window now. Expiring the clock routes
            // through the normal CheckDeath expiry -> death path (the marker
            // becomes the real grave).
            var state = _p.State();
            state.DownedTime = (float)ZNet.instance.GetTimeSeconds() - Plugin.ReviveWindow - 1f;
            if (_p.GetHealth() > 0f) _p.SetHealth(0f);
            _giveUp.Reset();
            Plugin.Logger.LogInfo($"{_p.GetPlayerName()} gave up");
        }
        return null;
    }
}
