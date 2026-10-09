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
        {
            if (placement.Y is not null) continue;
            MapSupportBinding binding = placement.SupportBinding ??
                throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: missing authored binding.");
            if (binding.Kind == MapSupportBindingKind.Space &&
                (binding.ReferenceY is null || binding.SearchBelow is null || binding.SearchAbove is null))
                throw new MapDocumentException($"Placement '{placement.Id}' support status Invalid: space binding requires reference Y and bounded search.");
            bindings.Add(placement.Id, binding with { });
        }
        var supports = new List<MapPlacementSupport>();
        MapResolvedDocument resolved = MapResolver.Resolve(document, assets, ResolveHeight, hash);
        return new(resolved, supports);

        float ResolveHeight(string id, float x, float z)
        {
            try { return SelectHeight(id, x, z); }
            catch (Exception error) when (error is MapExactOverflowException or OverflowException)
            { throw new MapDocumentException($"Placement '{id}' support status NotRepresentable: overflow."); }
        }

        float SelectHeight(string id, float x, float z)
        {
            MapSupportBinding binding = bindings[id];
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
            supports.Add(new(id, support));
            return support.WorldY;
        }
    }
}
