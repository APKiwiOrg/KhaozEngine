using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc;

/// <summary>Native metadata checks shared by loading, editing and saving.</summary>
internal static class MapNativeValidation
{
    internal static void Validate(MapDocument doc, List<string> errors)
    {
        ValidateNumericIds(doc, errors);
        ValidateSupport(doc, errors);
        if (doc.ResolverIdentity is { } identity && (identity.PayloadVersion <= 0 || identity.ResolverVersion <= 0))
            errors.Add("resolverIdentity payloadVersion and resolverVersion must be positive.");
        if (doc.NativeAssets is null)
            errors.Add("nativeAssets must not be null.");
        else
        {
            foreach (MapAssetRef asset in doc.NativeAssets)
            {
                if (asset is null)
                {
                    errors.Add("nativeAssets must not contain null entries.");
                    continue;
                }
                if (string.IsNullOrEmpty(asset.Id) || string.IsNullOrEmpty(asset.Path) || string.IsNullOrEmpty(asset.Sha256))
                    errors.Add("native asset id, path and sha256 must be non-empty.");
                if (asset.PayloadVersion <= 0)
                    errors.Add($"native asset '{asset.Id}': payloadVersion must be positive.");
            }
        }
        if (doc.PlayableBounds is not { } bounds) return;
        if (!float.IsFinite(bounds.MinX) || !float.IsFinite(bounds.MinZ) ||
            !float.IsFinite(bounds.MaxX) || !float.IsFinite(bounds.MaxZ) ||
            !(bounds.MaxX > bounds.MinX) || !(bounds.MaxZ > bounds.MinZ))
            errors.Add("playableBounds must be finite with MaxX > MinX and MaxZ > MinZ.");
        if (!(bounds.MinX >= doc.Bounds.MinX) || !(bounds.MinZ >= doc.Bounds.MinZ) ||
            !(bounds.MaxX <= doc.Bounds.MaxX) || !(bounds.MaxZ <= doc.Bounds.MaxZ))
            errors.Add("playableBounds must be contained in bounds.");
    }

    static void ValidateSupport(MapDocument doc, List<string> errors)
    {
        if (!Enum.IsDefined(doc.SupportRecipe)) errors.Add("supportRecipe is unsupported.");
        if (doc.Surfaces is null) errors.Add("surfaces must not be null.");
        bool legacy = doc.ResolverIdentity is null or { PayloadVersion: 1, ResolverVersion: 1 };
        if (legacy)
        {
            if (doc.SupportRecipe != MapSupportRecipe.LegacyXzCallbackV1)
                errors.Add("Resolver version 1 requires supportRecipe LegacyXzCallbackV1.");
            if (doc.Surfaces is { IsEmpty: false }) errors.Add("Resolver version 1 requires no surfaces.");
            if (doc.Placements.Any(p => p.SupportBinding is not null))
                errors.Add("Resolver version 1 requires no support bindings.");
        }
        else if (doc.ResolverIdentity is { PayloadVersion: 1, ResolverVersion: 2 })
        {
            if (doc.SupportRecipe != MapSupportRecipe.AuthoredBindingsV2)
                errors.Add("Resolver version 2 requires supportRecipe AuthoredBindingsV2.");
        }
        else errors.Add("resolverIdentity requires payload version 1 and resolver version 1 or 2.");

        var surfaces = new Dictionary<string, MapSurfaceRef>(StringComparer.Ordinal);
        if (doc.Surfaces is { } set)
        {
            foreach (MapSurfaceRef surface in set.Refs)
            {
                if (surface is null || string.IsNullOrWhiteSpace(surface.Id))
                {
                    errors.Add("every surface needs a non-empty id.");
                    continue;
                }
                if (!surfaces.TryAdd(surface.Id, surface)) errors.Add($"duplicate surface id '{surface.Id}'.");
                if (surface.Frame is null || !Enum.IsDefined(surface.Role) || !Enum.IsDefined(surface.PresencePolicy))
                    errors.Add($"surface '{surface.Id}': invalid frame, role or presence policy.");
                else
                {
                    try { _ = surface.Frame.WorldXz(MapLatticeAddress.Corner(0, 0)); }
                    catch (MapDocumentException) { errors.Add($"surface '{surface.Id}': invalid lattice frame."); }
                }
                if (surface.SemanticSha256 is not { Length: 64 } digest ||
                    digest.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    errors.Add($"surface '{surface.Id}': semanticSha256 must be a lowercase SHA-256 digest.");
                if (surface.IndoorSpan is { } span &&
                    (string.IsNullOrWhiteSpace(span.Id) || span.ParentSpace is null ||
                     string.IsNullOrWhiteSpace(span.ParentSpace.Id) || string.IsNullOrWhiteSpace(span.ParentSpace.Anchor.SurfaceId) ||
                     span.DomainTags is null || span.DomainTags.Any(t => t is null)))
                    errors.Add($"surface '{surface.Id}': invalid indoor span.");
            }
        }
        foreach (MapPlacement placement in doc.Placements) ValidateSupportBinding(placement, surfaces, errors);
    }

    /// <summary>One placement's support binding against the document's declared surfaces.</summary>
    internal static void ValidateSupportBinding(MapPlacement placement, IReadOnlyDictionary<string, MapSurfaceRef> surfaces,
        List<string> errors)
    {
        if (placement.SupportBinding is not { } binding) return;
        string prefix = $"placement '{placement.Id}': ";
        if (placement.Y is not null) errors.Add(prefix + "explicit Y cannot have a support binding.");
        if (!Enum.IsDefined(binding.Kind)) errors.Add(prefix + "unsupported support binding kind.");
        else if (binding.Kind == MapSupportBindingKind.Surface)
        {
            if (string.IsNullOrWhiteSpace(binding.SurfaceId) || !surfaces.ContainsKey(binding.SurfaceId) || binding.SpaceId is not null)
                errors.Add(prefix + "surface support binding requires a declared surface and no spaceId.");
        }
        else if (string.IsNullOrWhiteSpace(binding.SpaceId) || binding.SurfaceId is not null)
            errors.Add(prefix + "space support binding requires a spaceId and no surfaceId.");
        if ((binding.ReferenceY is { } y && !float.IsFinite(y)) ||
            (binding.SearchBelow is { } below && (!float.IsFinite(below) || below < 0)) ||
            (binding.SearchAbove is { } above && (!float.IsFinite(above) || above < 0)))
            errors.Add(prefix + "support search values must be finite with nonnegative extents.");
    }

    // Reservation accepts newly imported placements above the old mark, but still checks their IDs.
    internal static long ValidateNumericIds(MapDocument doc, List<string> errors, bool requireHighWater = true)
    {
        long maximum = doc.NumericIdHighWaterMark;
        if (maximum < 0)
            errors.Add("numericIdHighWaterMark must be nonnegative.");
        var seen = new HashSet<long>();
        foreach (MapPlacement placement in doc.Placements)
        {
            ValidateNumericId(placement, doc.NumericIdHighWaterMark, seen, errors, requireHighWater);
            if (placement.NumericId is { } id) maximum = System.Math.Max(maximum, id);
        }
        return maximum;
    }

    /// <summary>One placement's numeric ID. <paramref name="seen"/> accumulates the IDs of every placement checked
    /// so far in the same document.</summary>
    internal static void ValidateNumericId(MapPlacement placement, long highWaterMark, HashSet<long> seen,
        List<string> errors, bool requireHighWater = true)
    {
        if (placement.NumericId is not { } id) return;
        if (id <= 0)
            errors.Add($"placement '{placement.Id}': numericId must be positive when present.");
        if (!seen.Add(id))
            errors.Add($"placement '{placement.Id}': duplicate numericId '{id}'.");
        if (requireHighWater && id > highWaterMark)
            errors.Add($"placement '{placement.Id}': numericId exceeds numericIdHighWaterMark.");
    }
}
