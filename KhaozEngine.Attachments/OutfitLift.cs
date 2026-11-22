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
    readonly Dictionary<(int WornIndex, uint PartId), IReadOnlyList<ReadOnlyMemory<Vector3>>> lifted = new();

    OutfitLift(int wornCount, int regionCount)
    {
        heights = new WornHeights[wornCount];
        this.regionCount = regionCount;
    }

    /// <summary>
    /// Resolve lift for the same body and worn list used to resolve <paramref name="coverage"/>. Supply one mesh
    /// per skinned part that declares lift. The result retains only its own positions and heights.
    /// </summary>
    /// <exception cref="ArgumentException">The worn count differs from coverage, or a lifting mesh has the wrong shape.</exception>
    public static OutfitLift Resolve(BodyRegionMap body, IReadOnlyList<AttachmentSetDefinition> worn,
        OutfitCoverage coverage, IReadOnlyList<LiftingPartMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(worn);
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(meshes);
        if (coverage.WornCount != worn.Count)
            throw new ArgumentException("Coverage must describe the same worn list.", nameof(coverage));

        var ordered = new List<(int WornIndex, AttachmentLayer Layer)>();
        for (int wornIndex = 0; wornIndex < worn.Count; wornIndex++)
        {
            AttachmentSetDefinition set = worn[wornIndex] ?? throw new ArgumentNullException(nameof(worn), $"worn[{wornIndex}] is null.");
            if (set.Layer is { } layer)
                ordered.Add((wornIndex, layer));
        }
        ordered.Sort((left, right) => left.Layer.CompareTo(right.Layer));

        var remaining = new Dictionary<(int WornIndex, uint PartId), LiftingPartMesh>(meshes.Count);
        foreach (LiftingPartMesh mesh in meshes)
        {
            ArgumentNullException.ThrowIfNull(mesh, nameof(meshes));
            if (!remaining.TryAdd((mesh.WornIndex, mesh.PartId), mesh))
                throw new ArgumentException($"worn[{mesh.WornIndex}] part {mesh.PartId} has more than one lifting mesh.", nameof(meshes));
        }

        var result = new OutfitLift(worn.Count, body.Regions.Count);
        var highest = new float?[result.regionCount];
        float?[] below = highest;
        AttachmentLayer? currentLayer = null;
        foreach ((int wornIndex, AttachmentLayer layer) in ordered)
        {
            // All sets on this layer share a frozen snapshot of the strictly lower layers.
            if (layer != currentLayer)
            {
                below = (float?[])highest.Clone();
                currentLayer = layer;
            }

            RegionSet hidden = coverage.HiddenFor(wornIndex);
            ReadOnlySpan<(int Region, float Metres)> standoff = coverage.StandoffFor(wornIndex);
            var effective = new float?[result.regionCount];
            foreach ((int region, float metres) in standoff)
                if (!hidden.Contains(region))
                    effective[region] = metres;

            var largestLift = new float[result.regionCount];
            foreach (AttachmentPartDefinition part in worn[wornIndex].Parts)
            {
                if (part is not SkinnedAttachmentPartDefinition { Lift: { } lift })
                    continue;
                if (!remaining.Remove((wornIndex, part.Id), out LiftingPartMesh? mesh))
                    throw new ArgumentException($"worn[{wornIndex}] part {part.Id} is missing its lifting mesh.", nameof(meshes));

                IReadOnlyList<ReadOnlyMemory<Vector3>>? positions = OutfitPartLift.Resolve(mesh, lift.ClearanceMetres,
                    coverage.SkinnedPartRegions(wornIndex, part.Id), below, effective, largestLift, nameof(meshes));
                if (positions is not null)
                    result.lifted.Add((wornIndex, part.Id), positions);
            }

            foreach ((int region, float _) in standoff)
            {
                if (effective[region] is not { } declared)
                    continue;
                float height = declared + largestLift[region];
                effective[region] = height;
                highest[region] = MathF.Max(highest[region] ?? 0, height);
            }
            result.heights[wornIndex] = new(below, effective);
        }

        if (remaining.Count != 0)
            throw new ArgumentException("A lifting mesh has no corresponding worn part that declares lift.", nameof(meshes));
        return result;
    }

    /// <summary>True when at least one part has lifted vertices.</summary>
    public bool HasLift => lifted.Count != 0;

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

    readonly record struct WornHeights(float?[]? Below, float?[]? Effective);
}
