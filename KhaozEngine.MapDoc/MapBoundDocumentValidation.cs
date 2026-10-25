using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc;

/// <summary>Render-free native validity, independent of builder options and authored hashing.</summary>
public static class MapBoundDocumentValidation
{
    /// <summary>Validates a complete document against an explicitly supplied verified asset closure.</summary>
    public static void Validate(MapDocument document, MapAssetClosure assets, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ValidateLocal(document, registry);
        RequireRoots(document.NativeAssets, assets);
        MapPrefabIdentity.RequireOwnership(document, assets);
        RequireWaterMaterials(document.Water, assets);
        Require(MapFoliageValidation.ValidateBound(document, assets));
        foreach (MapPlacement placement in document.Placements) RequireClosureAsset(placement, assets);
    }

    /// <summary>Checks native document-local invariants only. Asset membership requires Validate with a closure.</summary>
    public static void ValidateLocal(MapDocument document, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true }) throw new MapDocumentException("Native document validation requires every tile to be loaded.");
        if (document.ResolverIdentity is not { PayloadVersion: 1, ResolverVersion: 1 or 2 })
            throw new MapDocumentException("Native document requires payload version 1 and resolver version 1 or 2.");
        if (document.PlayableBounds is null) throw new MapDocumentException("Native document requires playable bounds.");
        Require(MapDocumentValidator.Validate(document, registry ?? MapDocRegistry.CreateDefault()));
        if (!float.IsFinite(document.Bounds.MinX) || !float.IsFinite(document.Bounds.MinZ) ||
            !float.IsFinite(document.Bounds.MaxX) || !float.IsFinite(document.Bounds.MaxZ))
            throw new MapDocumentException("Native storage bounds must be finite.");
        foreach (MapPlacement p in document.Placements) ValidatePlacement(p);
    }

    /// <summary>The publication gate for a complete effective water snapshot. Identity validation remains
    /// geometry-free. Placement, prefab and roof publications use this same full-body compilation gate.</summary>
    public static void ValidateEffectiveWater(MapEffectiveResources resources, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(assets);
        using var preparation = Scene.MapSceneAdmission.Current?.BeginPreparation();
        if (resources.Water is not { } water) return;
        RequireWaterMaterials(water, assets);
        foreach (var body in MapWaterCompiler.Compile(resources.CompleteView(), water))
            if (body.Status != MapWaterCompileStatus.Complete)
                throw new MapDocumentException(body.Detail ?? $"water body '{body.BodyId}': {body.Status}");
    }

    /// <summary>Validates one slice of a document's content, such as one stored tile, with the rules and messages
    /// <see cref="Validate"/> applies to it. <paramref name="globals"/> is the content-free document, already
    /// accepted by <see cref="ValidateLocal"/> and <see cref="RequireRoots"/>. <paramref name="numericIds"/>
    /// accumulates across slices and starts with the prefab reservations. Duplicate string IDs across slices and
    /// prefab identity collisions stay with the caller's shared registry.</summary>
    internal static void ValidateContent(MapDocument globals, IReadOnlyDictionary<string, MapSurfaceRef> surfaces,
        MapTileContent content, HashSet<long> numericIds, MapAssetClosure assets)
    {
        Require(MapDocumentValidator.ValidateContent(globals, surfaces, content, numericIds));
        foreach (MapPlacement p in content.Placements) ValidatePlacement(p);
        foreach (MapPlacement p in content.Placements) RequireClosureAsset(p, assets);
    }

    internal static void RequireRoots(IReadOnlyList<MapAssetRef> nativeAssets, MapAssetClosure assets)
    {
        var roots = new HashSet<MapAssetRef>(nativeAssets);
        if (roots.Count != nativeAssets.Count || roots.Count != assets.Roots.Count || !roots.SetEquals(assets.Roots))
            throw new MapDocumentException("Native document roots do not match the verified asset closure.");
    }

    /// <summary>Requires every water body's render material to be a <see cref="MapResourceKind.Material"/> resource in
    /// the verified closure. Bodies are checked in id order and every failure is reported, so the in-memory and
    /// stored identity paths refuse with the same message whatever order the set was built in.</summary>
    internal static void RequireWaterMaterials(MapWaterSet? water, MapAssetClosure assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (water is null) return;
        var errors = new List<string>();
        foreach (MapWaterBodyDoc body in water.Bodies.OrderBy(b => b.Id, StringComparer.Ordinal))
            if (!assets.TryGetResource(body.RenderMaterial, out MapResolvedResource? resource) || resource!.Kind != MapResourceKind.Material)
                errors.Add($"water body '{body.Id}': render material '{body.RenderMaterial}' is not a Material resource");
        Require(errors);
    }

    static void Require(IReadOnlyList<string> errors)
    {
        if (errors.Count != 0) throw new MapDocumentException(string.Join("\n", errors));
    }

    static void ValidatePlacement(MapPlacement p)
    {
        if (string.IsNullOrWhiteSpace(p.AssetId)) throw new MapDocumentException($"Placement '{p.Id}' requires a native asset ID.");
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Z) || (p.Y is { } y && !float.IsFinite(y)) ||
            !float.IsFinite(p.Yaw) || !float.IsFinite(p.Scale) || p.Scale <= 0 || p.Tags is null || p.Tags.Any(t => t is null))
            throw new MapDocumentException($"Placement '{p.Id}' has invalid transform or tags.");
    }

    static void RequireClosureAsset(MapPlacement placement, MapAssetClosure assets) => assets.GetAsset(placement.AssetId!);
}
