using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

/// <summary>Resolves a layer at a time and records only the results needed after binding.</summary>
internal static class OutfitLiftResolver
{
    internal static OutfitLift Resolve(OutfitCoverage coverage, IReadOnlyList<LiftingPartMesh> meshes,
        IReadOnlyList<LiftFootprint?>? footprints)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(meshes);

        var ordered = new List<(int WornIndex, AttachmentLayer Layer)>();
        for (int wornIndex = 0; wornIndex < coverage.WornCount; wornIndex++)
            if (coverage.LayerFor(wornIndex) is { } layer)
                ordered.Add((wornIndex, layer));
        ordered.Sort((left, right) => left.Layer.CompareTo(right.Layer));

        var remaining = new Dictionary<(int WornIndex, uint PartId), LiftingPartMesh>(meshes.Count);
        foreach (LiftingPartMesh mesh in meshes)
        {
            ArgumentNullException.ThrowIfNull(mesh, nameof(meshes));
            if (!remaining.TryAdd((mesh.WornIndex, mesh.PartId), mesh))
                throw new ArgumentException($"worn[{mesh.WornIndex}] part {mesh.PartId} has more than one lifting mesh.", nameof(meshes));
        }

        LiftFootprint?[]? applicable = SelectFootprints(coverage, footprints, out int bodyTriangleCount);
        // With only regional standoffs, every cell of a region has the same height. The region field suffices.
        using OutfitLiftCellHeights? cells = applicable is null ? null : new(coverage.Body, bodyTriangleCount);
        int regionCount = coverage.Body.Regions.Count;
        var heights = new OutfitLift.WornHeights[coverage.WornCount];
        var lifted = new Dictionary<(int WornIndex, uint PartId), IReadOnlyList<ReadOnlyMemory<Vector3>>>();
        var highestByRegion = new float?[regionCount];
        float?[] belowByRegion = highestByRegion;
        AttachmentLayer? currentLayer = null;
        foreach ((int wornIndex, AttachmentLayer layer) in ordered)
        {
            // Maxima over the cell field reduce through the sets' region maxima. Freeze both views together.
            if (layer != currentLayer)
            {
                belowByRegion = (float?[])highestByRegion.Clone();
                cells?.Freeze();
                currentLayer = layer;
            }

            LiftFootprint? footprint = applicable?[wornIndex];
            var effective = new float?[regionCount];
            ResolveSet(coverage, wornIndex, footprint, cells, belowByRegion, effective, remaining, lifted);

            for (int region = 0; region < regionCount; region++)
                if (effective[region] is { } height)
                    highestByRegion[region] = MathF.Max(highestByRegion[region] ?? float.NegativeInfinity, height);
            heights[wornIndex] = new(belowByRegion, effective, footprint is not null);
        }

        if (remaining.Count != 0)
            throw new ArgumentException("A lifting mesh has no corresponding worn part that declares lift.", nameof(meshes));
        return new OutfitLift(regionCount, heights, lifted);
    }

    static void ResolveSet(OutfitCoverage coverage, int wornIndex, LiftFootprint? footprint,
        OutfitLiftCellHeights? cells, float?[] belowByRegion, float?[] effective,
        Dictionary<(int, uint), LiftingPartMesh> meshes,
        Dictionary<(int, uint), IReadOnlyList<ReadOnlyMemory<Vector3>>> lifted)
    {
        RegionSet hidden = coverage.HiddenFor(wornIndex);
        ReadOnlySpan<(int Region, float Metres)> standoff = coverage.StandoffFor(wornIndex);
        float[] largestLift = footprint is null && !standoff.IsEmpty ? new float[effective.Length] : [];

        foreach (OutfitCoverage.SkinnedPart part in coverage.SkinnedPartsFor(wornIndex))
        {
            LiftFootprintPart? partFootprint = footprint?.PartById(part.Id);
            float[]? triangleLifts = null;
            if (part.LiftClearanceMetres is { } clearance)
            {
                if (!meshes.Remove((wornIndex, part.Id), out LiftingPartMesh? mesh))
                    throw new ArgumentException($"worn[{wornIndex}] part {part.Id} is missing its lifting mesh.", nameof(meshes));

                var placement = OutfitPartLift.Resolve(mesh, clearance, part.Regions, belowByRegion, cells,
                    partFootprint, largestLift, nameof(meshes));
                if (placement.Positions is { } positions)
                    lifted.Add((wornIndex, part.Id), positions);
                triangleLifts = placement.TriangleLifts;
            }

            if (partFootprint is not null)
                AddSkinnedPart(cells!, part.Regions, hidden, partFootprint, triangleLifts, effective);
        }

        if (footprint is not null)
        {
            foreach (uint id in coverage.RigidPartIdsFor(wornIndex))
                if (!coverage.RigidPartHidden(wornIndex, id))
                    foreach (LiftFootprintTriangle triangle in footprint.PartById(id)!.Triangles)
                        cells!.AddTriangle(triangle, 0, effective);
        }
        else if (!standoff.IsEmpty)
        {
            foreach ((int region, float declared) in standoff)
                if (!hidden.Contains(region) && (cells is null || cells.HasCells(region)))
                    effective[region] = declared + largestLift[region];
            cells?.AddRegional(effective);
        }
    }

    static void AddSkinnedPart(OutfitLiftCellHeights cells, IReadOnlyList<RegionRun> runs, RegionSet hidden,
        LiftFootprintPart footprint, float[]? triangleLifts, Span<float?> effective)
    {
        int triangle = 0;
        foreach (RegionRun run in runs)
        {
            int end = triangle + run.TriangleCount;
            if (!hidden.Contains(run.Region))
                for (; triangle < end; triangle++)
                    cells.AddTriangle(footprint.Triangles[triangle], triangleLifts?[triangle] ?? 0, effective);
            triangle = end;
        }
    }

    static LiftFootprint?[]? SelectFootprints(OutfitCoverage coverage, IReadOnlyList<LiftFootprint?>? footprints,
        out int bodyTriangleCount)
    {
        bodyTriangleCount = 0;
        if (footprints is null)
            return null;
        if (footprints.Count != coverage.WornCount)
            throw new ArgumentException("Supply one optional footprint per worn set.", nameof(footprints));

        BodyRegionMap body = coverage.Body;
        long triangleCount = 0;
        for (int part = 0; part < body.Parts.Count; part++)
            triangleCount += body.TriangleCount(part);

        LiftFootprint?[]? applicable = null;
        for (int wornIndex = 0; wornIndex < footprints.Count; wornIndex++)
        {
            LiftFootprint? footprint = footprints[wornIndex];
            if (footprint is null || coverage.LayerFor(wornIndex) is null ||
                footprint.BodyRegionMapSha256 != body.Sha256 || footprint.BodyTriangleCount != triangleCount)
                continue;
            CheckFootprintShape(coverage, wornIndex, footprint, nameof(footprints));
            (applicable ??= new LiftFootprint?[coverage.WornCount])[wornIndex] = footprint;
            bodyTriangleCount = footprint.BodyTriangleCount;
        }

        return applicable;
    }

    static void CheckFootprintShape(OutfitCoverage coverage, int wornIndex, LiftFootprint footprint, string parameter)
    {
        var rigidIds = coverage.RigidPartIdsFor(wornIndex);
        var skinnedParts = coverage.SkinnedPartsFor(wornIndex);
        if (footprint.Parts.Count != rigidIds.Count + skinnedParts.Count)
            throw new ArgumentException($"worn[{wornIndex}] footprint has a different number of parts from the set.", parameter);

        // Only skinned parts have triangle counts in the coverage. A binder can check rigid counts against the upload.
        foreach (uint id in rigidIds)
            if (footprint.PartById(id) is null)
                throw MissingPart(wornIndex, id, parameter);

        foreach (OutfitCoverage.SkinnedPart part in skinnedParts)
        {
            LiftFootprintPart measured = footprint.PartById(part.Id) ?? throw MissingPart(wornIndex, part.Id, parameter);
            int triangleCount = 0;
            foreach (RegionRun run in part.Regions)
                triangleCount += run.TriangleCount;
            if (measured.TriangleCount != triangleCount)
                throw new ArgumentException($"worn[{wornIndex}] part {part.Id} has a different footprint triangle count from its region runs.", parameter);
        }
    }

    static ArgumentException MissingPart(int wornIndex, uint id, string parameter) =>
        new($"worn[{wornIndex}] footprint does not contain part {id}.", parameter);
}
