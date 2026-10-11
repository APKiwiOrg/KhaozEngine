using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Document-level water rules shared by loading, editing and saving, including exact domain geometry:
/// simple rings, no overlap between the pieces of one body, and no overlap between bodies that cover the same
/// space. Containment in the covered spaces is checked elsewhere.</summary>
internal static class MapWaterValidation
{
    const int MaxMetres = 1_000_000;
    static readonly MapExactValue MaxExtent = new(MaxMetres, 1);
    static readonly MapExactValue MinExtent = new(-MaxMetres, 1);

    internal static void Validate(MapDocument doc, List<string> errors)
    {
        if (doc.Water is not { } water) return;
        if (doc.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 2 } ||
            doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
            errors.Add("water: bounded water requires a resolver-2 native document.");

        var media = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapWaterMediumDoc medium in water.Media) ValidateMedium(medium, media, errors);
        var bodies = new HashSet<string>(StringComparer.Ordinal);
        var compiled = new List<(MapWaterBodyDoc Body, MapWaterDomain Domain)>();
        foreach (MapWaterBodyDoc body in water.Bodies)
            if (ValidateBody(body, media, bodies, errors) is { } domain) compiled.Add((body, domain));
        ValidateSharedSpaces(compiled, errors);
    }

    static void ValidateMedium(MapWaterMediumDoc medium, HashSet<string> keys, List<string> errors)
    {
        if (medium is null || string.IsNullOrWhiteSpace(medium.Key))
        {
            errors.Add("every water medium needs a non-empty key.");
            return;
        }
        string label = $"water medium '{medium.Key}'";
        if (!keys.Add(medium.Key)) errors.Add($"{label} is duplicated.");
        if (!Enum.IsDefined(medium.Kind)) errors.Add($"{label}: kind is unsupported.");
        if (!float.IsFinite(medium.WadeSpeedScale) || !(medium.WadeSpeedScale > 0f) || medium.WadeSpeedScale > 1f)
            errors.Add($"{label}: wadeSpeedScale must be in (0, 1].");
    }

    /// <summary>Returns the compiled domain when the body's geometry is valid and its id is unique.</summary>
    static MapWaterDomain? ValidateBody(MapWaterBodyDoc body, HashSet<string> media, HashSet<string> ids, List<string> errors)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            errors.Add("every water body needs a non-empty id.");
            return null;
        }
        string label = $"water body '{body.Id}'";
        bool unique = ids.Add(body.Id);
        if (!unique) errors.Add($"{label} is duplicated.");
        if (!float.IsFinite(body.SurfaceY) || MathF.Abs(body.SurfaceY) > MaxMetres)
            errors.Add($"{label}: surfaceY must be finite within {MaxMetres} m.");
        else
        {
            try { _ = MapExactValue.FromSingle(body.SurfaceY); }
            catch (MapExactOverflowException) { errors.Add($"{label}: surfaceY is not representable."); }
        }
        if (body.Medium is null || !media.Contains(body.Medium)) errors.Add($"{label}: unknown medium '{body.Medium}'.");
        if (string.IsNullOrWhiteSpace(body.RenderMaterial)) errors.Add($"{label}: renderMaterial must be non-empty.");
        MapWaterDomain? domain = ValidateDomain(body.Domain, label, errors);
        bool covers = ValidateCovers(body.Covers, label, errors);
        return unique && covers ? domain : null;
    }

    static MapWaterDomain? ValidateDomain(MapWaterDomainDoc domain, string label, List<string> errors)
    {
        if (domain?.Rects is null || domain.Rings is null)
        {
            errors.Add($"{label}: domain is required.");
            return null;
        }
        if (!ValidFrame(domain.Frame))
        {
            errors.Add($"{label}: invalid lattice frame.");
            return null;
        }
        // Piece and vertex limits come before any per-piece or geometry work.
        var limits = new List<string>();
        if (!MapWaterDomain.WithinLimits(domain, limits))
        {
            errors.Add($"{label}: {limits[0]}.");
            return null;
        }
        int before = errors.Count;
        if (domain.Rects.Count == 0 && domain.Rings.Count == 0) errors.Add($"{label}: domain is empty.");
        bool far = false;
        for (int i = 0; i < domain.Rects.Count; i++)
        {
            MapWaterRect rect = domain.Rects[i];
            if (rect.MinX >= rect.MaxX || rect.MinZ >= rect.MaxZ) errors.Add($"{label}: rect {i} is empty.");
            far |= Far(domain.Frame, MapLatticeAddress.Corner(rect.MinX, rect.MinZ)) ||
                Far(domain.Frame, MapLatticeAddress.Corner(rect.MaxX, rect.MaxZ));
        }
        for (int i = 0; i < domain.Rings.Count; i++)
        {
            IReadOnlyList<MapLatticeAddress>? vertices = domain.Rings[i]?.Vertices;
            if (vertices is null || vertices.Count < 3)
            {
                errors.Add($"{label}: ring {i} needs at least 3 vertices.");
                if (vertices is null) continue;
            }
            for (int v = 0; v < vertices.Count; v++)
            {
                try { far |= Far(domain.Frame, vertices[v]); }
                catch (MapDocumentException) { errors.Add($"{label}: ring {i} vertex {v} is not a valid lattice address."); }
            }
        }
        if (far) errors.Add($"{label}: domain exceeds {MaxMetres} m.");
        if (errors.Count != before) return null;

        var geometry = new List<string>();
        MapWaterDomain? compiled = MapWaterDomain.TryCompile(domain, null, geometry);
        foreach (string error in geometry) errors.Add($"{label}: {error}.");
        if (compiled is null) return null;
        bool overlap = false;
        foreach ((int a, int b) in compiled.OverlappingPieces())
        {
            errors.Add($"{label}: pieces {a} and {b} overlap.");
            overlap = true;
        }
        return overlap ? null : compiled;
    }

    /// <summary>Refuses positive-area overlap between bodies that cover the same space. Pieces of every body in one
    /// space meet through a sorted bounds sweep, so only overlapping bounds reach the exact test.</summary>
    static void ValidateSharedSpaces(List<(MapWaterBodyDoc Body, MapWaterDomain Domain)> compiled, List<string> errors)
    {
        var bySpace = new SortedDictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < compiled.Count; i++)
            foreach (MapRecordRef cover in compiled[i].Body.Covers)
            {
                if (!bySpace.TryGetValue(cover.Id, out List<int>? bodies)) bySpace[cover.Id] = bodies = new();
                bodies.Add(i);
            }
        foreach ((string space, List<int> bodies) in bySpace)
        {
            if (bodies.Count < 2) continue;
            var pieces = new List<(MapWaterDomain Domain, int Piece)>();
            var owners = new List<int>();
            foreach (int body in bodies)
                for (int p = 0; p < compiled[body].Domain.Pieces.Count; p++)
                {
                    pieces.Add((compiled[body].Domain, p));
                    owners.Add(body);
                }
            var reported = new HashSet<(int, int)>();
            foreach ((int a, int b) in MapWaterDomain.OverlappingPairs(pieces))
            {
                int first = Math.Min(owners[a], owners[b]), second = Math.Max(owners[a], owners[b]);
                if (first == second || !reported.Add((first, second))) continue;
                string[] ids = { compiled[first].Body.Id, compiled[second].Body.Id };
                Array.Sort(ids, StringComparer.Ordinal);
                errors.Add($"water bodies '{ids[0]}' and '{ids[1]}' overlap in space '{space}'.");
            }
        }
    }

    static bool ValidateCovers(IReadOnlyList<MapRecordRef> covers, string label, List<string> errors)
    {
        if (covers is null || covers.Count == 0)
        {
            errors.Add($"{label}: covers no space.");
            return false;
        }
        int before = errors.Count;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < covers.Count; i++)
        {
            MapRecordRef cover = covers[i];
            if (cover is null || string.IsNullOrWhiteSpace(cover.Id) || string.IsNullOrWhiteSpace(cover.Anchor.SurfaceId))
                errors.Add($"{label}: cover {i} needs a space id and an anchor surface.");
            else if (!seen.Add(cover.Id)) errors.Add($"{label}: cover '{cover.Id}' is repeated.");
        }
        return errors.Count == before;
    }

    static bool ValidFrame(MapLatticeFrame? frame)
    {
        if (frame is null) return false;
        try { _ = frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
        catch (MapDocumentException) { return false; }
        return true;
    }

    /// <summary>True when either world axis of the corner lies more than <see cref="MaxMetres"/> from the origin,
    /// including a corner too large to convert exactly.</summary>
    static bool Far(MapLatticeFrame frame, MapLatticeAddress address)
    {
        try
        {
            MapExactXz world = frame.WorldXz(address);
            return world.X.CompareTo(MaxExtent) > 0 || world.X.CompareTo(MinExtent) < 0 ||
                world.Z.CompareTo(MaxExtent) > 0 || world.Z.CompareTo(MinExtent) < 0;
        }
        catch (MapExactOverflowException) { return true; }
    }
}
