using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>Document-level water rules shared by loading, editing and saving. Domain geometry and containment are
/// checked elsewhere.</summary>
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
        foreach (MapWaterBodyDoc body in water.Bodies) ValidateBody(body, media, bodies, errors);
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

    static void ValidateBody(MapWaterBodyDoc body, HashSet<string> media, HashSet<string> ids, List<string> errors)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Id))
        {
            errors.Add("every water body needs a non-empty id.");
            return;
        }
        string label = $"water body '{body.Id}'";
        if (!ids.Add(body.Id)) errors.Add($"{label} is duplicated.");
        if (!float.IsFinite(body.SurfaceY) || MathF.Abs(body.SurfaceY) > MaxMetres)
            errors.Add($"{label}: surfaceY must be finite within {MaxMetres} m.");
        else
        {
            try { _ = MapExactValue.FromSingle(body.SurfaceY); }
            catch (MapExactOverflowException) { errors.Add($"{label}: surfaceY is not representable."); }
        }
        if (body.Medium is null || !media.Contains(body.Medium)) errors.Add($"{label}: unknown medium '{body.Medium}'.");
        if (string.IsNullOrWhiteSpace(body.RenderMaterial)) errors.Add($"{label}: renderMaterial must be non-empty.");
        ValidateDomain(body.Domain, label, errors);
        ValidateCovers(body.Covers, label, errors);
    }

    static void ValidateDomain(MapWaterDomainDoc domain, string label, List<string> errors)
    {
        if (domain?.Rects is null || domain.Rings is null)
        {
            errors.Add($"{label}: domain is required.");
            return;
        }
        if (!ValidFrame(domain.Frame))
        {
            errors.Add($"{label}: invalid lattice frame.");
            return;
        }
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
    }

    static void ValidateCovers(IReadOnlyList<MapRecordRef> covers, string label, List<string> errors)
    {
        if (covers is null || covers.Count == 0)
        {
            errors.Add($"{label}: covers no space.");
            return;
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < covers.Count; i++)
        {
            MapRecordRef cover = covers[i];
            if (cover is null || string.IsNullOrWhiteSpace(cover.Id) || string.IsNullOrWhiteSpace(cover.Anchor.SurfaceId))
                errors.Add($"{label}: cover {i} needs a space id and an anchor surface.");
            else if (!seen.Add(cover.Id)) errors.Add($"{label}: cover '{cover.Id}' is repeated.");
        }
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
