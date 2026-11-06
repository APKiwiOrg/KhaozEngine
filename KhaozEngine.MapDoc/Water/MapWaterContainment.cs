using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The outcome of containment validation. <see cref="Complete"/> is false when some check needed records or
/// apertures the view does not hold, and each such body reports "unverified" in <see cref="Errors"/>.</summary>
public sealed record MapWaterContainmentReport(bool Complete, IReadOnlyList<string> Errors)
{
    IReadOnlyList<MapDocumentFinding> _findings = Array.Empty<MapDocumentFinding>();

    /// <summary>The known typed refusals in report order, as an owned read-only snapshot. Untyped upstream
    /// failures remain available through Errors until their producers supply structured findings.</summary>
    public IReadOnlyList<MapDocumentFinding> Findings
    {
        get => _findings;
        init => _findings = Array.AsReadOnly(value.ToArray());
    }
}

/// <summary>Geometric containment of water bodies in the spaces they cover. It runs where space coverage validation
/// runs, at editor publication and inside every compile, never on an identity path. Per body: every cover is an
/// acquired space that is not an alias, the domain meets a covered footprint with positive area, and several covered
/// spaces form one set connected by portals and vertical link openings whose exact bottom lies strictly below the
/// body's level, since water at exactly an aperture's bottom does not pass it. The connectivity graph is built once per
/// run and shared by every body. A disconnected body is refused only in a view of the whole document, and is
/// unverified in any other view.</summary>
public static class MapWaterContainment
{
    public static MapWaterContainmentReport Validate(MapScopedSurfaces view, MapWaterSet water) => Validate(view, water, out _);

    internal static MapWaterContainmentReport Validate(MapScopedSurfaces view, MapWaterSet water, out MapWaterConnectivityReads reads)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(water);
        MapWaterSet snapshot = water.Clone();
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        bool complete = true;
        MapWaterConnectivity? graph = null;
        var work = new MapWaterConnectivityWork();
        foreach (MapWaterBodyDoc body in snapshot.Bodies.Where(b => b is not null).OrderBy(b => b.Id, StringComparer.Ordinal))
            complete &= Check(view, body, null, errors, findings, ref graph, work);
        reads = work.Reads;
        return new(complete, errors.AsReadOnly()) { Findings = findings };
    }

    /// <summary>The footprint rectangles of one covered space near the body's domain, and whether the view proves it
    /// holds all of them.</summary>
    internal delegate IReadOnlyList<MapWaterFootprintRect> FootprintSource(string spaceId, out bool complete);
    internal delegate IReadOnlyList<MapOccupiedPiece> OccupiedSource(string spaceId);

    /// <summary>A body the compile has already prepared: its exact level and compiled domain, the footprints it
    /// gathered once under its work meter, the compile budget that every overlap test charges, and the set that
    /// collects the anchors of the relation records connectivity evaluated.</summary>
    internal sealed record Prepared(MapExactValue Surface, MapWaterDomain Domain, FootprintSource Footprints, MapWaterWorkBudget Pairs,
        ISet<MapPatchKey> Read, OccupiedSource? Occupied = null, ISet<MapRecordRef>? Records = null);

    /// <summary>The compile's per-body entry: the same rules as <see cref="Validate(MapScopedSurfaces, MapWaterSet)"/>
    /// for one prepared body, sharing <paramref name="graph"/> with every other body of the compile. Work past a compile
    /// limit throws <see cref="MapWaterCapacityException"/> rather than reporting the body unverified.</summary>
    internal static MapWaterContainmentReport ValidateBody(MapScopedSurfaces view, MapWaterBodyDoc body, Prepared prepared,
        ref MapWaterConnectivity? graph)
    {
        var errors = new List<string>();
        var findings = new List<MapDocumentFinding>();
        bool complete = Check(view, body, prepared, errors, findings, ref graph);
        return new(complete, errors.AsReadOnly()) { Findings = findings };
    }

    /// <summary>Validates one body into <paramref name="errors"/> and returns false when it could not be verified.</summary>
    static bool Check(MapScopedSurfaces view, MapWaterBodyDoc body, Prepared? prepared, List<string> errors,
        List<MapDocumentFinding> findings, ref MapWaterConnectivity? graph, MapWaterConnectivityWork? work = null)
    {
        string? unverified;
        MapDocumentFinding? unverifiedFinding = null;
        try { unverified = ValidateBody(view, body, prepared, errors, findings, ref graph, work, out unverifiedFinding); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        {
            unverified = "not representable";
            unverifiedFinding = new(MapDocumentRefusal.GeometryNotRepresentable, unverified);
        }
        catch (MapWaterCapacityException error) when (prepared is null) { unverified = error.Message; }
        catch (MapDocumentException error) when (Invalid(error))
        {
            string message = $"water body '{body.Id}': {error.Message}";
            errors.Add(message);
            Forward(error, body.Id, message, findings);
            return true;
        }
        catch (MapDocumentException error)
        {
            unverified = error.Message;
            unverifiedFinding = MapDocumentFinding.FromException(error);
        }
        if (unverified is null) return true;
        string detail = $"water body '{body.Id}': unverified ({unverified})";
        errors.Add(detail);
        if (unverifiedFinding is not null) findings.Add(unverifiedFinding with { Message = detail, OwnerId = body.Id });
        return false;
    }

    /// <summary>Adds the body's definite refusals to <paramref name="errors"/> and returns why it could not be
    /// verified, or null when every check ran on available data.</summary>
    static string? ValidateBody(MapScopedSurfaces view, MapWaterBodyDoc body, Prepared? prepared, List<string> errors,
        List<MapDocumentFinding> findings, ref MapWaterConnectivity? graph, MapWaterConnectivityWork? work, out MapDocumentFinding? unverifiedFinding)
    {
        unverifiedFinding = null;
        string label = $"water body '{body.Id}'";
        MapExactValue surface;
        MapWaterDomain domain;
        bool effective = prepared?.Occupied is not null;
        if (prepared is not null)
        {
            surface = prepared.Surface;
            domain = prepared.Domain;
        }
        else
        {
            try { surface = MapExactValue.FromSingle(body.SurfaceY); }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
            {
                Add(new(MapDocumentRefusal.GeometryNotRepresentable, $"{label}: surfaceY is not representable")
                { EntityId = body.Id, FieldPath = "SurfaceY", ActualNumber = body.SurfaceY }, errors, findings);
                return null;
            }
            try
            {
                var resolved = MapEffectiveWater.Resolve(view, body);
                domain = resolved.Domain;
                surface = resolved.SurfaceY;
                effective = MapEffectiveWater.UsesOccupied(view, resolved);
                if (effective)
                {
                    IReadOnlyList<MapOccupiedPiece>? pieces = null;
                    var meter = new MapWaterWorkMeter(new());
                    prepared = new(surface, domain, EmptyFootprints, meter.Pairs, new HashSet<MapPatchKey>(),
                        id => (pieces ??= MapWaterOccupiedPieces.Of(view, domain.Min, domain.Max, new(), meter)).Where(p => p.SpaceId == id).ToArray());
                }
            }
            catch (MapDocumentException error)
            {
                string message = $"{label}: {error.Message}";
                errors.Add(message);
                Forward(error, body.Id, message, findings);
                return null;
            }
        }

        var covered = new List<string>();
        string? unavailable = null;
        foreach (MapRecordRef cover in body.Covers ?? Array.Empty<MapRecordRef>())
        {
            if (cover is null) continue;
            if (!view.TryRecord(cover, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) || record is null)
            {
                // A present or known-empty anchor proves the record is absent, and so does an unread anchor the view
                // proves does not exist. Any other anchor hides it.
                if (effective) unavailable ??= $"cover '{cover.Id}' {cover.Anchor} is {status}";
                else if (status is MapPatchStatus.Missing or MapPatchStatus.KnownEmpty ||
                    status == MapPatchStatus.Unloaded && MapWaterFootprints.ProvesAbsent(view, cover.Anchor))
                    Add(new(MapDocumentRefusal.ReferenceMissing, $"{label}: cover '{cover.Id}' is not a space")
                    { EntityId = body.Id, RecordId = cover.Id, Patch = cover.Anchor, PatchStatus = status,
                        ExpectedRecordType = typeof(MapSpaceDoc) }, errors, findings);
                else unavailable ??= $"cover '{cover.Id}' is {status}";
                continue;
            }
            if (record is not MapSpaceDoc space)
                Add(new(MapDocumentRefusal.ReferenceKindMismatch, $"{label}: cover '{cover.Id}' is not a space")
                { EntityId = body.Id, RecordId = cover.Id, Patch = cover.Anchor,
                    RecordType = record.GetType(), ExpectedRecordType = typeof(MapSpaceDoc) }, errors, findings);
            else if (space.AliasOf is not null)
                Add(new(MapDocumentRefusal.WaterCoversAlias, $"{label}: cover '{cover.Id}' is an alias")
                { EntityId = body.Id, RecordId = cover.Id, SpaceId = space.AliasOf.Id, Patch = cover.Anchor }, errors, findings);
            else if (!covered.Contains(space.Id, StringComparer.Ordinal)) covered.Add(space.Id);
        }
        if (unavailable is not null) return unavailable;
        if (covered.Count == 0) return null;
        // A successfully acquired relation with unavailable aperture geometry must name that aperture. Room
        // partition acquisition is conservative too, but cannot hide the hydraulic reason behind a generic gap.
        if (effective && covered.Count > 1)
        {
            graph ??= new MapWaterConnectivity(view, work);
            string? apertureUnknown = graph.Join(body.Id, surface, covered, errors, findings, out bool joined, out unverifiedFinding, prepared?.Read, prepared?.Records);
            if (apertureUnknown is not null) return apertureUnknown;
            if (!joined)
            {
                if (!view.ListsEverySurface) return "covered spaces not proven connected in this view";
                Add(new(MapDocumentRefusal.WaterSpacesDisconnected, $"{label}: covered spaces are not connected below its surface")
                { EntityId = body.Id, SourceIds = covered }, errors, findings);
            }
        }
        if (!MeetsFootprint(view, domain, covered, prepared, out bool footprintsComplete))
        {
            if (!footprintsComplete) return "covered footprints are unavailable";
            Add(new(MapDocumentRefusal.WaterNoSpaceFootprint, $"{label} covers no space footprint")
            { EntityId = body.Id, SourceIds = covered }, errors, findings);
        }
        if (covered.Count == 1 || effective) return null;
        graph ??= new MapWaterConnectivity(view, work);
        string? unknown = graph.Join(body.Id, surface, covered, errors, findings, out bool connected, out unverifiedFinding, prepared?.Read);
        if (connected) return null;
        if (unknown is not null) return unknown;
        // Scoped acquisition never follows a space's relation lists, so only a view of the whole document proves that
        // no unacquired relation joins the covered spaces.
        if (!view.ListsEverySurface) return "covered spaces not proven connected in this view";
        errors.Add($"{label}: covered spaces are not connected below its surface");
        return null;
    }

    static void Add(MapDocumentFinding finding, List<string> errors, List<MapDocumentFinding> findings)
    {
        errors.Add(finding.Message);
        findings.Add(finding);
    }

    static void Forward(MapDocumentException error, string bodyId, string message, List<MapDocumentFinding> findings)
    {
        if (MapDocumentFinding.FromException(error) is { } finding)
            findings.Add(finding with { Message = message, OwnerId = bodyId });
    }

    /// <summary>True when the domain shares positive area with a footprint rectangle of a covered space. A prepared body
    /// reads its gathered footprints and charges its compile budget.</summary>
    static bool MeetsFootprint(MapScopedSurfaces view, MapWaterDomain domain, IReadOnlyList<string> covered, Prepared? prepared,
        out bool complete)
    {
        complete = true;
        foreach (string space in covered)
        {
            bool spaceComplete;
            if (prepared?.Occupied is { } occupied)
            {
                foreach (var piece in occupied(space))
                    for (int index = 0; index < domain.Pieces.Count; index++)
                        foreach (var part in domain.ConvexParts(index, prepared.Pairs))
                        {
                            prepared.Pairs.Charge(1);
                            if (MapWaterPolygons.ClipToConvex(part, piece.Polygon).Count != 0) return true;
                        }
                continue;
            }
            IReadOnlyList<MapWaterFootprintRect> rects = prepared is not null ? prepared.Footprints(space, out spaceComplete)
                : MapWaterFootprints.Of(view, space, domain.Min, domain.Max, out spaceComplete, out _, out _);
            complete &= spaceComplete;
            foreach (MapWaterFootprintRect rect in rects)
            {
                var cells = new MapWaterDomainDoc(rect.Frame,
                    new[] { new MapWaterRect(rect.CellMinX, rect.CellMinZ, rect.CellMaxX, rect.CellMaxZ) }, Array.Empty<MapWaterRing>());
                MapWaterDomain footprint = MapWaterDomain.Compile(cells);
                if (prepared is not null ? MapWaterDomain.OverlapsPositively(domain, footprint, prepared.Pairs)
                    : MapWaterDomain.OverlapsPositively(domain, footprint)) return true;
            }
        }
        return false;
    }

    static IReadOnlyList<MapWaterFootprintRect> EmptyFootprints(string _, out bool complete)
    { complete = true; return Array.Empty<MapWaterFootprintRect>(); }

    /// <summary>True when an exception reports definitely invalid authored data, as opposed to missing, unavailable
    /// or unrepresentable data that a complete or wider view could still resolve.</summary>
    internal static bool Invalid(MapDocumentException error) => !MapCommonRefinement.NotRepresentable(error) &&
        !error.Message.Contains("not representable", StringComparison.Ordinal) && error.Message.StartsWith("invalid", StringComparison.Ordinal);
}
