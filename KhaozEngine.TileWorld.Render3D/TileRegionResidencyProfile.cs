using System;

namespace KhaozEngine.TileWorld;

/// <summary>The representation retained for one authored region.</summary>
public enum TileRegionResidencyState
{
    /// <summary>No CPU snapshot or region-owned GPU handle is retained.</summary>
    Unloaded,
    /// <summary>Render-only far representation. It carries no picking or ordinary prop batches.</summary>
    Decor,
    /// <summary>The full interactive representation used by existing TileWorld views.</summary>
    Gameplay,
}

/// <summary>Opt-in three-state TileWorld residency in Chebyshev region distance. Regions inside
/// <see cref="GameplayRadius"/> use the full interactive representation. Regions through
/// <see cref="DecorRadius"/> use the render-only far representation. An already resident region stays as decor
/// through <see cref="UnloadRadius"/> to prevent border churn.</summary>
/// <param name="GameplayRadius">Outer inclusive radius of full interactive regions.</param>
/// <param name="DecorRadius">Outer inclusive radius of render-only regions.</param>
/// <param name="UnloadRadius">Outer inclusive hysteresis boundary for an already resident region.</param>
public sealed record TileRegionResidencyProfile(int GameplayRadius, int DecorRadius, int UnloadRadius)
{
    /// <summary>Throws when the three rings are not strictly ordered.</summary>
    /// <param name="paramName">The argument name reported by a caller validating on another API's behalf.</param>
    public void Validate(string? paramName = null)
    {
        if (GameplayRadius < 0)
            throw new ArgumentException($"GameplayRadius ({GameplayRadius}) must not be negative.", paramName);
        if (DecorRadius <= GameplayRadius)
            throw new ArgumentException(
                $"DecorRadius ({DecorRadius}) must exceed GameplayRadius ({GameplayRadius}).", paramName);
        if (UnloadRadius <= DecorRadius)
            throw new ArgumentException(
                $"UnloadRadius ({UnloadRadius}) must exceed DecorRadius ({DecorRadius}).", paramName);
    }

    /// <summary>Classifies one region relative to a focus region. A region already held by the view receives the
    /// unload hysteresis band. A missing region does not enter that band.</summary>
    /// <param name="region">Candidate region.</param>
    /// <param name="focus">Region containing the current focus.</param>
    /// <param name="currentlyResident">Whether the candidate already has state in the view.</param>
    public TileRegionResidencyState Classify(RegionCoord region, RegionCoord focus, bool currentlyResident)
    {
        int distance = Math.Max(Math.Abs(region.Rx - focus.Rx), Math.Abs(region.Rz - focus.Rz));
        if (distance <= GameplayRadius) return TileRegionResidencyState.Gameplay;
        if (distance <= DecorRadius || currentlyResident && distance <= UnloadRadius)
            return TileRegionResidencyState.Decor;
        return TileRegionResidencyState.Unloaded;
    }
}
