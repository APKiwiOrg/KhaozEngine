using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The outcome of containment validation. <see cref="Complete"/> is false when some check needed records or
/// apertures the view does not hold, and each such body reports "unverified" in <see cref="Errors"/>.</summary>
public sealed record MapWaterContainmentReport(bool Complete, IReadOnlyList<string> Errors);

/// <summary>Geometric containment of water bodies in the spaces they cover. It runs where space coverage validation
/// runs, at editor publication and inside every compile, never on an identity path. Per body: every cover is an
/// acquired space that is not an alias, the domain meets a covered footprint with positive area, and several covered
/// spaces form one set connected by portals and vertical link openings whose exact bottom lies strictly below the
/// body's level, since water at exactly an aperture's bottom does not pass it.</summary>
public static class MapWaterContainment
{
    public static MapWaterContainmentReport Validate(MapScopedSurfaces view, MapWaterSet water)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(water);
        MapWaterSet snapshot = water.Clone();
        var errors = new List<string>();
        bool complete = true;
        foreach (MapWaterBodyDoc body in snapshot.Bodies.Where(b => b is not null).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            string? unverified;
            try { unverified = ValidateBody(view, body, errors); }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException) { unverified = "not representable"; }
            catch (MapDocumentException error) when (Invalid(error))
            {
                errors.Add($"water body '{body.Id}': {error.Message}");
                continue;
            }
            catch (MapDocumentException error) { unverified = error.Message; }
            if (unverified is null) continue;
            complete = false;
            errors.Add($"water body '{body.Id}': unverified ({unverified})");
        }
        return new(complete, errors.AsReadOnly());
    }

    /// <summary>Adds the body's definite refusals to <paramref name="errors"/> and returns why it could not be
    /// verified, or null when every check ran on available data.</summary>
    static string? ValidateBody(MapScopedSurfaces view, MapWaterBodyDoc body, List<string> errors)
    {
        string label = $"water body '{body.Id}'";
        MapExactValue surface;
        try { surface = MapExactValue.FromSingle(body.SurfaceY); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or ArgumentException)
        {
            errors.Add($"{label}: surfaceY is not representable");
            return null;
        }
        MapWaterDomain domain;
        try { domain = MapWaterDomain.Compile(body.Domain); }
        catch (MapDocumentException error)
        {
            errors.Add($"{label}: {error.Message}");
            return null;
        }

        var covered = new List<(MapRecordRef Ref, MapSpaceDoc Space)>();
        string? unavailable = null;
        foreach (MapRecordRef cover in body.Covers ?? Array.Empty<MapRecordRef>())
        {
            if (cover is null) continue;
            if (!view.TryRecord(cover, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) || record is null)
            {
                // A present or known-empty anchor proves the record is absent, and so does an unread anchor the view
                // proves does not exist. Any other anchor hides it.
                if (status is MapPatchStatus.Missing or MapPatchStatus.KnownEmpty ||
                    status == MapPatchStatus.Unloaded && MapWaterFootprints.ProvesAbsent(view, cover.Anchor))
                    errors.Add($"{label}: cover '{cover.Id}' is not a space");
                else unavailable ??= $"cover '{cover.Id}' is {status}";
                continue;
            }
            if (record is not MapSpaceDoc space) errors.Add($"{label}: cover '{cover.Id}' is not a space");
            else if (space.AliasOf is not null) errors.Add($"{label}: cover '{cover.Id}' is an alias");
            else if (!covered.Any(c => c.Space.Id == space.Id)) covered.Add((cover, space));
        }
        if (unavailable is not null) return unavailable;
        if (covered.Count == 0) return null;
        if (!MeetsFootprint(view, domain, covered.Select(c => c.Space.Id).ToArray(), out bool footprintsComplete))
        {
            if (!footprintsComplete) return "covered footprints are unavailable";
            errors.Add($"{label} covers no space footprint");
        }
        if (covered.Count == 1) return null;
        string? unknown = Connected(view, label, surface, covered, errors, out bool connected);
        if (connected) return null;
        if (unknown is not null) return unknown;
        errors.Add($"{label}: covered spaces are not connected below its surface");
        return null;
    }

    /// <summary>True when the domain shares positive area with a footprint rectangle of a covered space.</summary>
    static bool MeetsFootprint(MapScopedSurfaces view, MapWaterDomain domain, IReadOnlyList<string> covered, out bool complete)
    {
        complete = true;
        foreach (string space in covered)
        {
            IReadOnlyList<MapWaterFootprintRect> rects = MapWaterFootprints.Of(view, space, domain.Min, domain.Max,
                out bool spaceComplete, out _, out _);
            complete &= spaceComplete;
            foreach (MapWaterFootprintRect rect in rects)
            {
                var cells = new MapWaterDomainDoc(rect.Frame,
                    new[] { new MapWaterRect(rect.CellMinX, rect.CellMinZ, rect.CellMaxX, rect.CellMaxZ) }, Array.Empty<MapWaterRing>());
                if (MapWaterDomain.OverlapsPositively(domain, MapWaterDomain.Compile(cells))) return true;
            }
        }
        return false;
    }

    /// <summary>Whether the covered spaces form one set over edges from portals and link openings, counting an edge
    /// only when its exact aperture bottom is strictly below <paramref name="surface"/>. The candidates are the
    /// relations a covered space lists and every acquired portal and link, since R2 lists a relation on the endpoint
    /// record it names, which may be an alias of a covered space. Each endpoint is normalized to its alias owner, as R2
    /// relations are, and read only while its edge can still join two covered spaces. An endpoint whose owner is not
    /// covered settles its edge, so a relation that reaches an uncovered space never leaves the result unknown, and no
    /// space record is read beyond the relations' endpoints. A relation whose geometry is definitely invalid adds an
    /// error and joins nothing. Returns why an edge could not be read, or null when every edge was evaluated.</summary>
    static string? Connected(MapScopedSurfaces view, string label, MapExactValue surface,
        IReadOnlyList<(MapRecordRef Ref, MapSpaceDoc Space)> covered, List<string> errors, out bool connected)
    {
        var adjacency = covered.ToDictionary(c => c.Space.Id, _ => new List<string>(), StringComparer.Ordinal);
        var refs = covered.ToDictionary(c => c.Space.Id, c => c.Ref, StringComparer.Ordinal);
        var owners = new Dictionary<MapRecordRef, (string? Owner, MapDocumentException? Error)>();
        var visited = new HashSet<MapRecordRef>();
        string? unknown = null;
        foreach (var (_, space) in covered)
        {
            foreach (MapBoundaryRef side in space.Portals ?? Array.Empty<MapBoundaryRef>())
                if (side?.Record is { } reference && !visited.Contains(reference) && Read<MapCavePortal>(reference) is { } portal)
                    Portal(reference, portal);
            foreach (MapRecordRef reference in space.Links ?? Array.Empty<MapRecordRef>())
                if (reference is not null && !visited.Contains(reference) && Read<MapVerticalLink>(reference) is { } link)
                    Link(reference, link);
        }
        foreach (MapRecordRef reference in view.Witness.Records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (!view.TryRecord(reference, out MapTopologyRecord? record, out _, borrow: true)) continue;
            if (record is MapCavePortal portal) Portal(reference, portal);
            else if (record is MapVerticalLink link) Link(reference, link);
        }

        var reached = new HashSet<string>(StringComparer.Ordinal) { covered[0].Space.Id };
        var pending = new Queue<string>(reached);
        while (pending.TryDequeue(out string? current))
            foreach (string next in adjacency[current])
                if (reached.Add(next)) pending.Enqueue(next);
        connected = reached.Count == adjacency.Count;
        return unknown;

        T? Read<T>(MapRecordRef reference) where T : MapTopologyRecord
        {
            if (view.TryRecord(reference, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) && record is T typed)
                return typed;
            unknown ??= $"record '{reference.Id}' is {(record is null ? status.ToString() : "not a " + typeof(T).Name)}";
            return null;
        }

        void Portal(MapRecordRef reference, MapCavePortal portal)
        {
            if (visited.Add(reference)) Edge(reference, portal.FromSpace, portal.ToSpace, null, $"portal '{reference.Id}'");
        }

        void Link(MapRecordRef reference, MapVerticalLink link)
        {
            if (!visited.Add(reference)) return;
            foreach (MapRecordRef opening in link.Openings ?? Array.Empty<MapRecordRef>())
                if (opening is not null)
                    Edge(opening, link.UpperSpace, link.LowerSpace, reference, $"link '{reference.Id}' opening '{opening.Id}'");
        }

        (string? Owner, MapDocumentException? Error) Owner(MapRecordRef endpoint)
        {
            if (adjacency.ContainsKey(endpoint.Id)) return (endpoint.Id, null);
            if (owners.TryGetValue(endpoint, out var known)) return known;
            (string?, MapDocumentException?) result;
            try { result = (MapSpaceGeometry.Owner(view, MapSpaceGeometry.Record<MapSpaceDoc>(view, endpoint)), null); }
            catch (MapDocumentException error) { result = (null, error); }
            owners.Add(endpoint, result);
            return result;
        }

        void Edge(MapRecordRef aperture, MapRecordRef? from, MapRecordRef? to, MapRecordRef? link, string relation)
        {
            if (from is null || to is null) return;
            // An endpoint whose owner is not covered settles the edge, so the other endpoint is never read.
            (string? a, MapDocumentException? fromError) = Owner(from);
            if (a is not null && !adjacency.ContainsKey(a)) return;
            (string? b, MapDocumentException? toError) = Owner(to);
            if (b is not null && !adjacency.ContainsKey(b)) return;
            if (fromError is not null || toError is not null)
            {
                // With both endpoints unresolved the edge may join other spaces, so only one known endpoint is definite.
                if (fromError is not null && toError is not null) unknown ??= $"{relation} (space '{from.Id}' {fromError.Message})";
                else Fail($"space '{(fromError is null ? to : from).Id}'", fromError ?? toError!);
                return;
            }
            if (a is null || b is null || a == b) return;
            try
            {
                MapPortalFact fact = MapRelationApertures.Compile(view, aperture, refs[a], refs[b], link);
                if (MapRelationApertures.BottomOf(fact).CompareTo(surface) >= 0) return;
                adjacency[a].Add(b);
                adjacency[b].Add(a);
            }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException)
            {
                unknown ??= $"{relation} is not representable";
            }
            catch (MapDocumentException error) { Fail(relation, error); }
        }

        void Fail(string what, MapDocumentException error)
        {
            if (Invalid(error)) errors.Add($"{label}: {what} is invalid ({error.Message})");
            else unknown ??= $"{what} ({error.Message})";
        }
    }

    /// <summary>True when an exception reports definitely invalid authored data, as opposed to missing, unavailable
    /// or unrepresentable data that a complete or wider view could still resolve.</summary>
    static bool Invalid(MapDocumentException error) => !MapCommonRefinement.NotRepresentable(error) &&
        !error.Message.Contains("not representable", StringComparison.Ordinal) && error.Message.StartsWith("invalid", StringComparison.Ordinal);
}
