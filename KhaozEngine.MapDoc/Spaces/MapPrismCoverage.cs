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
            var raw = MapOccupiedPartitions.Build(view, min, max, limits, carve: false);
            var work = new MapWaterWorkBudget(limits.MaxPairChecks);
            foreach (var prism in prisms)
            {
                var domain = domains[prism.Id];
                var parents = raw.Where(p => p.SpaceId == prism.Parent.Id).ToArray();
                var (lo, hi) = MapPrismGeometry.Heights(view, prism);
                for (int i = 0; i < domain.Pieces.Count; i++)
                    foreach (var convex in domain.ConvexParts(i, work))
                    {
                        var polygon = convex.Select(p => p.TryNarrow(out var point) ? point : throw new MapExactOverflowException()).ToArray();
                        var uncovered = new List<IReadOnlyList<MapExactXz>> { polygon };
                        foreach (var parent in parents)
                        {
                            work.Charge(1);
                            var clip = MapOccupiedPartitions.Intersect(polygon, parent.Polygon);
                            if (!MapOccupiedPartitions.Positive(clip)) continue;
                            if (clip.Any(p => MapSpaceGeometry.Min(parent.Top(p) ?? hi, hi).CompareTo(MapSpaceGeometry.Max(parent.Bottom(p), lo)) <= 0))
                                errors.Add($"prism '{prism.Id}': effective separation over parent refinement");
                            var next = new List<IReadOnlyList<MapExactXz>>();
                            foreach (var remaining in uncovered)
                                next.AddRange(MapOccupiedPartitions.Difference(remaining, parent.Polygon));
                            uncovered = next;
                        }
                        if (uncovered.Any(MapOccupiedPartitions.Positive)) errors.Add($"prism '{prism.Id}': outside parent footprint");
                    }
            }
            for (int i = 0; i < raw.Count; i++)
                for (int j = i + 1; j < raw.Count; j++)
                {
                    var a = raw[i]; var b = raw[j];
                    if (a.SpaceId == b.SpaceId || MapOccupiedPartitions.Ancestor(view, a.SpaceId, b.SpaceId) || MapOccupiedPartitions.Ancestor(view, b.SpaceId, a.SpaceId)) continue;
                    work.Charge(1);
                    var polygon = MapOccupiedPartitions.Intersect(a.Polygon, b.Polygon);
                    if (!MapOccupiedPartitions.Positive(polygon)) continue;
                    if (a.UpperFaces is not null || a.UpperPlane is not null)
                        polygon = MapOccupiedPartitions.Clip(polygon, p => MapOccupiedPartitions.Height(a, p, true).Subtract(MapOccupiedPartitions.Height(b, p, false)));
                    if (b.UpperFaces is not null || b.UpperPlane is not null)
                        polygon = MapOccupiedPartitions.Clip(polygon, p => MapOccupiedPartitions.Height(b, p, true).Subtract(MapOccupiedPartitions.Height(a, p, false)));
                    if (MapOccupiedPartitions.Positive(polygon) &&
                        polygon.Any(p => a.Top(p) is not { } ta || ta.CompareTo(b.Bottom(p)) > 0) &&
                        polygon.Any(p => b.Top(p) is not { } tb || tb.CompareTo(a.Bottom(p)) > 0))
                        errors.Add($"peer occupied overlap: '{a.SpaceId}' with '{b.SpaceId}'");
                }
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
