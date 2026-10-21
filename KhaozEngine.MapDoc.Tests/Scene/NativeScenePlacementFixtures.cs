using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Scene;

internal static partial class NativeSceneFixtures
{
    internal static NativeSceneFixture SeparatedPlacements(MapAssetClosure closure, float tileSize, params MapEffectiveTransform[] poses)
    {
        var fixture = Create("empty") with { Assets = closure };
        fixture.Document.NativeAssets = closure.Roots.ToList();
        fixture.Document.TileSize = tileSize;
        fixture.Document.Bounds = new() { MinX = -32064, MinZ = -32064, MaxX = 32064, MaxZ = 32064 };
        fixture.Document.PlayableBounds = fixture.Document.Bounds;
        var transforms = new List<MapStandaloneResourceTransform>();
        for (int i = 0; i < poses.Length; i++)
        {
            string id = "sparse-" + i;
            var pose = poses[i];
            fixture.Document.Placements.Add(new()
            {
                Id = id,
                AssetId = "prop-a",
                Kind = "fixture-kind",
                NumericId = i + 1,
                X = (float)pose.X,
                Y = (float)pose.Y,
                Z = (float)pose.Z,
                Yaw = (float)pose.YawRadians,
                Scale = (float)pose.Scale
            });
            transforms.Add(new(MapPrefabChildKind.Placement, id, pose, new string('a', 64)));
        }
        fixture.Document.NumericIdHighWaterMark = poses.Length;
        fixture.Document.NativeResourceTransforms = transforms;
        return fixture;
    }

    internal static NativeSceneFixture Placements(MapAssetClosure closure)
    {
        var fixture = Create("terrain") with { Assets = closure };
        fixture.Document.NativeAssets = closure.Roots.ToList();
        var bindings = new List<MapPrefabBinding>();
        string[] leaves = { "leaf-a", "leaf-b", "leaf-c", "roof-a", "roof-b" };
        for (int i = 0; i < leaves.Length; i++) bindings.Add(new(new(MapPrefabChildKind.Placement, leaves[i]), leaves[i], 101 + i, false));
        for (int i = 0; i < 17; i++)
        {
            string id = "emitter-" + i.ToString("D2");
            bindings.Add(new(new(MapPrefabChildKind.Light, id), id, null, false));
        }
        bindings.Add(new(new(MapPrefabChildKind.Marker, "pin"), "pin", null, false));
        var patch = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        foreach (string id in new[] { "room-a", "room-b" })
            patch.Records.Add(new MapSpaceDoc(id, MapSpaceKind.Room, null, null, Array.Empty<string>(),
                Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>()));
        var surface = fixture.Document.Surfaces.Refs[0];
        fixture.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            new[] { KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch)) })
        };
        MapPrefabReference World(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);
        fixture.Document.BuildingIntent = new(1, new[]
        {
            new MapRoofLinkDoc("roof-a-room", World(MapPrefabChildKind.Placement, "roof-a"), new[] { World(MapPrefabChildKind.Record, "room-a") }, Array.Empty<MapPrefabReference>(), 3),
            new MapRoofLinkDoc("roof-b-room", World(MapPrefabChildKind.Placement, "roof-b"), new[] { World(MapPrefabChildKind.Record, "room-b") }, Array.Empty<MapPrefabReference>(), 3)
        }, Array.Empty<MapRoofGroupDoc>(), Array.Empty<MapLocalLightDoc>(), Array.Empty<MapLocalMarkerDoc>());
        fixture.Document.NumericIdHighWaterMark = 105;
        fixture.Document.Prefabs = new(1, 1, new[]
        {
            new MapPrefabInstance("building", "assembly", closure.GetResource("assembly").Reference.Sha256,
                new(new(-1.25f, 2.5f, 3.75f), .371f, 1.2f), null, new(bindings), Array.Empty<MapPrefabOverride>())
        }, Array.Empty<MapPrefabBindingTransfer>());
        return fixture;
    }
}
