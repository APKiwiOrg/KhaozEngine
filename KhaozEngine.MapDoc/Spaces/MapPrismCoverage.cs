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
    public static IReadOnlyList<string> Validate(MapScopedSurfaces view, MapRefinementLimits limits) => Validate(view, limits, null);

    // Findings remain additive until refinement and water work supply every failure.
    internal static IReadOnlyList<string> Validate(MapScopedSurfaces view, MapRefinementLimits limits,
        List<MapDocumentFinding>? typedFindings, List<MapSpaceDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(limits);
        var prisms = MapSpaceGeometry.Records(view).OfType<MapPrismDomain>().OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        if (prisms.Length == 0) return Array.Empty<string>();
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        var domains = new Dictionary<string, MapWaterDomain>(StringComparer.Ordinal);
        foreach (var prism in prisms)
        {
            try
            {
                var space = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Space);
                _ = MapSpaceGeometry.Record<MapSpaceDoc>(view, prism.Parent);
                if (space.Kind != MapSpaceKind.Room || space.Parent != prism.Parent)
                    throw new MapDocumentException(space.Kind != MapSpaceKind.Room ? MapDocumentRefusal.ReferenceKindMismatch
                        : MapDocumentRefusal.SpacePrismParentMismatch, "prism parent mismatch")
                    { RecordId = prism.Id, SpaceId = space.Id, RelatedRecordId = prism.Parent.Id,
                        FieldPath = space.Kind != MapSpaceKind.Room ? "Kind" : "Parent",
                        EnumType = space.Kind != MapSpaceKind.Room ? typeof(MapSpaceKind) : null,
                        ActualInteger = space.Kind != MapSpaceKind.Room ? (int)space.Kind : null };
                if (MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>().Any(f => f.Space.Id == space.Id))
                    throw new MapDocumentException(MapDocumentRefusal.SpaceRoomOccupancyInvalid, "room occupancy requires only its prism domain")
                    { RecordId = prism.Id, SpaceId = space.Id, FieldPath = "Footprints" };
                _ = MapSpaceGeometry.Ancestors(view, space);
                RequireStorey(view, prism);
                int count = prisms.Count(p => p.Space.Id == prism.Space.Id);
                if (count != 1) throw new MapDocumentException(MapDocumentRefusal.SpaceRoomOccupancyInvalid, "room requires one prism")
                { RecordId = prism.Id, SpaceId = space.Id, FieldPath = "Prisms", ActualCount = count, ExpectedCount = 1 };
                domains.Add(prism.Id, MapPrismGeometry.Compile(view, prism));
            }
            catch (Exception error) when (error is MapDocumentException or MapExactOverflowException or OverflowException)
            { AddError(error, $"prism '{prism.Id}': {Detail(error)}", prism); }
        }
        if (errors.Count != 0) { typedFindings?.AddRange(findings); return errors.AsReadOnly(); }
        try
        {
            var min = new MapExactXz(domains.Values.Min(d => d.Min.X), domains.Values.Min(d => d.Min.Z));
            var max = new MapExactXz(domains.Values.Max(d => d.Max.X), domains.Values.Max(d => d.Max.Z));
            var partition = MapOccupiedPartitions.BuildWide(view, min, max, limits, carve: false);
            if (partition.Status != MapRefinementStatus.Complete) throw MapOccupiedPartitions.Refusal(partition.Status, partition.Detail);
            var raw = partition.Pieces;
            var work = new MapWaterWorkBudget(limits.MaxPairChecks);
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
                var parents = bySpace[group.Key].ToArray();
                var pieces = group.SelectMany(p => Enumerable.Range(0, domains[p.Id].Pieces.Count).Select(i => (Prism: p.Id, Piece: i))).ToArray();
                foreach (var (left, right) in MapWaterBoundsSweep.Cross(pieces.Select(p => domains[p.Prism].PieceBounds(p.Piece)).ToArray(),
                    parents.Select(i => bounds[i]).ToArray()))
                {
                    if (!near.TryGetValue(pieces[left], out var list)) near.Add(pieces[left], list = new());
                    list.Add(parents[right]);
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
                        var polygon = convex;
                        var extent = MapWaterPolygons.Bounds.Of(polygon);
                        var uncovered = new List<IReadOnlyList<MapWideXz>> { polygon };
                        foreach (var parent in (near.GetValueOrDefault((prism.Id, i)) ?? []).Where(p => bounds[p].OverlapsOpen(extent)).Select(p => raw[p]))
                        {
                            work.Charge(1);
                            var clip = MapOccupiedPartitions.Intersect(polygon, parent.Polygon);
                            if (!MapOccupiedPartitions.Positive(clip)) continue;
                            if (clip.Any(p => MapWideRational.Min(parent.Top(p) ?? hi, hi).CompareTo(MapWideRational.Max(parent.Bottom(p), lo)) <= 0))
                                Add(new(MapDocumentRefusal.SpacePrismParentSeparationInvalid, $"prism '{prism.Id}': effective separation over parent refinement")
                                { RecordId = prism.Id, SpaceId = prism.Space.Id, RelatedRecordId = prism.Parent.Id });
                            var next = new List<IReadOnlyList<MapWideXz>>();
                            foreach (var remaining in uncovered)
                                next.AddRange(MapOccupiedPartitions.Difference(remaining, parent.Polygon));
                            uncovered = next;
                        }
                        if (uncovered.Any(MapOccupiedPartitions.Positive)) Add(new(MapDocumentRefusal.SpacePrismOutsideParent,
                            $"prism '{prism.Id}': outside parent footprint")
                        { RecordId = prism.Id, SpaceId = prism.Space.Id, RelatedRecordId = prism.Parent.Id });
                    }
            }
            // The sweep only compares bounds, in work proportional to the pairs whose bounds overlap or touch, and stays uncharged.
            var peers = MapWaterBoundsSweep.Pairs(bounds, touching: false, new MapWaterWorkBudget(long.MaxValue)).Order().ToArray();
            foreach (var (i, j) in peers)
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
                    Add(new(MapDocumentRefusal.SpaceOccupiedOverlap, $"peer occupied overlap: '{a.SpaceId}' with '{b.SpaceId}'")
                    { SpaceId = a.SpaceId, RelatedRecordId = b.SpaceId });
            }
        }
        catch (Exception error) when (error is MapDocumentException or MapExactOverflowException or OverflowException)
        { AddError(error, "prism coverage: " + Detail(error), null); }
        typedFindings?.AddRange(findings.DistinctBy(f => f.Message).OrderBy(f => f.Message, StringComparer.Ordinal));
        return Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());

        void Add(MapDocumentFinding finding)
        {
            errors.Add(finding.Message); findings.Add(finding);
            string owner = finding.RecordId ?? finding.SpaceId ?? "";
            // Peer diagnostics name both spaces. Their first ordinal owner keeps the released ordering.
            if (finding.Reason == MapDocumentRefusal.SpaceOccupiedOverlap && finding.RelatedRecordId is { } other &&
                StringComparer.Ordinal.Compare(owner, other) > 0) owner = other;
            diagnostics?.Add(new(owner, finding.Message, finding));
        }
        void AddError(Exception error, string message, MapPrismDomain? prism)
        {
            MapDocumentFinding? finding = error is MapDocumentException refusal ? MapDocumentFinding.FromException(refusal)
                : new(MapDocumentRefusal.GeometryNotRepresentable, error.Message) { Operation = "prism coverage" };
            if (finding is null) { errors.Add(message); diagnostics?.Add(new(prism?.Id ?? "", message, null)); }
            else Add(finding with { Message = message, RecordId = finding.RecordId ?? prism?.Id,
                SpaceId = finding.SpaceId ?? prism?.Space.Id });
        }
    }

    internal static void RequireStorey(MapScopedSurfaces view, MapPrismDomain prism)
    {
        var storey = view.EffectiveContext?.Storeys.SingleOrDefault(s => s.Id == prism.StoreyId)
            ?? throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "missing geometry: declared prism storey")
            { RecordId = prism.Id, SpaceId = prism.Space.Id, EntityId = prism.StoreyId, SubjectKind = "storey", FieldPath = "StoreyId" };
        if (storey.PayloadVersion != 1 || (storey.InstanceId ?? "") != prism.InstanceId)
            throw new MapDocumentException(storey.PayloadVersion != 1 ? MapDocumentRefusal.SchemaPayloadVersionUnsupported
                : MapDocumentRefusal.AuthoredOwnershipMismatch, "invalid prism/storey instance ownership")
            { RecordId = prism.Id, SpaceId = prism.Space.Id, EntityId = storey.Id, SubjectKind = "storey", InstanceId = prism.InstanceId,
                FieldPath = storey.PayloadVersion != 1 ? "Storey.PayloadVersion" : "Storey.InstanceId",
                ActualVersion = storey.PayloadVersion != 1 ? storey.PayloadVersion : null,
                SupportedVersions = storey.PayloadVersion != 1 ? [1] : null,
                ActualText = storey.PayloadVersion == 1 ? storey.InstanceId : null };
    }

    internal static void RequireStandaloneStoreys(MapDocument document)
    {
        foreach (var prism in document.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>())
        {
            var storey = document.BuildingIntent?.Storeys.SingleOrDefault(s => s.Id == prism.StoreyId)
                ?? throw new MapDocumentException(MapDocumentRefusal.ReferenceMissing, "Prism names an undeclared standalone storey.")
                { RecordId = prism.Id, SpaceId = prism.Space.Id, EntityId = prism.StoreyId, SubjectKind = "storey", FieldPath = "StoreyId" };
            if (storey.PayloadVersion != 1 || prism.InstanceId != "" || storey.InstanceId is not null)
                throw new MapDocumentException(storey.PayloadVersion != 1 ? MapDocumentRefusal.SchemaPayloadVersionUnsupported
                    : MapDocumentRefusal.AuthoredOwnershipMismatch, "Standalone prism and storey must have standalone ownership.")
                { RecordId = prism.Id, SpaceId = prism.Space.Id, EntityId = storey.Id, SubjectKind = "storey", InstanceId = prism.InstanceId,
                    FieldPath = storey.PayloadVersion != 1 ? "Storey.PayloadVersion" : prism.InstanceId != "" ? "InstanceId" : "Storey.InstanceId",
                    ActualVersion = storey.PayloadVersion != 1 ? storey.PayloadVersion : null,
                    SupportedVersions = storey.PayloadVersion != 1 ? [1] : null,
                    ActualText = storey.PayloadVersion != 1 ? null : prism.InstanceId != "" ? prism.InstanceId : storey.InstanceId };
            var transfer = document.Prefabs?.Transfers.SingleOrDefault(t => t.Binding.Child.Kind == MapPrefabChildKind.Record && t.Binding.WorldId == prism.Id);
            if (transfer is not null && (transfer.Retired || transfer.Binding.Removed ||
                !document.Prefabs!.Transfers.Any(t => t.InstanceId == transfer.InstanceId && t.Binding.Child.Kind == MapPrefabChildKind.Storey &&
                    t.Binding.WorldId == storey.Id && !t.Retired && !t.Binding.Removed)))
                throw new MapDocumentException(MapDocumentRefusal.SpaceStoreyTransferMissing, "Unpacked prism requires a matching live storey transfer.")
                { RecordId = prism.Id, InstanceId = transfer.InstanceId, EntityId = storey.Id, FieldPath = "Transfers" };
        }
    }

    static string Detail(Exception error) => error is MapExactOverflowException or OverflowException ||
        error.Message.Contains("not representable", StringComparison.OrdinalIgnoreCase) ? "not representable: " + error.Message : error.Message;

}
