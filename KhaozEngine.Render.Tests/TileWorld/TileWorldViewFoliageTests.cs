using System.Linq;
using System.Numerics;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldViewFoliageTests
{
    static TileFoliageLayer Layer(byte density = 255) => new(
        "meadow", 0, 0f, -64f, 1f, 65, 65,
        Enumerable.Repeat(density, 65 * 65).ToArray(), 19, 2f, 0.8f, 1.1f, -0.04f,
        [new TileFoliageArchetype("bush", 1f)], [TileRenderTestData.Grass], true, true, 1f, 0.5f);

    static TileWorldView View(RecordingTileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions? options = null) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options);

    [Fact]
    public void View_CachesAndDrawsSavedFoliageThroughLiveOptions()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions
        {
            GroundCover = new GroundCoverRenderOptions { DrawRadius = 200f, QualityDensity = 1f, CastsShadows = false },
        };
        using TileWorldView view = View(scene, doc, options);

        view.LoadRegion(TileRenderTestData.Region);
        Assert.True(view.GeneratedCoverCount > 100);
        view.Draw(Vector3.Zero);

        Assert.True(view.LastDrawnCover > 0);
        TileFoliageDrawRecord draw = Assert.Single(scene.FoliageDraws);
        Assert.Same(options.GroundCover, draw.Options);
        Assert.All(draw.Instances, p =>
        {
            Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, p.Transform));
            Assert.True(up.Y > 0f);
        });
    }

    [Fact]
    public void View_OldWorldDoesNoFoliageWork()
    {
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, TileRenderTestData.HillWorld());
        view.LoadRegion(TileRenderTestData.Region);
        view.Draw(Vector3.Zero);

        Assert.Equal(0, view.GeneratedCoverCount);
        Assert.Equal(0, view.LastDrawnCover);
        Assert.Empty(scene.FoliageDraws);
    }

    [Fact]
    public void View_RebuildsFoliageAfterTerrainAndLayerEditsAndDropsItOnUnload()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetFoliageLayer(Layer());
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        view.LoadRegion(TileRenderTestData.Region);
        int beforeCount = view.GeneratedCoverCount;
        GroundCoverInstance before = view.CoverIn(TileRenderTestData.Region)
            .OrderBy(x => x.Position.X * x.Position.X + x.Position.Z * x.Position.Z).First();

        int tileX = (int)System.MathF.Floor(TileWorldSpace.TileX(before.Position.X, doc.TileSize));
        int tileZ = (int)System.MathF.Floor(TileWorldSpace.TileZ(before.Position.Z, doc.TileSize));
        doc.SetCornerHeightCm(tileX, tileZ, 0, 200);
        doc.SetCornerHeightCm(tileX + 1, tileZ, 0, 200);
        doc.SetCornerHeightCm(tileX, tileZ + 1, 0, 200);
        doc.SetCornerHeightCm(tileX + 1, tileZ + 1, 0, 200);
        view.MarkDirty(new TileRect(tileX, tileZ, 1, 1), 0);
        view.Flush(int.MaxValue);
        GroundCoverInstance afterHeight = view.CoverIn(TileRenderTestData.Region)
            .Single(x => x.ModelId == before.ModelId && x.Position.X == before.Position.X && x.Position.Z == before.Position.Z);
        Assert.NotEqual(before.Transform, afterHeight.Transform);

        doc.SetFoliageLayer(Layer(0));
        view.MarkDirty(TileRenderTestData.Region, 0);
        view.Flush(int.MaxValue);
        Assert.Equal(0, view.GeneratedCoverCount);

        doc.SetFoliageLayer(Layer());
        view.LoadRegion(TileRenderTestData.Region);
        Assert.Equal(beforeCount, view.GeneratedCoverCount);

        GroundCoverInstance blocked = view.CoverIn(TileRenderTestData.Region).First();
        int blockedX = (int)System.MathF.Floor(TileWorldSpace.TileX(blocked.Position.X, doc.TileSize));
        int blockedZ = (int)System.MathF.Floor(TileWorldSpace.TileZ(blocked.Position.Z, doc.TileSize));
        doc.AddObject("tree", blockedX, blockedZ, 0, 0);
        view.MarkDirty(new TileRect(blockedX, blockedZ, 1, 1), 0);
        view.Flush(int.MaxValue);
        Assert.DoesNotContain(view.CoverIn(TileRenderTestData.Region), p =>
            (int)System.MathF.Floor(TileWorldSpace.TileX(p.Position.X, doc.TileSize)) == blockedX &&
            (int)System.MathF.Floor(TileWorldSpace.TileZ(p.Position.Z, doc.TileSize)) == blockedZ);

        view.UnloadRegion(TileRenderTestData.Region);
        Assert.Equal(0, view.GeneratedCoverCount);
    }

    [Fact]
    public void View_RegionCachesMatchOneDistributionAcrossTheirSharedBorder()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldFile.Load(TileRenderTestData.SaveGrid(tmp, 2, 1));
        var layer = new TileFoliageLayer(
            "meadow", 0, 0f, -64f, 1f, 129, 65,
            Enumerable.Repeat((byte)255, 129 * 65).ToArray(), 19, 2f, 0.8f, 1.1f, -0.04f,
            [new TileFoliageArchetype("bush", 1f)], [TileRenderTestData.Grass], true, true, 0f, 0f);
        doc.SetFoliageLayer(layer);
        var scene = new RecordingTileWorldScene();
        using TileWorldView view = View(scene, doc);
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        view.LoadRegion(west);
        view.LoadRegion(east);

        var settings = new GroundCoverSettings
        {
            Seed = layer.Seed,
            Spacing = layer.Spacing,
            ScaleMin = layer.ScaleMin,
            ScaleMax = layer.ScaleMax,
            RootOffset = layer.RootOffset,
            Models = [new GroundCoverModel("bush", 1f)],
        };
        var surface = new TileFoliageSurface(doc, TileRenderTestData.Catalogs, layer);
        string[] expected = GroundCoverDistribution.Generate(
                new RectArea(0f, -64f, 128f, 0f), settings, surface.Sample)
            .Select(Key).OrderBy(x => x, System.StringComparer.Ordinal).ToArray();
        string[] actual = view.CoverIn(west).Concat(view.CoverIn(east))
            .Select(Key).OrderBy(x => x, System.StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal(actual.Length, actual.Distinct(System.StringComparer.Ordinal).Count());
    }

    static string Key(GroundCoverInstance instance) =>
        $"{instance.ModelId}|{instance.Position.X:R}|{instance.Position.Y:R}|{instance.Position.Z:R}|{instance.ThinningRank:R}";
}
