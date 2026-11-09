using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

internal static partial class NativeRenderAssetFixtures
{
    internal static MapAssetClosure Presentation(IReadOnlyDictionary<string, byte[]>? replacements = null)
    {
        var source = new Source(); var resources = new JsonArray(); var assets = new JsonArray();
        void Resource(string id, string kind, byte[] bytes)
        {
            var r = source.Add(id, replacements is not null && replacements.TryGetValue(id, out var replacement) ? replacement : bytes);
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject
                { ["id"] = id, ["path"] = r.Path, ["sha256"] = r.Sha256, ["payloadVersion"] = 1 },
                ["kind"] = kind,
                ["dependencies"] = new JsonArray()
            });
        }
        Resource("mesh", "Mesh", Glb());
        foreach (string id in new[] { "roof", "object", "low", "explicit", "cosmetic" })
        {
            if (id != "cosmetic")
            {
                using var stream = new MemoryStream();
                PropCollisionFormat.Write(new BoxShape(id == "low" ? new(10) : new(50)), stream);
                Resource(id + ".coll", "Collider", stream.ToArray());
            }
            if (id == "explicit")
            {
                using var stream = new MemoryStream();
                PropCollisionFormat.Write(new CylinderShape(10, 20), stream);
                Resource("selection", "Selection", stream.ToArray());
            }
            var asset = new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = "mesh",
                ["sourceUnitsToMetres"] = .01f,
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = new JsonArray(),
                ["lightResourceIds"] = new JsonArray(),
                ["renderBounds"] = Bounds(100),
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = true
            };
            if (id != "cosmetic") asset["collisionResourceId"] = id + ".coll";
            if (id == "explicit") asset["selectionResourceId"] = "selection";
            assets.Add(asset);
        }
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        { ["payloadVersion"] = 1, ["assets"] = assets, ["resources"] = resources }.ToJsonString(MapJsonTextOptions.CreateSerializer(false))));
        return MapAssetClosure.Load([root], source);
    }
}

internal static class NativePresentationFixtures
{
    internal static ResidencyFixture Create(bool cluster = false)
    {
        var closure = cluster ? NativeRenderAssetFixtures.Placements() : NativeRenderAssetFixtures.Presentation();
        using var native = cluster ? NativeSceneFixtures.Placements(closure) : NativeSceneFixtures.Create("empty");
        var doc = native.Document; doc.TileSize = 1; doc.NativeAssets = closure.Roots.ToList();
        doc.Surfaces = new();
        var patch = new MapSurfacePatch
        {
            Key = new("ground", 0, 0),
            Width = 16,
            Depth = 4,
            Heights = new int[85],
            Cells = new MapSurfaceCell[64],
            Presence = [ulong.MaxValue]
        };
        var parent = new MapRecordRef("exterior", patch.Key);
        patch.Records.Add(new MapSpaceDoc("exterior", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("exterior-domain", parent, patch.Key,
            Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 16).Select(i => z * 64 + i)).ToArray(),
            new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
        foreach (var (room, x) in new[] { ("room-a", 0), ("room-b", 8) })
        {
            patch.Records.Add(new MapSpaceDoc(room, MapSpaceKind.Room, parent, null, [], [], [], []));
            patch.Records.Add(new MapPrismDomain(room + "-domain", 1, new(room, patch.Key), parent, "", "ground-storey",
                new(MapLatticeFrame.ImportedMetreCentimetre, [new MapWaterRect(x, 0, x + 4, 4)], []), 0, 3));
        }
        doc.Surfaces.Patches.Add(patch.Key, patch);
        var surface = new MapSurfaceRef("ground", MapLatticeFrame.ImportedMetreCentimetre, MapSurfaceRole.SupportFloor,
            MapPresencePolicy.Native, null, null, new string('a', 64));
        doc.Surfaces.Refs.Add(surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        });
        if (!cluster)
        {
            doc.NumericIdHighWaterMark = 6;
            doc.Placements = [Placement("roof-a", "roof", 0, .5f, 2.5f, 1), Placement("object-behind", "object", 0, .5f, 5.5f, 2),
                Placement("roof-b", "roof", 8, .5f, 2.5f, 3), Placement("shelter", "object", 12, .5f, 2.5f, 4),
                Placement("low", "low", 3, .1f, 5.5f, 5), Placement("explicit", "explicit", 5, .5f, 5.5f, 6)];
            MapPrefabReference Ref(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);
            doc.BuildingIntent = new(1,
                [new("a-link", Ref(MapPrefabChildKind.Placement, "roof-a"), [Ref(MapPrefabChildKind.Record, "room-a")], [], 3),
                 new("b-link", Ref(MapPrefabChildKind.Placement, "roof-b"), [Ref(MapPrefabChildKind.Record, "room-b")], [], 3)], [], [], [])
            { Roofs = [Ref(MapPrefabChildKind.Placement, "roof-a"), Ref(MapPrefabChildKind.Placement, "roof-b")] };
        }
        var intent = doc.BuildingIntent!;
        doc.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights, intent.Markers)
        { Roofs = intent.Roofs, Storeys = [new(1, "ground-storey", "Ground", null)] };
        return new(doc, closure);
    }
    static MapPlacement Placement(string id, string asset, float x, float y, float z, long numeric) =>
        new() { Id = id, AssetId = asset, Kind = "invented", NumericId = numeric, X = x, Y = y, Z = z, Scale = 1 };
    internal static MapSceneScope Scope => new(new(WorldFrame.Origin, new(-16, -16), new(16, 16), null, null,
        Enum.GetValues<MapSurfaceRole>(), null, new()), new(-16, -16, -16, 16, 16, 16), MapSceneChannels.Placements | MapSceneChannels.Rooms | MapSceneChannels.Annotations);
    internal static MapFrameState Frame(float x = 1, MapRoofMode mode = MapRoofMode.Auto) =>
        new(WorldFrame.Origin, new(WorldFrame.Origin, new(x, 1, -1)), mode, 0, 0, new(WorldFrame.Origin, Vector3.Zero));
    internal static MapRenderPlan Plan(ResidencyFixture fixture, bool cluster = false, MapSceneScope? scope = null)
    {
        scope ??= Scope;
        var state = fixture.State(scope);
        if (cluster) state = state with
        {
            Policy = new(NativeMapPlacementTests.Policy(true), new HashSet<string>(),
            state.Policy.Foliage, state.Policy.Water, [], new Dictionary<ushort, string>())
        };
        var result = state.Source.Acquire(scope);
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        using var cache = new MapRenderAssetCache();
        var loaded = cache.Acquire(state.Closure, result.Scene!.Placements.Select(p => p.AssetId).Distinct().ToArray(), [], state.Extents, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status);
        return MapRenderPlan.Prepare(state.Source, result.Scene, loaded.Input!, state.Policy, 1);
    }
    internal static MapDrawSet Draw(MapRenderPlan plan, MapFrameState frame)
    { var sink = new MapDrawRecorder(); plan.Register(sink); plan.Record(frame, sink); return sink.Snapshot(); }
}
