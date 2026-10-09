using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>Whole authored identity from current complete edits or verified pinned storage. It covers the manifest
/// root, the authored content digest, the ordered surface patch facts, the asset closure, the builder, the options
/// hash and the resolver version. Both overloads refuse the same documents through the same validation
/// code.</summary>
public static class MapAuthoredIdentityV2
{
    public static string Compute(MapDocument document, MapAssetClosure assets, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException("Whole authored identity cannot hash a partial window.");
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        Validate(document.ResolverIdentity, document.SupportRecipe, options);
        MapBoundDocumentValidation.Validate(document, assets);
        return MapAuthoredIdentityProjection.Compute(MapSurfaceSemantics.RootDigest(document),
            MapAuthoredContentDigest.Compute(document),
            document.Surfaces.Patches.Select(p => new KeyValuePair<MapPatchKey, string>(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))),
            assets, options);
    }

    public static string Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options) =>
        Compute(source, assets, options, null);

    internal static string Compute(MapStoredSurfaceSource source, MapAssetClosure assets, MapResolveOptions options,
        MapWholeIdentityWork? work)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(options);
        Validate(source.ResolverIdentity, source.SupportRecipe, options);
        source.RequireIdentityManifest(assets);
        var patches = new SortedDictionary<MapPatchKey, string>();
        foreach (MapSurfaceIndexEntry entry in source.EnumeratePinnedEntries(work))
            patches.Add(entry.Key, source.IdentityPatchDigest(entry, work));
        foreach (MapSurfaceRef surface in source.Surfaces)
            if (MapSurfaceSemantics.SurfaceDigest(surface, patches.Where(p => p.Key.SurfaceId == surface.Id)) != surface.SemanticSha256)
                throw new MapDocumentException($"Corrupt surface semantic digest for '{surface.Id}'.");
        return MapAuthoredIdentityProjection.Compute(source.RootSha256, source.IdentityContentDigest(assets), patches, assets, options);
    }

    static void Validate(MapResolverIdentityDoc? resolver, MapSupportRecipe recipe, MapResolveOptions options)
    {
        if (resolver is not { PayloadVersion: 1, ResolverVersion: 2 } || recipe != MapSupportRecipe.AuthoredBindingsV2)
            throw new MapDocumentException("Whole authored identity requires resolver (1, 2) and supportRecipe AuthoredBindingsV2.");
        if (options.ResolverVersion != 2 || string.IsNullOrWhiteSpace(options.BuilderId) ||
            options.BuilderVersion <= 0 || string.IsNullOrWhiteSpace(options.OptionsHash))
            throw new MapDocumentException("Whole authored identity requires resolver 2, builder ID, positive version and options hash.");
    }
}
