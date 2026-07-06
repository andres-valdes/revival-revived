using System;

namespace ReviveAllies.Components;

/// <summary>
/// A 0-1 progress that fills while it is being driven and decays when it is not,
/// and raises <see cref="Finished"/> once it has emptied (decayed to zero, or its
/// backing object is gone). This is the whole contract <see cref="ProgressUI"/>
/// needs: it reads <see cref="Fraction"/> each frame and despawns on Finished.
/// </summary>
public interface IDecayingProgress {
    /// <summary>Current fill, 0-1.</summary>
    float Fraction { get; }

    /// <summary>Raised once when the progress empties, so a bound UI can despawn.</summary>
    event Action Finished;
}
