using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Rest geometry and baked lift data for one skinned material part.</summary>
/// <param name="Positions">The source rest positions in upload order.</param>
/// <param name="Indices">The triangle list in upload order, with indices local to this material part.</param>
/// <param name="Directions">One validated outward unit direction per position.</param>
/// <param name="Bearings">One validated bearing in metres per position.</param>
public readonly record struct LiftMaterialMesh(ReadOnlyMemory<Vector3> Positions, ReadOnlyMemory<uint> Indices,
    ReadOnlyMemory<Vector3> Directions, ReadOnlyMemory<float> Bearings);

/// <summary>The meshes for one skinned attachment part that declares lift.</summary>
/// <param name="WornIndex">The zero-based index of this part's set in the worn list.</param>
/// <param name="PartId">The part's ID within that set.</param>
/// <param name="Materials">The material meshes in renderer upload order.</param>
public sealed record LiftingPartMesh(int WornIndex, uint PartId, IReadOnlyList<LiftMaterialMesh> Materials);

/// <summary>The lift and effective standoffs for an outfit. Resolve once when the outfit is bound.</summary>
public sealed class OutfitLift
{
    readonly WornHeights[] heights;
    readonly int regionCount;
    readonly Dictionary<(int WornIndex, uint PartId), IReadOnlyList<ReadOnlyMemory<Vector3>>> lifted;

    internal OutfitLift(int regionCount, WornHeights[] heights,
        Dictionary<(int WornIndex, uint PartId), IReadOnlyList<ReadOnlyMemory<Vector3>>> lifted)
    {
        this.regionCount = regionCount;
        this.heights = heights;
        this.lifted = lifted;
    }

    /// <summary>
    /// Resolve lift using the body and worn set data captured by <paramref name="coverage"/>. Supply one mesh per
    /// skinned part that declares lift. The result retains only its own positions and heights.
    /// </summary>
    /// <exception cref="ArgumentException">A lifting mesh is missing, extra or has the wrong shape.</exception>
    public static OutfitLift Resolve(OutfitCoverage coverage, IReadOnlyList<LiftingPartMesh> meshes) =>
        OutfitLiftResolver.Resolve(coverage, meshes, null);

    /// <summary>
    /// Resolve lift with one optional footprint per worn set. A layered set uses its footprint when the body map
    /// digest and triangle count match, and uses its regional standoff otherwise.
    /// </summary>
    /// <exception cref="ArgumentException">The meshes or footprints do not match the worn sets.</exception>
    public static OutfitLift Resolve(OutfitCoverage coverage, IReadOnlyList<LiftingPartMesh> meshes,
        IReadOnlyList<LiftFootprint?> footprints)
    {
        ArgumentNullException.ThrowIfNull(footprints);
        return OutfitLiftResolver.Resolve(coverage, meshes, footprints);
    }

    /// <summary>True when at least one part has lifted vertices.</summary>
    public bool HasLift => lifted.Count != 0;

    /// <summary>Whether the worn set used its measured body-cell footprint.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The worn index is outside the outfit.</exception>
    public bool FootprintApplied(int wornIndex)
    {
        CheckWornIndex(wornIndex);
        return heights[wornIndex].FootprintApplied;
    }

    /// <summary>The highest effective standoff in metres from strictly lower layers, or null without a contributor.</summary>
    public float? HeightBelow(int wornIndex, int region) => At(wornIndex, region).Below?[region];

    /// <summary>The set's effective standoff in metres at a body region index, or null where the set does not contribute.</summary>
    public float? EffectiveStandoff(int wornIndex, int region) => At(wornIndex, region).Effective?[region];

    /// <summary>The part's rest positions in upload order by material, or null if the part has no lifted vertices.</summary>
    public IReadOnlyList<ReadOnlyMemory<Vector3>>? LiftedPositions(int wornIndex, uint partId)
    {
        CheckWornIndex(wornIndex);
        return lifted.GetValueOrDefault((wornIndex, partId));
    }

    WornHeights At(int wornIndex, int region)
    {
        CheckWornIndex(wornIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(region);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(region, regionCount);
        return heights[wornIndex];
    }

    void CheckWornIndex(int wornIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wornIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(wornIndex, heights.Length);
    }

    internal readonly record struct WornHeights(float?[]? Below, float?[]? Effective, bool FootprintApplied);
}
