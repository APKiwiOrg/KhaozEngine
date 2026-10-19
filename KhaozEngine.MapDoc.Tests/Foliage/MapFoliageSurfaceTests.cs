using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

/// <summary>The legacy-tile compatibility surface over native data: released interpolation, smoothed corner normals,
/// cuts, flags and visible paint. Expected legacy bits are the literals Task 3 froze from the released TileWorld sampler
/// at f1cd6656b (Render.Tests/Terrain/TileFoliageReleasedFixtures.cs), copied here for the cases whose features the native
/// fixture reproduces. Density literals are compared only where no clearance, door or edge fade decides them.</summary>
public sealed class MapFoliageSurfaceTests
{
    static readonly MapFoliageLimits Limits = new();

    static readonly (float X, float Z)[] SlopePoints = [(10.25f, 10.5f), (10.75f, 11.2f), (11.5f, 10.1f), (12.9f, 12.9f), (0.1f, 0.1f), (5.5f, 7.3f)];
    static readonly (float X, float Z)[] CutPoints = [(40.2f, 40.2f), (40.8f, 40.2f), (40.2f, 40.8f), (40.8f, 40.8f), (40.5f, 40.5f), (40.35f, 40.6f)];
    static readonly (float X, float Z)[] CellPoints = [(30.5f, 30.5f), (30.2f, 30.8f), (33.5f, 30.5f)];

    [Fact]
    public void LegacyFlagsCutsAndBorderNormals_MatchTheNamedPolicy()
    {
        // Relief, two tile sizes and the released corner normals, including both lattice edges and a corner beyond them.
        AssertCase("slope", 1f, SlopePoints, Released.Slope, density: true);
        AssertCase("slope-2m", 2f, SlopePoints, Released.Slope2m, density: true);
        AssertCorners("slope", Grid(10, 12), Released.SlopeCorners);
        AssertCorners("slope-2m", Grid(10, 12), Released.Slope2mCorners);
        AssertCorners("control", [(63, 20), (64, 20), (65, 20), (0, 20), (-1, 20), (20, 64)], Released.RegionEdgeCorners);
        AssertCorners("cut-full-r0", [(40, 40), (41, 40), (40, 41), (41, 41)], Released.CutFullCorners);

        // All four cuts by four rotations: the selected triangle's overlay flag decides road (not allowed) or grass.
        foreach (string cut in FoliageFixtures.CutVariants())
        {
            IReadOnlyList<MapFoliageSurfacePoint> points = AssertCase(cut, 1f, CutPoints, Released.Cuts[cut], density: true);
            foreach (MapFoliageSurfacePoint point in points)
                Assert.Equal(point.Density == 0 ? MapFoliageReason.MaterialNotAllowed : MapFoliageReason.Accepted, point.Reason);
            Assert.All(points, p => Assert.Equal(p.Reason == MapFoliageReason.Accepted ? FoliageFixtures.Grass : FoliageFixtures.Road, p.VisibleMaterial));
        }

        // Each rejection predicate rejects its first point only, by its own reason, with the released height and normal.
        foreach (var (variant, reason) in new[]
        {
            ("void-underlay", MapFoliageReason.MissingUnderlay), ("allowed-underlay", MapFoliageReason.MaterialNotAllowed),
            ("water", MapFoliageReason.WaterMaterial), ("indoors", MapFoliageReason.Indoor), ("zero-raster", MapFoliageReason.RasterZero),
        })
        {
            IReadOnlyList<MapFoliageSurfacePoint> points = AssertCase(variant, 1f, CellPoints,
                variant == "zero-raster" ? Released.ZeroRaster : Released.Rejection, density: true);
            Assert.Equal(reason, points[0].Reason);
            Assert.Equal(MapFoliageReason.Accepted, points[2].Reason);
        }

        // NoDraw and Blocked are ignored exactly as the released predicate ignores them. The NoDraw cell has no physical
        // face, so its support is the logical compatibility triangle on the bound surface, never another floor.
        AssertCase("nodraw", 1f, CellPoints, Released.Ignored, density: true);
        AssertCase("blocked", 1f, CellPoints, Released.Ignored, density: true);
        IReadOnlyList<MapFoliageSurfacePoint> noFlagsDecision = Samples("control", 1f, CellPoints);
        IReadOnlyList<MapFoliageSurfacePoint> noDrawAndBlockedDecision = Samples("nodraw-blocked", 1f, CellPoints);
        Assert.Equal(noFlagsDecision, noDrawAndBlockedDecision);
        Assert.Equal(FoliageFixtures.GroundId, noDrawAndBlockedDecision[0].SupportOwnerId);
        Assert.Equal(new MapPatchKey(FoliageFixtures.GroundId, 0, 0), noDrawAndBlockedDecision[0].Face!.Value.Patch);
        Assert.Equal(30 * 64 + 30, noDrawAndBlockedDecision[0].Face!.Value.Primitive);

        // A missing neighbour slot yields no sample, while the in-domain point keeps the released bits. On the region edge
        // the normals read the known-absence boundary height rather than an extended slot.
        IReadOnlyList<MapFoliageSurfacePoint> absent = Samples("control", 1f, [(64.5f, 20.5f), (64.1f, 20.9f), (63.5f, 20.5f)]);
        Assert.All(absent.Take(2), p => Assert.Equal((MapFoliageStatus.Complete, MapFoliageReason.OutsideDomain, (MapFaceKey?)null),
            (p.Status, p.Reason, p.Face)));
        AssertBits(absent[2], Released.AbsentInDomain, density: true);
        IReadOnlyList<MapFoliageSurfacePoint> edge = Samples("control", 1f, [(63.9f, 20.5f), (63.5f, 20.5f), (64.5f, 20.5f), (-0.5f, 20.5f), (20.5f, 63.95f)]);
        foreach (int i in new[] { 0, 1, 4 }) AssertBits(edge[i], Released.RegionEdge[i], density: false);
        Assert.Equal(MapFoliageReason.OutsideDomain, edge[2].Reason);
        Assert.Equal(MapFoliageReason.OutsideDomain, edge[3].Reason);
    }

    [Fact]
    public void LegacyBorder63To65_DoesNotExtendAnAbsentSlot()
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(FoliageFixtures.SlopedBorder());
        MapScopedSurfaces view = context.Resources.CompleteView();
        float[] heights = new long[] { 63, 64, 65 }.Select(x => MapFoliageNormals.CornerHeight(view, FoliageFixtures.FloorId, x, 32, 0)).ToArray();
        Assert.Equal(new[] { 1f, 1.3f, 0f }, heights);
        Assert.NotEqual(1.3f, heights[2]);
        Assert.Equal(Vector3.Normalize(new(-(1.3f - 1f) / 2, 1, 0)), MapFoliageNormals.Corner(view, FoliageFixtures.FloorId, 63, 32, 0));
        Assert.Equal(Vector3.Normalize(new(0.5f, 1, 0)), MapFoliageNormals.Corner(view, FoliageFixtures.FloorId, 64, 32, 0));
        Assert.Equal(Vector3.Normalize(new(1.3f / 2, 1, 0)), MapFoliageNormals.Corner(view, FoliageFixtures.FloorId, 65, 32, 0));

        // Through prepared sampling: on the south edge of cell (63, 32) the released weights are one half, one half, zero.
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(context, layer, new(1, 0), Limits);
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);
        MapFoliageSurfacePoint point = prepared.Sample(63.5f, -32f);
        Vector3 n63 = Vector3.Normalize(new(-(1.3f - 1f) / 2, 1, 0)), n64 = Vector3.Normalize(new(0.5f, 1, 0));
        Vector3 n6433 = Vector3.Normalize(new(0.5f, 1, (1f - 1.3f) / 2));
        Assert.Equal(1f * 0.5f + 1.3f * 0.5f + 1f * 0f, point.Height);
        Assert.Equal(Vector3.Normalize(n63 * 0.5f + n64 * 0.5f + n6433 * 0f), point.Normal);
        Assert.Equal(MapFoliageReason.Accepted, point.Reason);
        Assert.Equal(MapFoliageReason.OutsideDomain, prepared.Sample(64.5f, -32f).Reason);
        Assert.Contains("normal-slot|floor|1|0|absent", prepared.DependencyParts);
    }

    [Fact]
    public void UnavailableNormalHalo_IsNotAnEdgeExtension()
    {
        FoliageFixture fixture = FoliageFixtures.UnavailableHalo();
        MapFoliageGenerationContext context = FoliageFixtures.UnavailableContext(fixture);
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        foreach (int chunkX in new[] { 0, 1 })
        {
            MapPreparedFoliageSupport prepared = MapFoliageSupport.Prepare(context, layer, new(chunkX, 0), Limits);
            Assert.Equal(MapFoliageStatus.Unavailable, prepared.Status);
            // Refused even where the raster is zero, so no caller can publish an edge-extended zero-density sample.
            float x = chunkX == 0 ? 61f : 100f;
            Assert.Equal(chunkX == 0 ? 200 / 255f : 0f, MapFoliageSupport.RasterDensity(layer, x, -32f));
            Assert.Equal(MapFoliageStatus.Unavailable, prepared.Sample(x, -32f).Status);
        }
        MapScopedSurfaces view = context.Acquire(MapFoliageSupport.Scope(layer, new(0, 0), context.Resources.SurfaceSource.Surfaces,
            context.Resources.Transforms));
        MapFoliageRefusal refusal = Assert.Throws<MapFoliageRefusal>(() => MapFoliageNormals.Corner(view, FoliageFixtures.FloorId, 64, 32, 0));
        Assert.Equal(MapFoliageStatus.Unavailable, refusal.Status);

        // The same document with slot 1 known absent prepares and reads the boundary height.
        MapFoliageGenerationContext complete = FoliageFixtures.Context(FoliageFixtures.SlopedBorder());
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageSupport.Prepare(complete, complete.Foliage!.Layers[0], new(0, 0), Limits).Status);
    }

    [Fact]
    public void ScopedPaint_PartialThenWorldThenCellDecideTheVisibleMaterial()
    {
        PrefabFixture f = PrefabFixtures.Create("paint-notch");
        PrefabFixtures.AddReleasedWorldPaint(f, false, 2);
        MapEffectiveResources resources = MapPrefabResolver.Expand(f.Document, f.Assets);
        MapPreparedFoliageSupport prepared = PreparePaint(resources);
        Assert.Equal(MapFoliageStatus.Complete, prepared.Status);
        MapPaintRegion[] partial = resources.PaintRegions["ground"].Regions.ToArray();

        // World paint "priority-high" (8) owns cells x 1..3 of rows 2..3, "priority-low" (7) only cell x 0.
        static bool WorldHigh(float x, float z) => x > 1 && x < 4 && z > -4 && z < -2;
        MapPaintRegion inside = partial.First(r => Centroid(r) is var c && WorldHigh(c.X, c.Y));
        Vector2 centre = Centroid(inside);
        MapFoliageSurfacePoint partialDecides = prepared.Sample(centre.X, centre.Y);
        Assert.Equal((MapFoliageReason.Accepted, (ushort)7, inside.Face), (partialDecides.Reason, partialDecides.VisibleMaterial, partialDecides.Face!.Value));

        MapFoliageSurfacePoint worldDecides = prepared.Sample(1.2f, -2.5f);
        Assert.DoesNotContain(partial, r => Holds(r, 1.2f, -2.5f));
        Assert.Equal(8, worldDecides.VisibleMaterial);
        Assert.Equal(7, prepared.Sample(0.5f, -3.5f).VisibleMaterial);
        Assert.Equal(3, prepared.Sample(6.5f, -6.5f).VisibleMaterial);

        // The unpainted notch keeps the face's own or world coverage, never the partial source's material.
        MapExactPoint notch = new MapResourceTransform(resources.Transforms["Surface:a/paint"]).Point(new(new(3, 2), default, new(-3, 2)));
        float nx = notch.X.ToSingle(), nz = notch.Z.ToSingle();
        Assert.DoesNotContain(partial, r => Holds(r, nx, nz));
        Assert.Equal(WorldHigh(nx, nz) ? 8 : 3, prepared.Sample(nx, nz).VisibleMaterial);
    }

    [Fact]
    public void PartialPaintPriority_HigherRegionOwnsTheOverlap()
    {
        MapEffectiveResources resources = MapPrefabResolver.Expand(PrefabFixtures.Create("prioritized-paint").Document,
            PrefabFixtures.Create("prioritized-paint").Assets);
        Assert.True(PrefabFixtures.HighPriorityOwnsOverlap(resources));
        MapPreparedFoliageSupport prepared = PreparePaint(resources);
        Assert.Equal((ushort)7, prepared.Sample(0.5f, -0.5f).VisibleMaterial);
        Assert.Equal((ushort)8, prepared.Sample(1.5f, -0.5f).VisibleMaterial);
        Assert.Equal((ushort)8, prepared.Sample(3.5f, -1.5f).VisibleMaterial);
        Assert.Equal((ushort)3, prepared.Sample(5.5f, -0.5f).VisibleMaterial);
    }

    [Fact]
    public void LegacyOverlayContainment_AtTheCellCentreFollowsTheAuthoredCut()
    {
        // The neighbour predicate reads the authored cut at the cell centre within 1e-6: a full or three-quarter overlay
        // holds it, the diagonal half holds it on its diagonal, and the corner quarter never reaches it.
        foreach (var (cut, expected) in new[] { (MapOverlayCut.Full, true), (MapOverlayCut.DiagonalHalf, true),
            (MapOverlayCut.CornerQuarter, false), (MapOverlayCut.CornerThreeQuarter, true) })
            for (byte rotation = 0; rotation < 4; rotation++)
            {
                var cell = new MapSurfaceCell(1, 6, cut, rotation, MapCellFlags.None, MapCellTopology.Auto);
                bool split = MapSurfaceTopology.SplitSwNe(0, 0, 0, 0, cut, rotation, MapCellTopology.Auto);
                Assert.Equal(expected, MapFoliageMaterials.OverlayAt(cell, split, 0.5f, 0.5f));
            }
    }

    static MapPreparedFoliageSupport PreparePaint(MapEffectiveResources resources)
    {
        MapFoliageLayer layer = FoliageFixtures.Layer("paint-cover", FoliageFixtures.Surface("ground"), -1, -9, 2, 6, 6,
            Enumerable.Repeat((byte)200, 36).ToArray(), []);
        var chunk = new MapFoliageChunkKey(0, 0);
        MapSurfaceScope scope = MapFoliageSupport.Scope(layer, chunk, resources.SurfaceSource.Surfaces, resources.Transforms);
        return MapFoliageSupport.Prepare(resources.Acquire(scope), resources.PaintRegions, resources.SurfaceSource.Surfaces, layer, chunk, Limits);
    }

    static Vector2 Centroid(MapPaintRegion region) => new(
        (float)region.Polygon.Average(p => p.X.ToDouble()), (float)region.Polygon.Average(p => p.Z.ToDouble()));

    static bool Holds(MapPaintRegion region, float x, float z) => PrefabFixtures.PaintContains(region.Polygon,
        new(MapExactValue.FromSingle(x), default, MapExactValue.FromSingle(z)));

    static IReadOnlyList<MapFoliageSurfacePoint> AssertCase(string variant, float tileSize, (float X, float Z)[] tiles, uint[][] expected, bool density)
    {
        IReadOnlyList<MapFoliageSurfacePoint> points = Samples(variant, tileSize, tiles);
        Assert.Equal(expected.Length, points.Count);
        for (int i = 0; i < points.Count; i++) AssertBits(points[i], expected[i], density);
        return points;
    }

    static void AssertBits(MapFoliageSurfacePoint point, uint[] expected, bool density)
    {
        Assert.Equal(MapFoliageStatus.Complete, point.Status);
        uint[] actual = [Bits(point.Height), Bits(point.Normal.X), Bits(point.Normal.Y), Bits(point.Normal.Z), Bits(point.Density)];
        Assert.Equal(density ? expected : expected[..4], density ? actual : actual[..4]);
    }

    // Samples released tile points through one preparation per touched chunk of the fixture's own context.
    static IReadOnlyList<MapFoliageSurfacePoint> Samples(string variant, float tileSize, (float X, float Z)[] tiles)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(FoliageFixtures.FlagsAndCuts(variant));
        MapFoliageLayer layer = context.Foliage!.Layers[0];
        var prepared = new Dictionary<MapFoliageChunkKey, MapPreparedFoliageSupport>();
        var points = new List<MapFoliageSurfacePoint>();
        foreach (var (tx, tz) in tiles)
        {
            float x = tx * tileSize, z = -tz * tileSize;
            var key = new MapFoliageChunkKey((int)MathF.Floor((x - layer.OriginX) / (32 * layer.CellSize)),
                (int)MathF.Floor((z - layer.OriginZ) / (32 * layer.CellSize)));
            if (!prepared.TryGetValue(key, out MapPreparedFoliageSupport? chunk))
            {
                prepared.Add(key, chunk = MapFoliageSupport.Prepare(context, layer, key, Limits));
                Assert.Equal(MapFoliageStatus.Complete, chunk.Status);
            }
            points.Add(chunk.Sample(x, z));
        }
        return points;
    }

    static void AssertCorners(string variant, (long X, long Z)[] corners, uint[][] expected)
    {
        MapFoliageGenerationContext context = FoliageFixtures.Context(FoliageFixtures.FlagsAndCuts(variant));
        MapScopedSurfaces view = context.Resources.CompleteView();
        Assert.Equal(expected.Length, corners.Length);
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 normal = MapFoliageNormals.Corner(view, FoliageFixtures.GroundId, corners[i].X, corners[i].Z, 0);
            Assert.Equal(expected[i], new[] { Bits(normal.X), Bits(normal.Y), Bits(normal.Z) });
        }
    }

    static (long X, long Z)[] Grid(int lo, int hi) =>
        Enumerable.Range(lo, hi - lo + 1).SelectMany(z => Enumerable.Range(lo, hi - lo + 1).Select(x => ((long)x, (long)z))).ToArray();

    static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    // Bits Task 3 froze from the released TileFoliageSurface and TileGroundMesher.CornerNormal at f1cd6656b
    // (Render.Tests/Terrain/TileFoliageReleasedFixtures.cs). Rows: height, normal X, Y, Z, density. Corner rows: normal X, Y, Z.
    static class Released
    {
        internal static readonly uint[][] Slope = new uint[][]
        {
            [0x3EF33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x3F0CECEDu],
            [0x3ECF5C2Du, 0xBC8FDB7Au, 0x3F7FE16Du, 0xBCCCB454u, 0x3F275423u],
            [0xBCA3D698u, 0x3E3F1F31u, 0x3F74BD4Bu, 0x3E67B633u, 0x3F0167CDu],
            [0xBE851EBAu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27F4u, 0x3EF7C48Fu],
            [0xBEC7AE13u, 0xBE151A54u, 0x3F7CBD38u, 0x3D835182u, 0x3F4F6903u],
            [0xBC23D698u, 0x3E9C2FD5u, 0x3F71B7F5u, 0x3DFE2FB0u, 0x3F6093C8u],
        };
        internal static readonly uint[][] SlopeCorners = new uint[][]
        {
            [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EFu],
            [0x3EBBCEDDu, 0x3F50ACF6u, 0x3EE58B0Eu],
            [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EBu],
            [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27E3u],
            [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27EFu],
            [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
            [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EFu],
            [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
            [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27F3u],
        };
        internal static readonly uint[][] Slope2m = new uint[][]
        {
            [0x3EF33332u, 0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C629Du, 0x3F0CECEDu],
            [0x3ECF5C2Du, 0xBBC5DD92u, 0x3F7FF9B0u, 0xBC4CC7BDu, 0x3F275423u],
            [0xBCA3D698u, 0x3DD703D2u, 0x3F7C8ADFu, 0x3E00BE6Fu, 0x3F0167CDu],
            [0xBE851EBAu, 0xBD7F7B4Au, 0x3F7F7B4Au, 0xBC4C62A4u, 0x3EF7C48Fu],
            [0xBEC7AE13u, 0xBD968C6Au, 0x3F7F2C38u, 0x3D04A9A4u, 0x3F4F6903u],
            [0xBC23D698u, 0x3E271FBBu, 0x3F7BEDA4u, 0x3D8FA9A2u, 0x3F6093C8u],
        };
        internal static readonly uint[][] Slope2mCorners = new uint[][]
        {
            [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C62A0u],
            [0x3E591A45u, 0x3F7139A3u, 0x3E84AC7Fu],
            [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C629Cu],
            [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C6294u],
            [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C62A0u],
            [0x3E60C386u, 0x3F79BCCEu, 0xBC47CA41u],
            [0xBD7F7B48u, 0x3F7F7B49u, 0xBC4C62A0u],
            [0x3E60C386u, 0x3F79BCCEu, 0xBC47CA41u],
            [0xBD7F7B49u, 0x3F7F7B49u, 0xBC4C62A4u],
        };
        internal static readonly uint[][] CutFullCorners = new uint[][]
        {
            [0x3EBBCEDDu, 0x3F50ACF6u, 0x3EE58B0Eu],
            [0xBDFDF1EBu, 0x3F7DF1ECu, 0xBCCB27EBu],
            [0xBDFDF1ECu, 0x3F7DF1ECu, 0xBCCB27EFu],
            [0x3ED20D58u, 0x3F69642Au, 0xBCBAB68Bu],
        };
        internal static readonly uint[][] Rejection = new uint[][]
        {
            [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
            [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
            [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
        };
        internal static readonly uint[][] ZeroRaster = new uint[][]
        {
            [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
            [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x00000000u],
            [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3EB45454u],
        };
        internal static readonly uint[][] Ignored = new uint[][]
        {
            [0x3EB33332u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F65E5E6u],
            [0xBD23D600u, 0xBDFDF1ECu, 0x3F7DF1EDu, 0xBCCB27EDu, 0x3F5EF894u],
            [0x3E199999u, 0x3ED20D58u, 0x3F69642Au, 0xBCBAB687u, 0x3F191919u],
        };
        internal static readonly uint[] AbsentInDomain = [0x3EB33333u, 0xBE1EE3C4u, 0x3F7CD209u, 0xBCCA41A3u, 0x3F191919u];
        internal static readonly uint[][] RegionEdge = new uint[][]
        {
            [0x3EB33333u, 0x3DD75E49u, 0x3F7E8044u, 0xBCCB99D1u, 0x3DA98350u],
            [0x3EB33333u, 0xBE1EE3C4u, 0x3F7CD209u, 0xBCCA41A3u, 0x3ECC2177u],
            [0x3CCCCCCCu, 0x3E2CDC95u, 0x3F7C4E2Au, 0xBC4ED8DBu, 0x00000000u],
            [0x3E000000u, 0x3E0B4B3Bu, 0x3F7D99B5u, 0xBC4BDFD3u, 0x00000000u],
            [0x3E999999u, 0xBDFACD1Au, 0x3F7ACD1Au, 0xBE229373u, 0x3D0026E8u],
        };
        internal static readonly uint[][] RegionEdgeCorners = new uint[][]
        {
            [0xBEA917A4u, 0x3F718F7Cu, 0xBCC13F96u],
            [0x3E9E3514u, 0x3F73655Bu, 0xBCC2B77Bu],
            [0x3E9E4087u, 0x3F7376F7u, 0x00000000u],
            [0x3DCBB878u, 0x3F7EA697u, 0xBCCBB87Cu],
            [0xBDFE05ECu, 0x3F7E05ECu, 0x00000000u],
            [0xBDFDF1ECu, 0x3F7DF1ECu, 0x3CCB27EFu],
        };
        internal static readonly Dictionary<string, uint[][]> Cuts = new(StringComparer.Ordinal)
        {
            ["cut-full-r0"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            },
            ["cut-full-r1"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            },
            ["cut-full-r2"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            },
            ["cut-full-r3"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            },
            ["cut-diagonalhalf-r0"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x3F086ED4u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x00000000u],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x3F03501Du],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x3F018182u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x00000000u],
            },
            ["cut-diagonalhalf-r1"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E3A006Fu, 0x3F719EE1u, 0x3E8D5331u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x3F086ED4u],
                [0x3EFFFFE6u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EFu, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E50B73Eu, 0x3F7A8C33u, 0xBCC87028u, 0x00000000u],
                [0x3E4CCCCDu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x3F018182u],
                [0x3EA51EB3u, 0xBDCE203Du, 0x3F7EB32Du, 0xBA976C60u, 0x3EFCF022u],
            },
            ["cut-diagonalhalf-r2"] = new uint[][]
            {
                [0x3E2E1480u, 0x3EC3F255u, 0x3F59B7ECu, 0x3EB8C7A6u, 0x00000000u],
                [0xBDCCCC64u, 0x3DAF4689u, 0x3F7E5A4Au, 0x3D97EEF8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DAF4688u, 0x3F7E5A4Au, 0x3D97EEF7u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3ED18C95u, 0x3F68D519u, 0x3D9519ABu, 0x00000000u],
                [0x3E4CCCCCu, 0x3ECCFE90u, 0x3F63C585u, 0x3E6083F1u, 0x00000000u],
                [0x3EA51EB3u, 0x3E8C8F92u, 0x3F720F6Du, 0x3E330AB7u, 0x3EFCF022u],
            },
            ["cut-diagonalhalf-r3"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E3A006Fu, 0x3F719EE1u, 0x3E8D5331u, 0x00000000u],
                [0xBDCCCC64u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27ECu, 0x00000000u],
                [0x3EFFFFE6u, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EFu, 0x00000000u],
                [0x3E6B851Au, 0x3E50B73Eu, 0x3F7A8C33u, 0xBCC87028u, 0x3F03501Du],
                [0x3E4CCCCDu, 0xBDFDF1EDu, 0x3F7DF1EDu, 0xBCCB27EEu, 0x00000000u],
                [0x3EA51EB3u, 0xBDCE203Du, 0x3F7EB32Du, 0xBA976C60u, 0x00000000u],
            },
            ["cut-cornerquarter-r0"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E374642u, 0x3F71F09Au, 0x3E8C0673u, 0x00000000u],
                [0xBDCCCC64u, 0x3DB1B761u, 0x3F7E3F3Cu, 0x3DA03199u, 0x3F086ED4u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x3F03501Du],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x3F018182u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x3EFCF022u],
            },
            ["cut-cornerquarter-r1"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x3EFF65CDu],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x3F086ED4u],
                [0x3EFFFFE6u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x00000000u],
                [0x3E6B851Au, 0x3E4FDCE1u, 0x3F7A9789u, 0xBCC8793Bu, 0x3F03501Du],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x3F018182u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x3EFCF022u],
            },
            ["cut-cornerquarter-r2"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x3EFF65CDu],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x3F086ED4u],
                [0x3EFFFFE7u, 0x3DB14ABBu, 0x3F7E5C12u, 0x3D94D10Au, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E4F0901u, 0x3F7AA27Eu, 0xBCC881FFu, 0x00000000u],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x3F018182u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x3EFCF022u],
            },
            ["cut-cornerquarter-r3"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E389A2Eu, 0x3F71C8F1u, 0x3E8CA87Bu, 0x3EFF65CDu],
                [0xBDCCCC65u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x00000000u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x3F03501Du],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x3F018182u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x3EFCF022u],
            },
            ["cut-cornerthreequarter-r0"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E374642u, 0x3F71F09Au, 0x3E8C0673u, 0x3EFF65CDu],
                [0xBDCCCC64u, 0x3DB1B761u, 0x3F7E3F3Cu, 0x3DA03199u, 0x00000000u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x00000000u],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x00000000u],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x00000000u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x00000000u],
            },
            ["cut-cornerthreequarter-r1"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x00000000u],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x00000000u],
                [0x3EFFFFE6u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x3EF5285Du],
                [0x3E6B851Au, 0x3E4FDCE1u, 0x3F7A9789u, 0xBCC8793Bu, 0x00000000u],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x00000000u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x00000000u],
            },
            ["cut-cornerthreequarter-r2"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E923005u, 0x3F675663u, 0x3EA36ABEu, 0x00000000u],
                [0xBDCCCC66u, 0xBCAB6EBFu, 0x3F7FE01Cu, 0x3CBD7AC8u, 0x00000000u],
                [0x3EFFFFE7u, 0x3DB14ABBu, 0x3F7E5C12u, 0x3D94D10Au, 0x00000000u],
                [0x3E6B851Au, 0x3E4F0901u, 0x3F7AA27Eu, 0xBCC881FFu, 0x3F03501Du],
                [0x3E4CCCCEu, 0x3E100068u, 0x3F7C3A03u, 0x3DC7791Au, 0x00000000u],
                [0x3EA51EB3u, 0x3E60F613u, 0x3F76EB1Eu, 0x3E15E57Bu, 0x00000000u],
            },
            ["cut-cornerthreequarter-r3"] = new uint[][]
            {
                [0x3E2E1480u, 0x3E389A2Eu, 0x3F71C8F1u, 0x3E8CA87Bu, 0x00000000u],
                [0xBDCCCC65u, 0x3DB3A537u, 0x3F7E41B3u, 0x3D9D08ACu, 0x3F086ED4u],
                [0x3EFFFFE7u, 0xBCA5E352u, 0x3F7FDC22u, 0x3CD648A2u, 0x00000000u],
                [0x3E6B851Au, 0x3E9D6663u, 0x3F73806Du, 0x3CE093D5u, 0x00000000u],
                [0x3E4CCCCDu, 0x3E0F7E6Bu, 0x3F7C128Cu, 0x3DD4F3DEu, 0x00000000u],
                [0x3EA51EB3u, 0x3D725819u, 0x3F7EC56Bu, 0x3D9FA5EAu, 0x00000000u],
            },
        };
    }
}
