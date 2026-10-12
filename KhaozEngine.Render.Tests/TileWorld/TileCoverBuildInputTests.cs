using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileCoverBuildInputTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Captured_cover_matches_live_cover_at_capture(bool smoothNormals)
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        GroundCoverInstance[] expectedAtCapture = TileCoverTestWorld.Live(document, catalogs, smoothNormals);
        var input = TileCoverBuildInput.Capture(document, catalogs, TileCoverTestWorld.Region,
            smoothNormals, new TileCoverObjectIndex(document, catalogs));

        Assert.NotEmpty(expectedAtCapture);
        Assert.Contains(expectedAtCapture, instance => instance.ModelId == "lower");
        Assert.Contains(expectedAtCapture, instance => instance.ModelId == "upper");
        Assert.IsType<GroundCoverBatch>(input.Build());
        Assert.Equal(expectedAtCapture, input.Build());
    }

    [Fact]
    public void Captured_cover_ignores_later_source_mutations()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        GroundCoverInstance[] expectedAtCapture = TileCoverTestWorld.Live(document, catalogs, true);
        var input = TileCoverBuildInput.Capture(document, catalogs, TileCoverTestWorld.Region,
            true, new TileCoverObjectIndex(document, catalogs));
        Assert.Equal(expectedAtCapture, input.Build());

        foreach (TileRegion region in document.Regions.Values)
            foreach (TilePlaneData plane in region.Planes)
            {
                if (plane.Heights is { } heights) Array.Fill(heights, (short)900);
                if (plane.Underlay is { } underlay) Array.Fill(underlay, (ushort)2);
                if (plane.Overlay is { } overlay) Array.Fill(overlay, (ushort)1);
                if (plane.OverlayShape is { } shape) Array.Fill(shape, (byte)TileOverlayShape.Full);
                if (plane.OverlayRotation is { } rotation) Array.Fill(rotation, (byte)3);
                if (plane.Settings is { } settings) Array.Fill(settings, (byte)TileSettings.Indoors);
            }
        foreach (TileObject obj in document.AllObjects())
        {
            obj.X += 20;
            obj.Rotation = 1;
            obj.Tags?.Clear();
        }
        catalogs.Material(1)!.Kind = GroundMaterialKind.Water;
        catalogs.Material(2)!.Kind = GroundMaterialKind.Ground;
        foreach (TileObjectArchetype archetype in catalogs.Archetypes.Values)
        {
            archetype.SizeX = 1;
            archetype.SizeZ = 1;
            archetype.IsRoof = false;
            archetype.CollisionKind = TileCollisionKind.None;
            archetype.Tags?.Clear();
        }
        document.TileSize = 0.5f;
        document.PlaneHeight = 9f;
        Assert.Equal(expectedAtCapture, input.Build());

        TileFoliageLayer layer = document.FoliageLayers[0];
        document.SetFoliageLayer(layer.WithDensity(new byte[layer.Width * layer.Height]));
        document.RemoveFoliageLayer("upper");
        Assert.Equal(expectedAtCapture, input.Build());
    }

    [Fact]
    public void Distant_door_and_upper_roof_are_not_limited_to_ground_ring()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        var objects = new TileCoverObjectIndex(document, catalogs);
        IReadOnlyList<TileCoverObject> candidates = objects.Query(TileCoverTestWorld.Region, document.FoliageLayers);
        var distantRoof = new TileCoverObject(new TileRect(-192, -42, 146, 4), 2, false, true, false);
        var distantDoor = new TileCoverObject(new TileRect(-129, -30, 1, 1), 0, false, false, true);
        Assert.Contains(distantRoof, candidates);
        Assert.Contains(distantDoor, candidates);

        var input = TileCoverBuildInput.Capture(document, catalogs, TileCoverTestWorld.Region, true, objects);
        GroundCoverInstance[] expectedAtCapture = TileCoverTestWorld.Live(document, catalogs, true);
        Assert.Equal(expectedAtCapture, input.Build());
        TileObject[] distant = document.AllObjects().Where(obj => obj.X < -128).ToArray();
        foreach (TileObject obj in distant) document.RemoveObject(obj.Id);
        Assert.False(expectedAtCapture.SequenceEqual(TileCoverTestWorld.Live(document, catalogs, true)));
        Assert.Equal(expectedAtCapture, input.Build());
    }

    [Fact]
    public void Ground_and_material_snapshots_own_their_values()
    {
        var (document, catalogs) = TileCoverTestWorld.Create();
        GroundMaterial source = catalogs.Material(1)!;
        source.Texture = "ground.png";
        source.TilesPerMetre = 0.75f;
        TileWorldDocument ground = TileWorldBuildSnapshot.CaptureGround(document, TileCoverTestWorld.Region);
        TileWorldCatalogs materials = TileWorldBuildSnapshot.CaptureMaterials(catalogs);
        Assert.Equal(9, ground.Regions.Count);
        Assert.Empty(ground.AllObjects());
        Assert.Empty(materials.Archetypes);
        GroundMaterial copy = materials.Material(1)!;
        Assert.NotSame(source, copy);
        Assert.Equal((source.Id, source.Name, source.Color, source.Texture, source.Kind, source.TilesPerMetre),
            (copy.Id, copy.Name, copy.Color, copy.Texture, copy.Kind, copy.TilesPerMetre));
        document.SetCornerHeightCm(-64, -64, 0, 1234);
        source.Color = "#000000";
        source.Texture = "later.png";
        Assert.NotEqual(document.CornerHeightCm(-64, -64, 0), ground.CornerHeightCm(-64, -64, 0));
        Assert.Equal("#4d8a3a", copy.Color);
        Assert.Equal("ground.png", copy.Texture);
    }
}

internal static class TileCoverTestWorld
{
    internal static readonly RegionCoord Region = new(-1, -1);

    internal static (TileWorldDocument Document, TileWorldCatalogs Catalogs) Create()
    {
        var document = new TileWorldDocument { TileSize = 2.5f, PlaneCount = 3 };
        var catalogs = TileWorldCatalogs.Greybox();
        catalogs.Archetype("roof_flat")!.SizeX = 146;
        catalogs.Archetype("roof_flat")!.SizeZ = 4;
        catalogs.Archetype("doorway")!.Tags = ["DoOr"];
        for (int rz = -2; rz <= 0; rz++)
            for (int rx = -2; rx <= 0; rx++)
            {
                TileRegion region = document.GetOrCreateRegion(new RegionCoord(rx, rz));
                for (int plane = 0; plane < 2; plane++)
                {
                    TilePlaneData data = region.Planes[plane];
                    data.Underlay = Enumerable.Repeat((ushort)1, TileRegion.TileCount).ToArray();
                    data.Heights = new short[TileRegion.TileCount];
                    for (int z = 0; z < TileRegion.Size; z++)
                        for (int x = 0; x < TileRegion.Size; x++)
                            data.Heights[TilePlaneData.Index(x, z)] = (short)(plane * 300 +
                                (region.Coord.OriginX + x) * 2 + (region.Coord.OriginZ + z) * 3 + (x % 2) * 13);
                }
            }
        document.GetOrCreateRegion(new RegionCoord(-3, -1));
        document.AddObject("roof_flat", -192, -42, 2, 0);
        document.AddObject("doorway", -129, -30, 0, 0);
        document.AddObject("bush", -20, -20, 1, 0, ["door"]);
        document.AddObject("tree", 0, -20, 0, 0);
        document.AddObject("tree", -10, -10, 0, 0);
        document.SetSettings(-30, -30, 0, TileSettings.Indoors);
        for (int plane = 0; plane < 2; plane++)
            for (int x = -64; x < 0; x++)
            {
                document.SetOverlay(x, -12, plane, 2);
                document.SetOverlayShape(x, -12, plane, (TileOverlayShape)(1 + ((x + 64) % 3)));
                document.SetOverlayRotation(x, -12, plane, (x + 64) % 4);
            }
        document.SetFoliageLayer(Layer("lower", 0, 171.25f));
        document.SetFoliageLayer(Layer("upper", 1, 4.25f));
        return (document, catalogs);
    }

    internal static TileFoliageLayer Layer(string id, int plane, float clearance,
        bool excludeSolid = true, bool excludeIndoors = true) => new(
        id, plane, -480f, -160f, 2.5f, 257, 257,
        Enumerable.Range(0, 257 * 257).Select(i => (byte)(96 + i % 160)).ToArray(),
        37 + plane, 3.75f, 0.8f, 1.2f, -0.04f, [new TileFoliageArchetype(id, 1f)],
        [1], excludeIndoors, excludeSolid, clearance, 2f);

    internal static GroundCoverInstance[] Live(TileWorldDocument document, TileWorldCatalogs catalogs,
        bool smoothNormals)
    {
        var area = new RectArea(-64 * document.TileSize, 0, 0, 64 * document.TileSize);
        var result = new List<GroundCoverInstance>();
        foreach (TileFoliageLayer layer in document.FoliageLayers)
        {
            var surface = new TileFoliageSurface(document, catalogs, layer, smoothNormals);
            var settings = new GroundCoverSettings
            {
                Seed = layer.Seed,
                Spacing = layer.Spacing,
                ScaleMin = layer.ScaleMin,
                ScaleMax = layer.ScaleMax,
                RootOffset = layer.RootOffset,
                Models = layer.Archetypes.Select(row => new GroundCoverModel(row.Id, row.Weight)).ToArray(),
            };
            result.AddRange(GroundCoverDistribution.Generate(area, settings, surface.Sample));
        }
        return result.ToArray();
    }
}
