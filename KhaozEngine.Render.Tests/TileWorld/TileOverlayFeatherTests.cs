using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileOverlayFeatherTests
{
    [Fact]
    public void A_flagged_road_fades_inward_into_its_grass_underlay_only_at_the_edge()
    {
        TileWorldDocument doc = World();
        Road(doc, 10, 10);
        ModelVertex[] vertices = Build(doc).Vertices;
        Assert.All(At(vertices, 10, 10.5f), v => Assert.Equal(0f, RoadWeight(v), 5));
        Assert.All(At(vertices, 10.5f, 10.5f), v => Assert.Equal(1f, RoadWeight(v), 5));
        Assert.Contains(vertices, v => v.Position.X > 10 && v.Position.X < 10.2f && RoadWeight(v) is > 0 and < 1);
        Assert.All(vertices.Where(v => v.Tangent.W > 0), v => Assert.Equal(new Vector2(1, 1), v.Uv));
    }

    [Fact]
    public void A_shared_edge_inside_the_road_does_not_fade()
    {
        TileWorldDocument doc = World();
        Road(doc, 10, 10);
        Road(doc, 11, 10);
        Assert.All(At(Build(doc).Vertices, 11, 10.5f), v => Assert.Equal(1f, RoadWeight(v), 5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_diagonal_cut_matches_the_underlay_along_its_whole_edge(int rotation)
    {
        TileWorldDocument doc = World();
        Road(doc, 10, 10);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(10, 10, 0, rotation);
        ModelVertex[] vertices = Build(doc).Vertices;
        Assert.All(At(vertices, 10.5f, 10.5f), v => Assert.Equal(0f, RoadWeight(v), 5));
        Assert.Contains(vertices, v => RoadWeight(v) > 0.99f);
    }

    [Fact]
    public void Joined_regions_agree_on_the_fade_and_the_solid_center()
    {
        TileWorldDocument doc = World();
        doc.GetOrCreateRegion(new RegionCoord(1, 0));
        for (int z = 8; z <= 12; z++)
            for (int x = 61; x <= 66; x++) doc.SetUnderlay(x, z, 0, 1);
        Road(doc, 63, 10);
        Road(doc, 64, 10);
        ModelVertex[] west = Build(doc).Vertices;
        ModelVertex[] east = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(1, 0), 0)!.Vertices;
        foreach (float z in new[] { 10f, 10.0625f, 10.125f, 10.5f, 11f })
        {
            float a = RoadWeight(At(west, 64, z).First());
            Assert.All(At(east, 0, z), v => Assert.Equal(a, RoadWeight(v), 5));
        }
        Assert.All(At(west, 64, 10.5f), v => Assert.Equal(1f, RoadWeight(v), 5));
    }

    [Fact]
    public void Unflagged_overlays_keep_their_original_six_vertices_and_packing()
    {
        TileWorldDocument doc = new();
        Road(doc, 10, 10, feather: false);
        GltfMesh mesh = Build(doc);
        Assert.Equal(6, mesh.Vertices.Length);
        Assert.All(mesh.Vertices, v =>
        {
            Assert.Equal(new Vector4(1, 0, 0, 0), v.Color);
            Assert.Equal(new Vector2(6, 6), v.Uv);
            Assert.Equal(new Vector4(6, 6, v.Tangent.Z, 0), v.Tangent);
        });
    }

    [Theory]
    [InlineData(TileOverlayShape.CornerQuarter)]
    [InlineData(TileOverlayShape.CornerThreeQuarter)]
    [InlineData(TileOverlayShape.DiagonalHalf)]
    [InlineData(TileOverlayShape.Full)]
    public void Feather_vertices_remain_on_the_authoritative_nonplanar_surface(TileOverlayShape shape)
    {
        TileWorldDocument doc = World();
        Road(doc, 10, 10);
        doc.SetOverlayShape(10, 10, 0, shape);
        doc.SetCornerHeightCm(10, 10, 0, 23);
        doc.SetCornerHeightCm(11, 11, 0, 74);
        doc.SetCornerHeightCm(11, 10, 0, 12);
        foreach (ModelVertex v in Build(doc).Vertices.Where(v => v.Tangent.W > 0))
        {
            TileHit? hit = TileRaycast.Pick(doc, 0, v.Position + Vector3.UnitY * 10, -Vector3.UnitY);
            Assert.NotNull(hit);
            Assert.Equal(v.Position.Y, hit.Value.Point.Y, 0.00001f);
        }
    }

    [Fact]
    public void A_half_tile_join_keeps_the_covered_half_solid_and_fades_the_exposed_half()
    {
        TileWorldDocument doc = World();
        Road(doc, 10, 10);
        Road(doc, 11, 10);
        doc.SetOverlayShape(11, 10, 0, TileOverlayShape.CornerQuarter);
        ModelVertex[] vertices = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(0, 0), 0,
            new TileGroundMesherOptions { OverlayFeatherWidthMetres = 0.1f })!.Vertices;
        Assert.All(At(vertices, 11, 10.25f).Where(v => v.Tangent.W > 0), v => Assert.Equal(1f, RoadWeight(v), 5));
        Assert.All(At(vertices, 11, 10.75f).Where(v => v.Tangent.W > 0), v => Assert.Equal(0f, RoadWeight(v), 5));
    }

    [Fact]
    public void A_distant_region_uses_the_same_local_feather_as_the_origin_region()
    {
        TileWorldDocument near = World();
        Road(near, 10, 10);
        var far = new TileWorldDocument();
        const int offset = 64000000;
        far.GetOrCreateRegion(new RegionCoord(offset / 64, 0));
        for (int z = 8; z <= 13; z++)
            for (int x = 8; x <= 13; x++) far.SetUnderlay(x + offset, z, 0, 1);
        Road(far, 10 + offset, 10);
        GltfMesh actual = TileGroundMesher.Build(far, TileRenderTestData.Catalogs, new RegionCoord(offset / 64, 0), 0,
            new TileGroundMesherOptions { JitterAmplitude = 0 })!;
        GltfMesh expected = TileGroundMesher.Build(near, TileRenderTestData.Catalogs, new RegionCoord(0, 0), 0,
            new TileGroundMesherOptions { JitterAmplitude = 0 })!;
        Assert.Equal(expected.Vertices.Select(v => (v.Position, v.Color, v.Tangent)),
            actual.Vertices.Select(v => (v.Position, v.Color, v.Tangent)));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_widths_are_rejected(float width) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TileGroundMesherOptions { OverlayFeatherWidthMetres = width });

    static TileWorldDocument World()
    {
        var doc = new TileWorldDocument();
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        for (int z = 8; z <= 13; z++)
            for (int x = 8; x <= 13; x++) doc.SetUnderlay(x, z, 0, 1);
        return doc;
    }

    static void Road(TileWorldDocument doc, int x, int z, bool feather = true)
    {
        doc.GetOrCreateRegion(RegionCoord.Of(x, z));
        doc.SetUnderlay(x, z, 0, 1);
        doc.SetOverlay(x, z, 0, 6);
        doc.SetSettings(x, z, 0, feather ? TileSettings.FeatherOverlay : TileSettings.None);
    }

    static GltfMesh Build(TileWorldDocument doc) =>
        TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, new RegionCoord(0, 0), 0)!;

    static ModelVertex[] At(ModelVertex[] vertices, float x, float z)
    {
        ModelVertex[] result = vertices.Where(v => Math.Abs(v.Position.X - x) < 0.0001f &&
            Math.Abs(v.Position.Z + z) < 0.0001f).ToArray();
        Assert.NotEmpty(result);
        return result;
    }

    static float RoadWeight(ModelVertex v) => v.Tangent.W > 0
        ? 1 - Vector4.Dot(v.Color, Vector4.One)
        : v.Uv.X == 6 ? 1 : 0;
}
