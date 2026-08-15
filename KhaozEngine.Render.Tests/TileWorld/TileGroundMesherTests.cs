using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The ground mesher over full tiles: triangle counts, the void rule, lattice normals across a
/// region border, flat overlays and the magenta a dangling material id renders as.</summary>
public class TileGroundMesherTests
{
    const int TilesPerRegion = TileRegion.Size * TileRegion.Size;

    [Fact]
    public void Flat_grass_region_meshes_two_triangles_per_tile()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        GltfMesh mesh = Require(doc, 0);

        Assert.Equal(TilesPerRegion * 6, mesh.Vertices.Length);
        Assert.Equal(TilesPerRegion * 6, mesh.Indices32.Length);

        float span = TileRegion.Size * doc.TileSize;
        foreach (ModelVertex v in mesh.Vertices)
        {
            Assert.InRange(v.Position.X, 0f, span);
            Assert.InRange(v.Position.Z, 0f, span);
            if (NearTheHill(v.Position, doc.TileSize)) continue;
            Assert.True(v.Normal.Y > 0.99f, $"corner ({v.Position.X}, {v.Position.Z}) should be flat");
        }
    }

    [Fact]
    public void Void_region_plane_returns_null()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        Assert.Null(TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 1));
    }

    [Fact]
    public void No_draw_tiles_are_skipped()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        doc.SetSettings(40, 40, 0, TileSettings.NoDraw);
        GltfMesh mesh = Require(doc, 0);

        Assert.Equal((TilesPerRegion - 1) * 6, mesh.Vertices.Length);
        Assert.Empty(TileVertices(mesh, doc, 40, 40));
    }

    [Fact]
    public void Hill_raises_its_corner_vertices_and_tilts_the_normals_beside_it()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        GltfMesh mesh = Require(doc, 0);

        ModelVertex raised = FindCorner(mesh, 21f * doc.TileSize, 21f * doc.TileSize);
        Assert.Equal(2f, raised.Position.Y, 1e-5f);

        // The lattice climbs toward +x on the hill's west flank, so the normal there leans toward -x.
        Assert.True(TileGroundMesher.CornerNormal(doc, 20, 21, 0).X < 0f);
        Assert.Equal(Vector3.UnitY, TileGroundMesher.CornerNormal(doc, 30, 30, 0));
    }

    [Fact]
    public void Shared_corner_between_two_regions_has_identical_position_and_normal()
    {
        TileWorldDocument doc = BorderSlopeWorld();
        var west = new RegionCoord(0, 0);
        var east = new RegionCoord(1, 0);
        GltfMesh? westMesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, west, 0);
        GltfMesh? eastMesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, east, 0);
        Assert.NotNull(westMesh);
        Assert.NotNull(eastMesh);

        float z = 10f * doc.TileSize;
        ModelVertex a = FindCorner(westMesh!, TileRegion.Size * doc.TileSize, z);
        ModelVertex b = FindCorner(eastMesh!, 0f, z);

        Vector3 wa = Vector3.Transform(a.Position, TileGroundMesher.WorldMatrix(doc, west));
        Vector3 wb = Vector3.Transform(b.Position, TileGroundMesher.WorldMatrix(doc, east));
        Assert.True((wa - wb).Length() < 1e-5f, $"{wa} != {wb}");
        Assert.True((a.Normal - b.Normal).Length() < 1e-5f, $"{a.Normal} != {b.Normal}");
        Assert.True(a.Normal.X < 0f, "the ramp climbs east, so the shared corner's normal leans west");
    }

    [Fact]
    public void Full_overlay_is_flat_and_unjittered()
    {
        TileWorldDocument doc = TileRenderTestData.RoadWorld();
        TileWorldCatalogs catalogs = TileRenderTestData.Catalogs;
        GltfMesh mesh = Require(doc, 0, catalogs);

        Vector4 road = TileColors.Parse(catalogs.Material(TileRenderTestData.Road)!);
        List<ModelVertex> paved = TileVertices(mesh, doc, TileRenderTestData.RoadMinX + 1, TileRenderTestData.RoadZ);
        Assert.Equal(6, paved.Count);
        foreach (ModelVertex v in paved) Assert.Equal(road, v.Color);

        Vector4 grass = TileColors.Parse(catalogs.Material(TileRenderTestData.Grass)!);
        List<ModelVertex> plain = TileVertices(mesh, doc, 30, 30);
        Assert.Equal(6, plain.Count);
        Assert.True(plain.Exists(v => v.Color != plain[0].Color), "jitter and blend should vary a grass tile's corners");

        Vector4 sum = Vector4.Zero;
        foreach (ModelVertex v in plain) sum += v.Color;
        Vector4 average = sum / plain.Count;
        Assert.Equal(grass.X, average.X, 0.05f);
        Assert.Equal(grass.Y, average.Y, 0.05f);
        Assert.Equal(grass.Z, average.Z, 0.05f);
        Assert.Equal(1f, average.W, 1e-5f);
    }

    [Fact]
    public void Missing_material_renders_magenta()
    {
        var doc = new TileWorldDocument { Id = "tile-render-dangling", DisplayName = "Dangling material" };
        doc.GetOrCreateRegion(TileRenderTestData.Region);
        doc.SetUnderlay(40, 40, 0, 99);
        GltfMesh mesh = Require(doc, 0);

        // The tile is alone in a void region, so each of its corners blends exactly one tile: the dangling one.
        Assert.Equal(6, mesh.Vertices.Length);
        foreach (ModelVertex v in mesh.Vertices)
        {
            Assert.InRange(v.Color.X, 0.96f, 1.04f);
            Assert.Equal(0f, v.Color.Y);
            Assert.Equal(v.Color.X, v.Color.Z);
            Assert.Equal(1f, v.Color.W);
        }
    }

    [Fact]
    public void Flat_normals_option_gives_one_normal_per_triangle()
    {
        TileWorldDocument doc = TileRenderTestData.HillWorld();
        var options = new TileGroundMesherOptions { SmoothNormals = false };
        GltfMesh? mesh = TileGroundMesher.Build(doc, TileRenderTestData.Catalogs, TileRenderTestData.Region, 0, options);
        Assert.NotNull(mesh);

        List<ModelVertex> sloped = TileVertices(mesh!, doc, TileRenderTestData.HillMin - 1, TileRenderTestData.HillMin);
        Assert.Equal(6, sloped.Count);
        Assert.Equal(sloped[0].Normal, sloped[1].Normal);
        Assert.Equal(sloped[0].Normal, sloped[2].Normal);
        Assert.True(sloped[0].Normal.Y > 0f, "a flat normal still points up");
        Assert.NotEqual(Vector3.UnitY, sloped[0].Normal);
    }

    static GltfMesh Require(TileWorldDocument doc, int plane, TileWorldCatalogs? catalogs = null)
    {
        GltfMesh? mesh = TileGroundMesher.Build(doc, catalogs ?? TileRenderTestData.Catalogs, TileRenderTestData.Region, plane);
        Assert.NotNull(mesh);
        return mesh!;
    }

    // The hill raises corners 20..22, so only corners 19..23 on both axes can see a height difference.
    static bool NearTheHill(Vector3 p, float tileSize)
    {
        int cx = (int)MathF.Round(p.X / tileSize);
        int cz = (int)MathF.Round(p.Z / tileSize);
        return cx >= TileRenderTestData.HillMin - 1 && cx <= TileRenderTestData.HillMax + 1
            && cz >= TileRenderTestData.HillMin - 1 && cz <= TileRenderTestData.HillMax + 1;
    }

    // Every vertex of every triangle whose centroid falls inside the region-local tile, which identifies a tile
    // without depending on the mesher's emission order.
    static List<ModelVertex> TileVertices(GltfMesh mesh, TileWorldDocument doc, int localX, int localZ)
    {
        var found = new List<ModelVertex>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            ModelVertex a = mesh.Vertices[mesh.Indices32[t * 3]];
            ModelVertex b = mesh.Vertices[mesh.Indices32[t * 3 + 1]];
            ModelVertex c = mesh.Vertices[mesh.Indices32[t * 3 + 2]];
            Vector3 centre = (a.Position + b.Position + c.Position) / 3f;
            if ((int)MathF.Floor(centre.X / doc.TileSize) != localX) continue;
            if ((int)MathF.Floor(centre.Z / doc.TileSize) != localZ) continue;
            found.Add(a);
            found.Add(b);
            found.Add(c);
        }
        return found;
    }

    // The one vertex the mesh carries at this region-local corner, asserting every copy of it agrees.
    static ModelVertex FindCorner(GltfMesh mesh, float x, float z)
    {
        ModelVertex? first = null;
        foreach (ModelVertex v in mesh.Vertices)
        {
            if (MathF.Abs(v.Position.X - x) > 1e-4f || MathF.Abs(v.Position.Z - z) > 1e-4f) continue;
            if (first is null) { first = v; continue; }
            Assert.Equal(first.Value.Position, v.Position);
            Assert.Equal(first.Value.Normal, v.Normal);
        }
        Assert.True(first.HasValue, $"no vertex at region-local ({x}, {z})");
        return first!.Value;
    }

    // Two regions of grass with a ramp climbing east straight through their shared border at x = 64.
    static TileWorldDocument BorderSlopeWorld()
    {
        var doc = new TileWorldDocument { Id = "tile-render-border", DisplayName = "Border slope" };
        doc.GetOrCreateRegion(new RegionCoord(0, 0));
        doc.GetOrCreateRegion(new RegionCoord(1, 0));
        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = 0; x < TileRegion.Size * 2; x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);

        for (int z = 0; z < TileRegion.Size; z++)
            for (int x = TileRegion.Size - 4; x <= TileRegion.Size + 4; x++)
                doc.SetCornerHeightCm(x, z, 0, (short)((x - (TileRegion.Size - 4)) * 50));
        return doc;
    }
}
