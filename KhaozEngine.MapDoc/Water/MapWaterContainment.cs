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
        bool complete = true;
        MapWaterConnectivity? graph = null;
        foreach (MapWaterBodyDoc body in snapshot.Bodies.Where(b => b is not null).OrderBy(b => b.Id, StringComparer.Ordinal))
        {
            string? unverified;
            try { unverified = ValidateBody(view, body, errors, ref graph); }
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
        reads = graph?.Reads ?? default;
        return new(complete, errors.AsReadOnly());
    }

    /// <summary>Adds the body's definite refusals to <paramref name="errors"/> and returns why it could not be
    /// verified, or null when every check ran on available data.</summary>
    static string? ValidateBody(MapScopedSurfaces view, MapWaterBodyDoc body, List<string> errors, ref MapWaterConnectivity? graph)
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

        var covered = new List<string>();
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
            else if (!covered.Contains(space.Id, StringComparer.Ordinal)) covered.Add(space.Id);
        }
        if (unavailable is not null) return unavailable;
        if (covered.Count == 0) return null;
        if (!MeetsFootprint(view, domain, covered, out bool footprintsComplete))
        {
            if (!footprintsComplete) return "covered footprints are unavailable";
            errors.Add($"{label} covers no space footprint");
        }
        if (covered.Count == 1) return null;
        graph ??= new MapWaterConnectivity(view);
        string? unknown = graph.Join(label, surface, covered, errors, out bool connected);
        if (connected) return null;
        if (unknown is not null) return unknown;
        // Scoped acquisition never follows a space's relation lists, so only a view of the whole document proves that
        // no unacquired relation joins the covered spaces.
        if (!view.ListsEverySurface) return "covered spaces not proven connected in this view";
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

    /// <summary>True when an exception reports definitely invalid authored data, as opposed to missing, unavailable
    /// or unrepresentable data that a complete or wider view could still resolve.</summary>
    internal static bool Invalid(MapDocumentException error) => !MapCommonRefinement.NotRepresentable(error) &&
        !error.Message.Contains("not representable", StringComparison.Ordinal) && error.Message.StartsWith("invalid", StringComparison.Ordinal);
}
