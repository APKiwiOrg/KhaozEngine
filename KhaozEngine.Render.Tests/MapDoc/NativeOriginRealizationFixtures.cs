using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Scene;

namespace KhaozEngine.Tests.MapDoc;

internal sealed record NativeOriginRealization(MapFoliageSampleBatch Samples, NativeSceneFixture Scene,
    MapRenderPolicy Policy, MapSnapshotFraming Framing, MapFramePoint PickOrigin);

internal static class NativeOriginRealizationFixtures
{
    internal static readonly RenderAnchor OriginA = new(32000, 0, -32000), OriginB = new(31872, 0, -31872);
    internal static NativeOriginRealization Create()
    {
        var assets = NativeRenderAssetFixtures.Create("foliage");
        var f = FarFoliageGenerationFixtures.Create(32000, -32000, assets);
        var samples = FarFoliageGenerationFixtures.Generate(f);
        var document = f.Document;
        document.NumericIdHighWaterMark = 1;
        document.Placements = [new() { Id = "yawed-prop", AssetId = "B", NumericId = 1, Kind = "cosmetic",
            X = 32000.375f, Y = 2.5f, Z = -31999.625f, Scale = 1.2f, Yaw = .371f }];
        document.NativeResourceTransforms = [.. document.NativeResourceTransforms!, new(MapPrefabChildKind.Placement, "yawed-prop",
            new(32000.375, 2.5, -31999.625, .371, 1.2), new string('b', 64))];
        document.BuildingIntent = new(1, [], [], [new MapLocalLightDoc(1, "far-light", null,
            new(32003.125f, 4, -31999.625f), Vector3.One, 2, 10, true)], []);
        var patch = document.Surfaces.Patches.Values.Single();
        patch.Records.Add(new MapSpaceDoc("wet-space", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("wet-domain", new("wet-space", patch.Key), patch.Key,
            [6 * 64 + 6, 6 * 64 + 7, 7 * 64 + 6, 7 * 64 + 7],
            new(MapBoundKind.SupportFloor, "far-floor", null), new(MapBoundKind.OpenTop, null, null)));
        var surface = document.Surfaces.Refs[0];
        document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        };
        document.Water = new(); document.Water.Media.Add(new("fresh", MapWaterMediumKind.Water, .75f));
        document.Water.Bodies.Add(new("far-water", .25f, "fresh", "water-look",
            new(MapLatticeFrame.ImportedMetreCentimetre with { RowDirection = MapRowDirection.PositiveZ },
                [new(32002, -31998, 32004, -31996)], []), [new("wet-space", patch.Key)]));
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            DrawRadius = 256,
            DensityRadius = 200
        };
        var foliage = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), assets.Assets.ToDictionary(a => a.Id,
            a => new MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, .35f)));
        var placement = new MapPlacementPolicy([new("far-props", new HashSet<string> { "B" }, [10], 4, 256, 30, 10, 0, true)], 256);
        var policy = new MapRenderPolicy(placement, new HashSet<string>(), foliage,
            MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = 0, WaveSource = WaterWaveSource.Procedural }),
            ["paint"], new Dictionary<ushort, string> { [10] = "paint" });
        var frame = new WorldFrame(250, -250);
        return new(samples, new(document, assets), policy,
            new(new(frame, new(5, 8, 10)), new(frame, Vector3.Zero), true, 12, 640, 480, .1f, 256),
            new(frame, new(.375f, 6, .375f)));
    }
    internal static MapSceneScope Scope(int x = 32000, int z = -32000, int y = 0)
    {
        var frame = WorldFrame.Nearest(x, z);
        return new(new(frame, new(-128), new(128), y - 64, y + 64, Enum.GetValues<MapSurfaceRole>(), null, new()),
            new(x - 128, y - 64, z - 128, x + 128, y + 64, z + 128), MapSceneChannels.All, 1);
    }
    internal static MapRenderSourceState State(NativeOriginRealization realization)
    {
        using var source = MapDocumentSource.FromDocument(realization.Scene.Document);
        var captured = MapSceneSource.Capture(source, realization.Scene.Assets).Source!;
        var proofs = MapRenderExtentValidator.Validate(realization.Scene.Assets, captured.RequiredAssetIds, new(new())).Proofs!;
        return new(captured.WithRenderExtents(proofs).Source!, realization.Scene.Assets, proofs, realization.Policy, [Scope()]);
    }
}
