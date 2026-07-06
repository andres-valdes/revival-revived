using System;
using UnityEngine;

namespace ReviveAllies.Components;

/// <summary>
/// The give-up hold as a purely client-side decaying timer -- no ZDO, since only
/// the local downed player ever sees or drives it. It fills while they hold Use
/// and decays when they release; <see cref="Finished"/> fires once it empties so
/// the injected <see cref="ProgressUI"/> despawns.
/// </summary>
public class GiveUpDecayingProgress : IDecayingProgress {
    /// <summary>Give-up seconds lost per real second while not held.</summary>
    private const float DecayRate = 2f;

    private float m_seconds;
    private bool m_active;

    public event Action? Finished;

    public float Fraction => Mathf.Clamp01(m_seconds / GiveUp.Duration);

    /// <summary>True once the full hold is reached -- the caller should end the window.</summary>
    public bool Full => m_seconds >= GiveUp.Duration;

    /// <summary>One held frame: fill.</summary>
    public void Channel(float dt) {
        m_seconds += dt;
        if (m_seconds > 0.01f) m_active = true;
    }

    /// <summary>One idle frame: decay toward zero, firing Finished when it empties.</summary>
    public void Decay(float dt) {
        if (m_seconds <= 0f) return;
        m_seconds = Mathf.Max(0f, m_seconds - dt * DecayRate);
        if (m_seconds <= 0f) Empty();
    }

    /// <summary>Clear immediately (completion / revive / leaving downed): always fires Finished.</summary>
    public void Reset() {
        m_seconds = 0f;
        m_active = false;
        Finished?.Invoke();
    }

    private void Empty() {
        if (m_active) { m_active = false; Finished?.Invoke(); }
    }
}
