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

        var covered = new List<MapSpaceDoc>();
        string? unavailable = null;
        foreach (MapRecordRef cover in body.Covers ?? Array.Empty<MapRecordRef>())
        {
            if (cover is null) continue;
            if (!view.TryRecord(cover, out MapTopologyRecord? record, out MapPatchStatus status, borrow: true) || record is null)
            {
                // A present or known-empty anchor proves the record is absent. Any other anchor hides it.
                if (status is MapPatchStatus.Missing or MapPatchStatus.KnownEmpty) errors.Add($"{label}: cover '{cover.Id}' is not a space");
                else unavailable ??= $"cover '{cover.Id}' is {status}";
                continue;
            }
            if (record is not MapSpaceDoc space) errors.Add($"{label}: cover '{cover.Id}' is not a space");
            else if (space.AliasOf is not null) errors.Add($"{label}: cover '{cover.Id}' is an alias");
            else if (!covered.Any(c => c.Id == space.Id)) covered.Add(space);
        }
        if (unavailable is not null) return unavailable;
        if (covered.Count == 0) return null;
        if (!MeetsFootprint(view, domain, covered, out bool footprintsComplete))
        {
            if (!footprintsComplete) return "covered footprints are unavailable";
            errors.Add($"{label} covers no space footprint");
        }
        if (covered.Count == 1) return null;
        string? unknown = Connected(view, surface, covered, out bool connected);
        if (connected) return null;
        if (unknown is not null) return unknown;
        errors.Add($"{label}: covered spaces are not connected below its surface");
        return null;
    }

    /// <summary>True when the domain shares positive area with a footprint rectangle of a covered space.</summary>
    static bool MeetsFootprint(MapScopedSurfaces view, MapWaterDomain domain, IReadOnlyList<MapSpaceDoc> covered, out bool complete)
    {
        complete = true;
        foreach (MapSpaceDoc space in covered)
        {
            IReadOnlyList<MapWaterFootprintRect> rects = MapWaterFootprints.Of(view, space.Id, domain.Min, domain.Max,
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

    /// <summary>Whether the covered spaces form one set over edges from their portals and their links' openings,
    /// counting an edge only when its exact aperture bottom is strictly below <paramref name="surface"/>. Returns why an
    /// edge could not be read, or null when every edge was evaluated.</summary>
    static string? Connected(MapScopedSurfaces view, MapExactValue surface, IReadOnlyList<MapSpaceDoc> covered, out bool connected)
    {
        var adjacency = covered.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.Ordinal);
        var visited = new HashSet<MapRecordRef>();
        string? unknown = null;
        foreach (MapSpaceDoc space in covered)
        {
            foreach (MapBoundaryRef side in space.Portals ?? Array.Empty<MapBoundaryRef>())
            {
                if (side?.Record is not { } reference || !visited.Add(reference)) continue;
                if (Read<MapCavePortal>(reference) is { } portal) Edge(reference, portal.FromSpace, portal.ToSpace, null);
            }
            foreach (MapRecordRef reference in space.Links ?? Array.Empty<MapRecordRef>())
            {
                if (reference is null || !visited.Add(reference) || Read<MapVerticalLink>(reference) is not { } link) continue;
                foreach (MapRecordRef opening in link.Openings ?? Array.Empty<MapRecordRef>())
                    if (opening is not null) Edge(opening, link.UpperSpace, link.LowerSpace, reference);
            }
        }

        var reached = new HashSet<string>(StringComparer.Ordinal) { covered[0].Id };
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

        void Edge(MapRecordRef aperture, MapRecordRef from, MapRecordRef to, MapRecordRef? link)
        {
            if (from is null || to is null || from.Id == to.Id || !adjacency.TryGetValue(from.Id, out List<string>? fromEdges) ||
                !adjacency.TryGetValue(to.Id, out List<string>? toEdges))
                return;
            try
            {
                MapPortalFact fact = MapRelationApertures.Compile(view, aperture, from, to, link);
                if (MapRelationApertures.BottomOf(fact).CompareTo(surface) >= 0) return;
                fromEdges.Add(to.Id);
                toEdges.Add(from.Id);
            }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException)
            {
                unknown ??= $"aperture '{aperture.Id}' is not representable";
            }
            catch (MapDocumentException error)
            {
                unknown ??= $"aperture '{aperture.Id}' ({error.Message})";
            }
        }
    }
}
