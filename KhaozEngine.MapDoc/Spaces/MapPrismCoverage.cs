using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Room ancestry, storey ownership and exact containment in the canonical variable-bound parent.</summary>
public static class MapPrismCoverage
{
    public static IReadOnlyList<string> Validate(MapScopedSurfaces view, MapRefinementLimits limits)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        var prisms = MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (prisms.Length == 0) return Array.Empty<string>();
        var errors = new List<string>();
        var domains = new Dictionary<string, MapWaterDomain>(StringComparer.Ordinal);
        foreach (var prism in prisms)
        {
            try
            {
                var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Space);
                _ = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Parent);
                if (space.Kind != MapSpaceKind.Room || space.Parent != prism.Parent)
                    throw new MapDocumentException("prism parent mismatch");
                if (MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>().Any(f => f.Space.Id == space.Id))
                    throw new MapDocumentException("room occupancy requires only its prism domain");
                _ = MapSpaceGeometry.Ancestors(view, space);
                RequireStorey(view, prism);
                if (prisms.Count(p => p.Space.Id == prism.Space.Id) != 1) throw new MapDocumentException("room requires one prism");
                domains.Add(prism.Id, MapPrismGeometry.Compile(view, prism));
            }
            catch (Exception error) when (error is MapDocumentException or MapExactOverflowException or OverflowException)
            { errors.Add($"prism '{prism.Id}': {Detail(error)}"); }
        }
        if (errors.Count != 0) return errors.AsReadOnly();
        try
        {
            var min = new MapExactXz(domains.Values.Min(d => d.Min.X), domains.Values.Min(d => d.Min.Z));
            var max = new MapExactXz(domains.Values.Max(d => d.Max.X), domains.Values.Max(d => d.Max.Z));
            var partition = MapOccupiedPartitions.BuildWide(view, min, max, limits, carve: false);
            if (partition.Status != MapRefinementStatus.Complete) throw MapOccupiedPartitions.Refusal(partition.Status, partition.Detail);
            var raw = partition.Pieces;
            var work = new MapWaterWorkBudget(limits.MaxPairChecks);
            MapPrismValidationWorkspace.Reserve(checked(512L + 256L * raw.Count));
            var bounds = raw.Select(p => MapWaterPolygons.Bounds.Of(p.Polygon)).ToArray();
            // Exact bounds broad phases. A parent or peer piece whose bounds do not overlap shares no positive area, so
            // skipping it changes no result. Each candidate is charged once, in the released visiting order. One sweep
            // per parent owner covers the pieces of every room it parents. The partition above already resolved every
            // owner, so resolving them first raises nothing new.
            var owners = prisms.ToDictionary(p => p.Id, p => MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, p.Parent)), StringComparer.Ordinal);
            var bySpace = Enumerable.Range(0, raw.Count).ToLookup(i => raw[i].SpaceId, StringComparer.Ordinal);
            var near = new Dictionary<(string Prism, int Piece), List<int>>();
            foreach (var group in prisms.GroupBy(p => owners[p.Id], StringComparer.Ordinal))
            {
                using var groupWorkspace = MapPrismValidationWorkspace.Admission?.BeginPreparation();
                var parents = bySpace[group.Key].ToArray();
                var pieces = group.SelectMany(p => Enumerable.Range(0, domains[p.Id].Pieces.Count).Select(i => (Prism: p.Id, Piece: i))).ToArray();
                foreach (var (left, right) in MapWaterBoundsSweep.Cross(pieces.Select(p => domains[p.Prism].PieceBounds(p.Piece)).ToArray(),
                    parents.Select(i => bounds[i]).ToArray(), long.MaxValue, MapPrismValidationWorkspace.Admission)! )
                {
                    MapPrismValidationWorkspace.Reserve(64);
                    if (!near.TryGetValue(pieces[left], out var list)) near.Add(pieces[left], list = new());
                    list.Add(parents[right]);
                    MapPrismValidationWorkspace.Candidates(list.Count);
                }
            }
            foreach (var prism in prisms)
            {
                var domain = domains[prism.Id];
                var heights = MapPrismGeometry.Heights(view, prism);
                var lo = MapWideRational.From(heights.Lower); var hi = MapWideRational.From(heights.Upper);
                for (int i = 0; i < domain.Pieces.Count; i++)
                    foreach (var convex in domain.ConvexParts(i, work))
                    {
                        using var convexWorkspace = MapPrismValidationWorkspace.Kernel(convex.Count);
                        var polygon = convex;
                        var extent = MapWaterPolygons.Bounds.Of(polygon);
                        var uncovered = new List<IReadOnlyList<MapWideXz>> { polygon };
                        MapPrismValidationWorkspace.Candidates(uncovered.Count, uncovered: true);
                        using var fragments = MapPrismValidationWorkspace.Admission?.Workspace();
                        foreach (var parent in (near.GetValueOrDefault((prism.Id, i)) ?? []).Where(p => bounds[p].OverlapsOpen(extent)).Select(p => raw[p]))
                        {
                            work.Charge(1);
                            var clip = MapOccupiedPartitions.Intersect(polygon, parent.Polygon);
                            if (!MapOccupiedPartitions.Positive(clip)) continue;
                            if (clip.Any(p => MapWideRational.Min(parent.Top(p) ?? hi, hi).CompareTo(MapWideRational.Max(parent.Bottom(p), lo)) <= 0))
                                errors.Add($"prism '{prism.Id}': effective separation over parent refinement");
                            long nextBytes = checked(128L + uncovered.Sum(p => 128L + 256L * parent.Polygon.Count * (p.Count + parent.Polygon.Count)));
                            fragments?.Reserve(nextBytes);
                            var next = new List<IReadOnlyList<MapWideXz>>();
                            foreach (var remaining in uncovered)
                                next.AddRange(MapOccupiedPartitions.Difference(remaining, parent.Polygon));
                            uncovered = next;
                            if (fragments is not null && fragments.Bytes > nextBytes) fragments.Release(fragments.Bytes - nextBytes);
                            MapPrismValidationWorkspace.Candidates(uncovered.Count, uncovered: true);
                        }
                        if (uncovered.Any(MapOccupiedPartitions.Positive)) errors.Add($"prism '{prism.Id}': outside parent footprint");
                    }
            }
            MapPrismCoveragePeers.Validate(view, raw, bounds, work, errors);
        }
        catch (Exception error) when (error is MapDocumentException or MapExactOverflowException or OverflowException)
        { errors.Add("prism coverage: " + Detail(error)); }
        return Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    internal static void RequireStorey(MapScopedSurfaces view, MapPrismDomain prism)
    {
        var storey = view.EffectiveContext?.Storeys.SingleOrDefault(s => s.Id == prism.StoreyId)
            ?? throw new MapDocumentException("missing geometry: declared prism storey");
        if (storey.PayloadVersion != 1 || (storey.InstanceId ?? "") != prism.InstanceId)
            throw new MapDocumentException("invalid prism/storey instance ownership");
    }

    internal static void RequireStandaloneStoreys(MapDocument document)
    {
        foreach (var prism in document.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>())
        {
            var storey = document.BuildingIntent?.Storeys.SingleOrDefault(s => s.Id == prism.StoreyId)
                ?? throw new MapDocumentException("Prism names an undeclared standalone storey.");
            if (storey.PayloadVersion != 1 || prism.InstanceId != "" || storey.InstanceId is not null)
                throw new MapDocumentException("Standalone prism and storey must have standalone ownership.");
            var transfer = document.Prefabs?.Transfers.SingleOrDefault(t => t.Binding.Child.Kind == MapPrefabChildKind.Record && t.Binding.WorldId == prism.Id);
            if (transfer is not null && (transfer.Retired || transfer.Binding.Removed ||
                !document.Prefabs!.Transfers.Any(t => t.InstanceId == transfer.InstanceId && t.Binding.Child.Kind == MapPrefabChildKind.Storey &&
                    t.Binding.WorldId == storey.Id && !t.Retired && !t.Binding.Removed)))
                throw new MapDocumentException("Unpacked prism requires a matching live storey transfer.");
        }
    }

    static string Detail(Exception error) => error is MapExactOverflowException or OverflowException ||
        error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase) ? "not representable: " + error.Message : error.Message;

}
