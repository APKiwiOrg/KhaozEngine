using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A captured body in world geometry. Stored covers have already passed binding remapping.</summary>
internal sealed record MapEffectiveWaterBody(MapWaterBodyDoc Source, MapWaterDomain Domain, MapExactValue SurfaceY,
    string BodyId, IReadOnlyList<MapRecordRef> Covers, string TransformDigest);

/// <summary>Converges local and standalone water on the shared point-freeze policy, once per capture.</summary>
internal static class MapEffectiveWater
{
    internal static MapEffectiveWaterBody Resolve(MapScopedSurfaces view, MapWaterBodyDoc body) =>
        Resolve(view, body, new MapWaterWorkBudget());

    internal static MapEffectiveWaterBody Resolve(MapScopedSurfaces view, MapWaterBodyDoc body, MapWaterWorkBudget budget)
    {
        var transform = view.EffectiveContext?.Transform(MapPrefabChildKind.WaterBody, body.Id);
        // EffectiveResources exposes world heights for existing consumers and retains the local source alongside
        // its immutable poses. Materialized standalone bodies instead arrive with their stored local level.
        var source = view.EffectiveContext?.WaterSources.TryGetValue(body.Id, out var local) == true
            ? body with { SurfaceY = local.SurfaceY } : body;
        var level = MapExactValue.FromSingle(source.SurfaceY);
        if (transform is not null) level = transform.Height(level);
        var errors = new List<string>();
        var domain = MapWaterDomain.TryCompileEffective(source.Domain, transform, errors, budget)
            ?? throw new MapDocumentException(errors[0]);
        string digest = transform is null ? "" : MapPrefabJson.Digest("kemap/effective-water/1", new
        { Definition = MapWaterSet.BodyDigest(source), Pose = transform.Transform, QueryPolicy = 2 });
        return new(source, domain, level, body.Id, Array.AsReadOnly(body.Covers.ToArray()), digest);
    }

    /// <summary>Only water whose actual dependencies use R5 geometry adopts occupied fragments. Unrelated world
    /// bodies retain the R4 result and hash even when the same document contains a distant prefab.</summary>
    internal static bool UsesOccupied(MapScopedSurfaces view, MapEffectiveWaterBody body)
    {
        if (view.EffectiveContext is not { } context) return false;
        if (context.Transform(MapPrefabChildKind.WaterBody, body.BodyId) is not null) return true;
        var records = MapSpaceGeometry.Records(view).ToArray();
        var covers = body.Covers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var footprint in records.OfType<MapSpaceFootprint>().Where(f => covers.Contains(f.Space.Id)))
            if (new[] { footprint.Lattice.SurfaceId, footprint.Lower.SurfaceId, footprint.Upper.SurfaceId }
                .OfType<string>().Any(id => context.Transform(MapPrefabChildKind.Surface, id) is not null)) return true;
        foreach (var prism in records.OfType<MapPrismDomain>())
        {
            if (covers.Contains(prism.Space.Id)) return true;
            if (!covers.Contains(prism.Parent.Id) && !body.Covers.Any(c => MapOccupiedPartitions.Ancestor(view, c.Id, prism.Space.Id))) continue;
            var domain = MapPrismGeometry.Compile(view, prism);
            if (MapWaterDomain.OverlapsPositively(body.Domain, domain)) return true;
        }
        return false;
    }
}
