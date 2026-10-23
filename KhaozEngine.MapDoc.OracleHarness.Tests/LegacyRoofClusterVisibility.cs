using System;
using System.Collections.Generic;
using KhaozEngine.Terrain;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Detached source observer/plane/footprint decisions in the actual source build order.</summary>
internal sealed class LegacyRoofClusterVisibility(PropClusterKey key, long generation, IReadOnlyList<bool> hidden) : IPropClusterVisibility
{
    public bool Controls(PropClusterKey candidate) => candidate == key;
    public int PlacementCount(PropClusterKey candidate, long sourceGeneration)
    {
        if (!Controls(candidate) || sourceGeneration != generation) throw new InvalidOperationException("Source roof generation unavailable.");
        return hidden.Count;
    }
    public bool IsHidden(PropClusterKey candidate, long sourceGeneration, int placementIndex)
    { _ = PlacementCount(candidate, sourceGeneration); return hidden[placementIndex]; }
}
