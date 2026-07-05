namespace ReviveAllies.Components;

/// <summary>
/// One state in a <see cref="StateMachine"/>. All behaviour lives here: the
/// state does its per-frame work in <see cref="Tick"/> and returns the state to
/// move to next (or itself / null to stay).
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
/// A minimal state machine with NO business logic of its own: it holds the
/// current state, forwards Enter/Tick/Exit, and swaps states when a Tick asks
/// for a transition. All decisions and behaviour belong to the <see cref="IState"/>s.
/// </summary>
public sealed class StateMachine {
    public IState? Current { get; private set; }

    /// <summary>Switch to <paramref name="next"/> (Exit the old, Enter the new).</summary>
    public void Change(IState? next) {
        if (ReferenceEquals(Current, next)) return;
        Current?.Exit();
        Current = next;
        Current?.Enter();
    }

    /// <summary>Advance the current state; apply any transition it requests.</summary>
    public void Tick() {
        var next = Current?.Tick();
        if (next != null && !ReferenceEquals(next, Current)) Change(next);
    }

    public bool Is<T>() where T : IState => Current is T;
}
