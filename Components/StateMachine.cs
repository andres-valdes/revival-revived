using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// One state in a <see cref="StateMachine"/>. All behaviour lives here: the state
/// does its per-frame work in <see cref="Tick"/> and returns the state to move to
/// next (or itself / null to stay).
/// </summary>
public interface IState {
    /// <summary>Called once when this state becomes current.</summary>
    void Enter();

    /// <summary>Per-frame work; return the next state, or null to stay.</summary>
    IState? Tick();

    /// <summary>Called once when this state stops being current.</summary>
    void Exit();
}

/// <summary>
/// A state machine as a MonoBehaviour base with NO business logic of its own: it
/// holds the current state, swaps states on transition, and ticks the active
/// state. Concrete machines just extend it to declare their initial state and an
/// Update that calls <see cref="Tick"/> (with any authority guard); all behaviour
/// lives in the <see cref="IState"/>s.
/// </summary>
public abstract class StateMachine : MonoBehaviour {
    public IState? Current { get; private set; }

    /// <summary>The state the machine starts in.</summary>
    protected abstract IState CreateInitialState();

    protected virtual void Awake() => Change(CreateInitialState());

    /// <summary>Switch to <paramref name="next"/> (Exit the old, Enter the new).</summary>
    private void Change(IState? next) {
        if (ReferenceEquals(Current, next)) return;
        Current?.Exit();
        Current = next;
        Current?.Enter();
    }

    /// <summary>Advance the active state; apply any transition it requests.</summary>
    protected void Tick() {
        var next = Current?.Tick();
        if (next != null && !ReferenceEquals(next, Current)) Change(next);
    }
}
