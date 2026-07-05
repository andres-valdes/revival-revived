using UnityEngine;

namespace ReviveAllies.Components.States;

/// <summary>Not downed. The machine rests here until the player is downed.</summary>
public sealed class AliveState : IState {
    private readonly DownedController _c;
    public AliveState(DownedController c) => _c = c;
    public void Enter() { }
    public IState? Tick() => null;
    public void Exit() { }
}

/// <summary>
/// Downed and idle: nobody is channeling a revive and the player isn't giving
/// up. Watches for either, and decays any leftover revive/give-up progress.
/// (Window expiry -> death is enforced by the controller + CheckDeath patch.)
/// </summary>
public sealed class WaitingState : IState {
    private readonly DownedController _c;
    public WaitingState(DownedController c) => _c = c;

    public void Enter() { }
    public void Exit() { }

    public IState? Tick() {
        if (_c.GiveUpHeld()) return new GivingUpState(_c);
        if (_c.IsChanneling) return new RevivingState(_c);

        // Decay leftover progress back toward zero.
        if (_c.ReviveSeconds > 0f) {
            _c.ReviveSeconds = Mathf.Max(0f, _c.ReviveSeconds - Time.deltaTime * 2f);
            _c.PublishReviveProgress(_c.ReviveSeconds);
        }
        if (_c.GiveUpSeconds > 0f) {
            _c.GiveUpSeconds = Mathf.Max(0f, _c.GiveUpSeconds - Time.deltaTime * 2f);
            _c.SetGiveUpFraction(_c.GiveUpSeconds);
        }
        return null;
    }
}

/// <summary>
/// A reviver is actively channeling. Accumulate progress, pause the bleed-out
/// window, publish progress, and revive at completion. Channel stops -> back to
/// Waiting; the player starts giving up -> GivingUp (give-up wins).
/// </summary>
public sealed class RevivingState : IState {
    private readonly DownedController _c;
    public RevivingState(DownedController c) => _c = c;

    public void Enter() { }
    public void Exit() { }

    public IState? Tick() {
        if (_c.GiveUpHeld()) return new GivingUpState(_c);
        if (!_c.IsChanneling) return new WaitingState(_c);

        // A single press completes a press-mode revive.
        if (Plugin.RevivePressMode) { _c.Revive(); return null; }

        var zdo = _c.Zdo;
        zdo.DownedTime += Time.deltaTime; // pause the bleed-out window while channeling
        _c.ReviveSeconds += Time.deltaTime;
        if (_c.ReviveSeconds >= Plugin.ReviveDuration) { _c.Revive(); return null; }

        _c.PublishReviveProgress(_c.ReviveSeconds);
        return null;
    }
}

/// <summary>
/// The downed player is holding Use to give up: accumulate the red progress and,
/// at completion, end the window early (routing through the normal expiry ->
/// death path). Releasing -> back to Waiting (the hold decays there).
/// </summary>
public sealed class GivingUpState : IState {
    private readonly DownedController _c;
    public GivingUpState(DownedController c) => _c = c;

    public void Enter() { }
    public void Exit() { }

    public IState? Tick() {
        if (!_c.GiveUpHeld()) return new WaitingState(_c);

        _c.GiveUpSeconds += Time.deltaTime;
        _c.SetGiveUpFraction(_c.GiveUpSeconds);
        if (_c.GiveUpSeconds >= DownedController.GiveUpDuration) { _c.ForceExpire(); return null; }
        return null;
    }
}
