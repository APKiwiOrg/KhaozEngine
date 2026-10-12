using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Prefabs;

internal static partial class PrefabFixtures
{
    internal const string SupportFloorId = "prefab/a/Surface/deck%2Fdeck-support%2Ffloor";

    // T5-R2. Task 5's physical proofs keep the complete geometry and IDs, without depending on Task 7 water.
    internal static PrefabFixture DryTransformed(float yaw, float scale, Vector3 position) =>
        DryAllResources(Transformed(yaw, scale, position));

    internal static PrefabFixture DryAllResources() => DryAllResources(Create("all-resources"));

    static PrefabFixture DryAllResources(PrefabFixture fixture)
    {
        var asset = fixture.Assets.GetPrefab("hut");
        return Reasset(fixture, asset with
        { Children = asset.Children.Where(c => c is not (MapPrefabWaterChild or MapPrefabMediumChild)).ToArray() });
    }

    // This variant is shared with preparation tests. The deck produces the only physical floor.
    static PrefabFixture SupportResourceFixture()
    {
        var f = Create();
        var original = f.Assets.GetPrefab("hut");
        const string floor = "deck/deck-support/floor";
        var children = original.Children.Where(c => c.Key != new MapPrefabChildKey(MapPrefabChildKind.Surface, "floor"))
            .Select(c =>
            {
                var (child, occurrences) = MapPrefabReferenceWalker.Child(c, new SupportFloorReferences(floor));
                return child with { References = MapPrefabReferences.Slots(occurrences) };
            }).ToList();
        children.Add(new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "deck"), new MapPlacement
        { Id = "deck", Kind = "deck", AssetId = "deck", Y = 0, Scale = 1 }));
        var asset = original with { Children = children };
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("floor", MapSurfaceRole.SupportFloor));
        var patch = Patch("floor", 4, 4, 0);
        set.Patches.Add(patch.Key, patch);
        var support = new MapSupportResource(1, set, null, Slots(Surf("/patches/0/key/surfaceId", "floor")));
        return Reasset(f, asset, captured: AssetsWithSupport(asset, support));
    }

    sealed class SupportFloorReferences(string floor) : MapPrefabReferenceMap
    {
        internal override (string Id, MapPrefabReferenceScope? Scope) Text(string path, MapPrefabChildKind kind, string id, bool space) =>
            (kind == MapPrefabChildKind.Surface && id == "floor" ? floor : id, MapPrefabReferenceScope.Local);
    }

    internal static PrefabFixture SupportAt(bool far)
    {
        var f = SupportBoundChild(far ? -450 : 5);
        if (far)
        {
            var instance = f.Document.Prefabs!.Instances.Single();
            f.Document.Prefabs = new(1, 1, new[] { instance with { Transform = instance.Transform with { Position = new(30000.25f, -450, -29999.75f) } } }, []);
        }
        return f;
    }

    internal static PrefabFixture CoincidentSupport()
    {
        var f = Create("support-resource");
        var asset = f.Assets.GetPrefab("hut");
        var second = SurfaceChild("independent-floor", MapSurfaceRole.SupportFloor, Patch("independent-floor", 4, 4, 0));
        asset = asset with { Children = asset.Children.Append(second).ToArray() };
        return Reasset(f, asset, captured: AssetsWithSupport(asset,
            new(1, FloorResource(), null, Slots(Surf("/patches/0/key/surfaceId", "floor")))));
    }

    internal static MapSurfaceSet FloorResource(MapSurfaceRole role = MapSurfaceRole.SupportFloor)
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(Surface("floor", role));
        var patch = Patch("floor", 4, 4, 0);
        set.Patches.Add(patch.Key, patch);
        return set;
    }

    internal static MapAssetClosure SupportAssets(MapPrefabAsset asset, MapSupportResource support) => AssetsWithSupport(asset, support);
}
