using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;

namespace KhaozEngine.MapDoc.Support;

/// <summary>Resolves authored support bindings while preserving explicit placement heights and native snapshots.</summary>
public static class MapResolverV2
{
    public static MapSupportedResolution Resolve(MapDocument document, MapAssetClosure assets,
        IMapSurfaceSource surfaces, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(surfaces);
        string hash = MapAuthoredIdentityV2.Compute(document, assets, options);
        var bindings = new Dictionary<string, MapSupportBinding>(StringComparer.Ordinal);
        foreach (MapPlacement placement in document.Placements.OrderBy(p => p.Id, StringComparer.Ordinal))
            if (placement.Y is null) bindings.Add(placement.Id, RequireBinding(placement));
        var supports = new List<MapPlacementSupport>();
        MapResolvedDocument resolved = MapResolver.Resolve(document, assets,
            (id, x, z) => SupportHeight(id, bindings[id], x, z, surfaces, assets, supports), hash);
        return new(resolved, supports);
    }

    /// <summary>Resolves <paramref name="placements"/>, which belong to <paramref name="document"/>, in the given order
    /// exactly as <see cref="Resolve"/> resolves them, without the authored hash and without resolving any other
    /// placement. The document's surfaces are captured only when a placement takes its height from support.</summary>
    internal static IReadOnlyList<MapResolvedPlacement> ResolvePlacements(MapDocument document, MapAssetClosure assets,
        IReadOnlyList<MapPlacement> placements)
    {
        IMapSurfaceSource? surfaces = null;
        var resolved = new MapResolvedPlacement[placements.Count];
        for (int i = 0; i < resolved.Length; i++)
        {
            MapPlacement placement = placements[i];
            MapSupportBinding? binding = placement.Y is null ? RequireBinding(placement) : null;
            resolved[i] = MapResolver.ResolveOne(placement, (id, x, z) =>
                SupportHeight(id, binding!, x, z, surfaces ??= MapDocumentSurfaceSource.Capture(document), assets, null));
        }
        return Array.AsReadOnly(resolved);
    }

    // A detached copy of the authored binding of a placement without an explicit height, after refusing an invalid one.
    static MapSupportBinding RequireBinding(MapPlacement placement)
    {
        MapSupportBinding binding = placement.SupportBinding ??
            throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: missing authored binding.");
        if (binding.Kind == MapSupportBindingKind.Space &&
            (binding.ReferenceY is null || binding.SearchBelow is null || binding.SearchAbove is null))
            throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: space binding requires reference Y and bounded search.");
        return binding with { };
    }

    static float SupportHeight(string id, MapSupportBinding binding, float x, float z, IMapSurfaceSource surfaces,
        MapAssetClosure assets, List<MapPlacementSupport>? supports)
    {
        try { return SelectHeight(id, binding, x, z, surfaces, assets, supports); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapDocumentException($"Placement '{id}' support status NotRepresentable: overflow."); }
    }

    static float SelectHeight(string id, MapSupportBinding binding, float x, float z, IMapSurfaceSource surfaces,
        MapAssetClosure assets, List<MapPlacementSupport>? supports)
    {
        WorldFrame frame = WorldFrame.Nearest(x, z);
        // Subtract integer anchors exactly before the one local float conversion.
        float localX = MapExactValue.FromSingle(x).Subtract(new((long)frame.X * (long)WorldFrame.Grid, 1)).ToSingle();
        float localZ = MapExactValue.FromSingle(z).Subtract(new((long)frame.Z * (long)WorldFrame.Grid, 1)).ToSingle();
        var point = new MapFramePoint(frame, new(localX, binding.ReferenceY ?? 0, localZ));
        var scope = new MapSurfaceScope(frame, new Vector2(localX - 2, localZ - 2), new Vector2(localX + 2, localZ + 2),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new MapQueryLimits());
        MapScopedSurfaces scoped = MapScopedSurfaces.Acquire(surfaces, scope, assets.Roots.Select(a => a.Sha256).ToArray());
        var query = new MapSupportQuery(scoped);
        MapSupportResult support = binding.Kind == MapSupportBindingKind.Surface
            ? query.SampleSurface(point, binding.SurfaceId!)
            : query.Select(new(point, null, binding.SpaceId, null, binding.SearchAbove!.Value, binding.SearchBelow!.Value, null));
        if (support.Status != MapSupportStatus.Supported)
            throw new MapDocumentException($"Placement '{id}' support status {support.Status}: {support.Detail}");
        supports?.Add(new(id, support));
        return support.WorldY;
    }
}
