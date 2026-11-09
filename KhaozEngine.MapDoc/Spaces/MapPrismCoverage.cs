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

    /// <summary>Returns the ordered immutable findings behind prism coverage diagnostics.</summary>
    public static IReadOnlyList<MapDocumentFinding> ValidateFindings(MapScopedSurfaces view, MapRefinementLimits limits)
    {
        var findings = new List<MapDocumentFinding>();
        _ = Validate(view, limits, findings);
        return findings.AsReadOnly();
    }

    /// <summary>Refuses with the first concrete rule and the full ordered finding snapshot.</summary>
    public static void Require(MapScopedSurfaces view, MapRefinementLimits limits)
    {
        var findings = ValidateFindings(view, limits);
        if (findings.Count != 0) throw MapDocumentRefusalFactory.FromFinding(findings[0], string.Join("\n", findings.Select(f => f.Message)), findings: findings);
    }

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
                    {
                        RecordId = prism.Id,
                        SpaceId = space.Id,
                        RelatedRecordId = prism.Parent.Id,
                        FieldPath = space.Kind != MapSpaceKind.Room ? "Kind" : "Parent",
                        EnumType = space.Kind != MapSpaceKind.Room ? typeof(MapSpaceKind) : null,
                        ActualInteger = space.Kind != MapSpaceKind.Room ? (int)space.Kind : null
                    };
                if (MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>().Any(f => f.Space.Id == space.Id))
                    throw new MapDocumentException(MapDocumentRefusal.SpaceRoomOccupancyInvalid, "room occupancy requires only its prism domain")
                    { RecordId = prism.Id, SpaceId = space.Id, FieldPath = "Footprints" };
                _ = MapSpaceGeometry.Ancestors(view, space);
                RequireStorey(view, prism);
                int count = prisms.Count(p => p.Space.Id == prism.Space.Id);
                if (count != 1) throw new MapDocumentException(MapDocumentRefusal.SpaceRoomOccupancyInvalid, "room requires one prism")
                { RecordId = prism.Id, SpaceId = space.Id, FieldPath = "Prisms", ActualCount = count, ExpectedCount = 1 };
                MapPrismValidationWorkspace.Reserve(MapPrismPreparationCapacity.DomainBytes(prism));
                using var domainWorkspace = MapPrismValidationWorkspace.DomainKernel(view, prism);
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
            if (partition.Status != MapRefinementStatus.Complete) throw MapOccupiedPartitions.Refusal(partition.Status, partition.Detail, partition.Finding!);
            MapPrismValidationWorkspace.ReleasePartitionIndexes();
            var raw = partition.Pieces;
            var work = new MapWaterWorkBudget(limits.MaxPairChecks, compile: false, source: limits);
            MapPrismValidationWorkspace.Reserve(checked(512L + 256L * raw.Count));
            var bounds = raw.Select(p => MapWaterPolygons.Bounds.Of(p.Polygon)).ToArray();
            // Exact bounds broad phases. A parent or peer piece whose bounds do not overlap shares no positive area, so
            // skipping it changes no result. Each candidate is charged once, in the released visiting order. One index
            // per parent owner covers the pieces of every room it parents. The partition above already resolved every
            // owner, so resolving them first raises nothing new.
            var owners = prisms.ToDictionary(p => p.Id, p => MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, p.Parent)), StringComparer.Ordinal);
            var bySpace = Enumerable.Range(0, raw.Count).ToLookup(i => raw[i].SpaceId, StringComparer.Ordinal);
            using var parentIndexes = new MapPrismParentIndex(bySpace, bounds);
            foreach (var prism in prisms)
            {
                var domain = domains[prism.Id];
                var heights = MapPrismGeometry.Heights(view, prism);
                var lo = MapWideRational.From(heights.Lower); var hi = MapWideRational.From(heights.Upper);
                using (var partPlanning = MapPrismValidationWorkspace.Kernel(MapPrismPreparationCapacity.LargestPiece(prism)))
                    MapPrismValidationWorkspace.Reserve(MapPrismPreparationCapacity.ConvexPartsBytes(domain));
                for (int i = 0; i < domain.Pieces.Count; i++)
                {
                    IReadOnlyList<IReadOnlyList<MapWideXz>> convexParts;
                    using (var triangulation = MapPrismValidationWorkspace.Kernel(MapPrismPreparationCapacity.LargestPiece(prism)))
                        convexParts = domain.ConvexParts(i, work);
                    foreach (var convex in convexParts)
                    {
                        using var convexPreparation = MapPrismValidationWorkspace.Admission?.BeginPreparation();
                        using var convexWorkspace = MapPrismValidationWorkspace.Kernel(convex.Count);
                        var polygon = convex;
                        var extent = MapWaterPolygons.Bounds.Of(polygon);
                        using var fragments = new MapPrismUncoveredFragments(polygon);
                        foreach (var parent in parentIndexes.Candidates(owners[prism.Id], extent).Select(p => raw[p]))
                        {
                            work.Charge(1);
                            var clip = MapOccupiedPartitions.Intersect(polygon, parent.Polygon);
                            if (!MapOccupiedPartitions.Positive(clip)) continue;
                            if (clip.Any(p => MapWideRational.Min(parent.Top(p) ?? hi, hi).CompareTo(MapWideRational.Max(parent.Bottom(p), lo)) <= 0))
                            {
                                MapPrismValidationWorkspace.Reserve(checked(128L + 2L * (prism.Id.Length + 64)));
                                Add(new(MapDocumentRefusal.SpacePrismParentSeparationInvalid, $"prism '{prism.Id}': effective separation over parent refinement")
                                { RecordId = prism.Id, SpaceId = prism.Space.Id, RelatedRecordId = prism.Parent.Id });
                            }
                            fragments.Subtract(parent.Polygon);
                        }
                        if (fragments.Polygons.Any(MapOccupiedPartitions.Positive))
                        {
                            MapPrismValidationWorkspace.Reserve(checked(128L + 2L * (prism.Id.Length + 48)));
                            Add(new(MapDocumentRefusal.SpacePrismOutsideParent, $"prism '{prism.Id}': outside parent footprint")
                            { RecordId = prism.Id, SpaceId = prism.Space.Id, RelatedRecordId = prism.Parent.Id });
                        }
                    }
                }
            }
            parentIndexes.Dispose();
            MapPrismCoveragePeers.Validate(view, raw, bounds, work, Add);
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
            else Add(finding with
            {
                Message = message,
                InvalidPrefixSuppressed = true,
                RecordId = finding.RecordId ?? prism?.Id,
                SpaceId = finding.SpaceId ?? prism?.Space.Id
            });
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
            {
                RecordId = prism.Id,
                SpaceId = prism.Space.Id,
                EntityId = storey.Id,
                SubjectKind = "storey",
                InstanceId = prism.InstanceId,
                ResolutionStatus = MapResolveStatus.Invalid,
                FieldPath = storey.PayloadVersion != 1 ? "Storey.PayloadVersion" : "Storey.InstanceId",
                ActualVersion = storey.PayloadVersion != 1 ? storey.PayloadVersion : null,
                SupportedVersions = storey.PayloadVersion != 1 ? [1] : null,
                ActualText = storey.PayloadVersion == 1 ? storey.InstanceId : null
            };
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
                {
                    RecordId = prism.Id,
                    SpaceId = prism.Space.Id,
                    EntityId = storey.Id,
                    SubjectKind = "storey",
                    InstanceId = prism.InstanceId,
                    FieldPath = storey.PayloadVersion != 1 ? "Storey.PayloadVersion" : prism.InstanceId != "" ? "InstanceId" : "Storey.InstanceId",
                    ActualVersion = storey.PayloadVersion != 1 ? storey.PayloadVersion : null,
                    SupportedVersions = storey.PayloadVersion != 1 ? [1] : null,
                    ActualText = storey.PayloadVersion != 1 ? null : prism.InstanceId != "" ? prism.InstanceId : storey.InstanceId
                };
            var transfer = document.Prefabs?.Transfers.SingleOrDefault(t => t.Binding.Child.Kind == MapPrefabChildKind.Record && t.Binding.WorldId == prism.Id);
            if (transfer is not null && (transfer.Retired || transfer.Binding.Removed ||
                !document.Prefabs!.Transfers.Any(t => t.InstanceId == transfer.InstanceId && t.Binding.Child.Kind == MapPrefabChildKind.Storey &&
                    t.Binding.WorldId == storey.Id && !t.Retired && !t.Binding.Removed)))
                throw new MapDocumentException(MapDocumentRefusal.SpaceStoreyTransferMissing, "Unpacked prism requires a matching live storey transfer.")
                { RecordId = prism.Id, InstanceId = transfer.InstanceId, EntityId = storey.Id, FieldPath = "Transfers" };
        }
    }

    static string Detail(Exception error) => error is MapExactOverflowException or OverflowException ||
        error is MapDocumentException refusal && MapGeometryRefusals.Representability(refusal, arithmetic: false)
            ? "not representable: " + error.Message : error.Message;

}
