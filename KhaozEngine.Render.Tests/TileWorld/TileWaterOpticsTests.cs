using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The TileWorld triangle producer over actual completed mesh snapshots. Every expectation comes from
/// the independent mesh-intersection oracle, never from the producer.</summary>
public class TileWaterOpticsTests
{
    static TileWorldDocument LoadedPatch(RegionCoord center = default, float tileSize = 1f)
    {
        var doc = new TileWorldDocument { Id = "optics-patch", DisplayName = "Optics patch", PlaneCount = 1, TileSize = tileSize };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
                doc.GetOrCreateRegion(center.Offset(x, z));
        return doc;
    }

    static void Grass(TileWorldDocument doc, RegionCoord region, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(region.OriginX + x, region.OriginZ + z, 0, TileRenderTestData.Grass);
    }

    static TileGroundSurfaceSnapshot Surface(TileWorldDocument doc, RegionCoord region, TileGroundLod lod = TileGroundLod.Full) =>
        TileGroundCpuBuild.Build(doc, TileRenderTestData.Catalogs, region, 0, lod, new TileGroundMesherOptions(),
            captureOpticalSurface: true).Surface!;

    static WaterPlane Plane(TileWorldDocument doc, int x, int z, int width, int height, float surfaceY) =>
        TileWaterPlanes.ToPlane(new TileRect(x, z, width, height), surfaceY, doc.TileSize);

    static TileWaterTriangleOutcome Prepare(TileWorldDocument doc, RegionCoord region, float padding,
        IReadOnlyDictionary<RegionCoord, TileGroundSurfaceSnapshot> sources, params WaterPlane[] planes) =>
        TileWaterTrianglePreparation.Prepare(new TileWaterPreparation(planes, new TileWaterSourceStamp(doc, [], null),
            padding, null), region, 0, r => sources.GetValueOrDefault(r),
            new TileWaterSourceContext(doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions(),
                new TileWaterDependencyRegistry()));

    static TileWaterTriangleBuild Single(TileWaterTriangleOutcome outcome)
    {
        Assert.Null(outcome.Failure);
        return Assert.Single(outcome.Fields);
    }

    // Physical depth as the shader forms it, without the clamp: rest depth plus level minus the datum.
    static float Sample(TileWaterTriangleBuild build, float worldX, float worldZ) =>
        build.Field.SampleDepth((worldX - build.Origin.X) / build.Field.Size.X,
            (worldZ - build.Origin.Z) / build.Field.Size.Y) + build.Level - build.Origin.Y;

    static double Oracle(TileGroundSurfaceSnapshot surface, float reference, float worldX, float worldZ) =>
        reference - TileWaterBedOracle.Height([surface], worldX, worldZ);

    [Fact]
    public void FieldMatchesTheActualMeshInsteadOfBilinearHeightAt()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 8, 8, 13, 13);
        // A non-planar saddle. Bilinear HeightAt gives 1 m at its center, while the mesh joins one diagonal.
        doc.SetCornerHeightCm(10, 10, 0, 0);
        doc.SetCornerHeightCm(11, 10, 0, 200);
        doc.SetCornerHeightCm(10, 11, 0, 200);
        doc.SetCornerHeightCm(11, 11, 0, 0);
        TileGroundSurfaceSnapshot surface = Surface(doc, default);
        TileWaterTriangleBuild build = Single(Prepare(doc, default, 0f, new Dictionary<RegionCoord, TileGroundSurfaceSnapshot>
        {
            [default] = surface,
        }, Plane(doc, 10, 10, 1, 1, 0.75f)));

        Assert.Equal(1f, doc.HeightAt(10.5f, -10.5f, 0), 5);
        foreach ((float x, float z) in new[] { (10.5f, -10.5f), (10.25f, -10.75f), (10f, -10f), (11f, -10f), (10.9f, -10.2f) })
            Assert.Equal(Oracle(surface, 0.75f, x, z), Sample(build, x, z), 5);
        Assert.True(Math.Abs(Sample(build, 10.5f, -10.5f) - (0.75f - 1f)) > 0.5f);
        Assert.All(build.Field.CertifiedRectangles.ToArray(), r => Assert.True(r.Minimum.X >= 0f && r.Maximum.X <= build.Field.Size.X));
    }

    [Fact]
    public void NegativeWorldZUsesTheCorrectRows()
    {
        var region = new RegionCoord(-1, 2);
        TileWorldDocument doc = LoadedPatch(region);
        Grass(doc, region, 4, 4, 20, 20);
        doc.SetCornerHeightCm(-54, 138, 0, 300);
        doc.SetCornerHeightCm(-53, 139, 0, 100);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f, new Dictionary<RegionCoord, TileGroundSurfaceSnapshot>
        {
            [region] = surface,
        }, Plane(doc, -56, 136, 5, 5, 10f)));

        Assert.True(build.Origin.Z < -136f);
        foreach ((float x, float z) in new[] { (-54f, -138f), (-53.5f, -138.5f), (-53f, -139f), (-55.2f, -140.7f) })
            Assert.Equal(Oracle(surface, 10f, x, z), Sample(build, x, z), 4);
    }

    [Fact]
    public void PaddingExpandsCertificatesOutwardWithoutMovingTriangles()
    {
        TileWorldDocument doc = LoadedPatch(tileSize: 2f);
        Grass(doc, default, 4, 4, 20, 20);
        TileGroundSurfaceSnapshot surface = Surface(doc, default);
        WaterPlane plane = Plane(doc, 10, 10, 2, 3, 6f);
        TileWaterTriangleBuild build = Single(Prepare(doc, default, 0.25f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = surface }, plane));

        WaterOpticsRectangle certificate = Assert.Single(build.Field.CertifiedRectangles.ToArray());
        double reach = 0.25 + 0.0001;
        Assert.InRange((double)certificate.Minimum.X + build.Origin.X, plane.CenterX - plane.HalfExtentX - reach - 1e-4,
            plane.CenterX - plane.HalfExtentX - reach);
        Assert.InRange((double)certificate.Maximum.Y + build.Origin.Z, plane.CenterZ + plane.HalfExtentZ + reach,
            plane.CenterZ + plane.HalfExtentZ + reach + 1e-4);
        // Complete supporting triangles reach whole tiles beyond the certificate, unclipped.
        Assert.Equal(0f, (build.Origin.X / doc.TileSize) % 1f);
        Assert.Equal(6f, Sample(build, 21f, -21f), 5);
    }

    [Fact]
    public void SameLevelRowRunsShareOneBuildDescription()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 4, 4, 24, 24);
        for (int z = 10; z < 16; z++)
            for (int x = 10; x < (z < 12 ? 16 : 12); x++)
                doc.SetUnderlay(x, z, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 13, 0, -80);
        var planes = TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, default, 0);
        Assert.Equal(2, planes.Count);
        TileGroundSurfaceSnapshot surface = Surface(doc, default);

        TileWaterTriangleBuild build = Single(Prepare(doc, default, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = surface }, planes.ToArray()));

        Assert.Equal(2, build.Field.CertifiedRectangles.Length);
        foreach (WaterPlane plane in planes)
        {
            Assert.Equal(-0.02f, plane.SurfaceY);
            Assert.Equal(Oracle(surface, -0.02f, plane.CenterX, plane.CenterZ), Sample(build, plane.CenterX, plane.CenterZ), 5);
        }
        Assert.Equal(Oracle(surface, -0.02f, 11f, -13f), Sample(build, 11f, -13f), 5);
    }

    [Fact]
    public void DistinctLevelsHaveDistinctReferenceMappings()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 4, 4, 20, 20);
        doc.SetCornerHeightCm(10, 10, 0, 250);
        TileGroundSurfaceSnapshot surface = Surface(doc, default);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = surface },
            Plane(doc, 9, 9, 2, 2, 2f), Plane(doc, 12, 12, 2, 2, 30f));

        Assert.Null(outcome.Failure);
        Assert.Equal(2, outcome.Fields.Length);
        TileWaterTriangleBuild low = outcome.Fields[0], high = outcome.Fields[1];
        Assert.Equal(2f, low.Level);
        Assert.Equal(30f, high.Level);
        Assert.Equal(0f, low.Origin.Y);
        Assert.Equal(0f, high.Origin.Y);
        Assert.Equal(-0.5f, Sample(low, 10f, -10f), 4);
        Assert.Equal(Oracle(surface, 30f, 13f, -13f), Sample(high, 13f, -13f), 4);
        // Immutable: a later document edit cannot change prepared bytes.
        doc.SetCornerHeightCm(10, 10, 0, 1000);
        Assert.Equal(-0.5f, Sample(low, 10f, -10f), 4);
    }

    [Fact]
    public void MissingHaloRefusesTheWholeBuild()
    {
        using var tmp = new TempDir();
        TileWorldDocument authored = LoadedPatch();
        Grass(authored, default, 56, 8, 64, 16);
        TileWorldFile.Save(authored, tmp.Path);
        TileWorldSource source = TileWorldSource.Open(tmp.Path);
        source.EnsureLoaded(new RegionCoord(0, 0));
        // East is known and unloaded. The mesh used its strict-read fallback at x = 64.
        TileWorldDocument doc = source.Document;
        TileGroundSurfaceSnapshot surface = Surface(doc, default);

        TileWaterTriangleOutcome outcome = Prepare(doc, default, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = surface }, Plane(doc, 62, 10, 1, 2, 8f));

        TileWaterOpticsFailure failure = outcome.Failure!.Value;
        Assert.Empty(outcome.Fields);
        Assert.Equal(TileWaterOpticsFailureKind.MissingHeightData, failure.Kind);
        Assert.Equal(new RegionCoord(1, 0), failure.MissingRegion);
        Assert.Equal(default, failure.Region);
        Assert.Equal(8f, failure.ReferenceSurfaceY);
        Assert.Contains(new TileWaterOpticsDependency(new RegionCoord(1, 0), null), outcome.Availability!.Entries.ToArray().Select(e => e.Dependency));

        // The same unavailable owner is unrelated to an interior body of that region.
        Assert.Null(Prepare(doc, default, 0f, new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = surface },
            Plane(doc, 58, 10, 2, 2, 8f)).Failure);
    }

    [Theory]
    [InlineData(1100f)]
    [InlineData(float.MaxValue)]
    public void OversizedFieldIsRefusedWithoutDownsampling(float padding)
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 4, 4, 20, 20);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, padding,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = Surface(doc, default) },
            Plane(doc, 10, 10, 2, 2, 3f));

        Assert.Empty(outcome.Fields);
        Assert.Equal(TileWaterOpticsFailureKind.FieldTooLarge, outcome.Failure!.Value.Kind);
        Assert.Null(outcome.Failure!.Value.MissingRegion);
    }

    [Fact]
    public void MissingNeighbourSnapshotIsRefusedWithoutAManufacturedHalo()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 56, 8, 64, 16);
        Grass(doc, new RegionCoord(1, 0), 0, 8, 8, 16);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, 1f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = Surface(doc, default) },
            Plane(doc, 62, 10, 2, 2, 3f));
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, outcome.Failure!.Value.Kind);
        Assert.Equal(new RegionCoord(1, 0), outcome.Failure!.Value.MissingRegion);
        Assert.Contains((new RegionCoord(1, 0), 0), outcome.SourceKeys);
    }

    [Theory]
    [InlineData(4096, 0, 0.1f)]
    [InlineData(0, -4096, 0.3f)]
    [InlineData(1000, 1000, 1f)]
    [InlineData(-3000, 20, 2.54f)]
    public void DistantFieldsConvertThroughTheExactUploadedTransform(int rx, int rz, float tileSize)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = LoadedPatch(region, tileSize);
        Grass(doc, region, 4, 4, 20, 20);
        int x = region.OriginX + 10, z = region.OriginZ + 10;
        doc.SetCornerHeightCm(x + 1, z + 1, 0, 150);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [region] = surface }, Plane(doc, x, z, 2, 2, 5f)));

        float worldX = TileWorldSpace.WorldX(x + 0.75f, tileSize), worldZ = TileWorldSpace.WorldZ(z + 0.25f, tileSize);
        Assert.Equal(Oracle(surface, 5f, worldX, worldZ), Sample(build, worldX, worldZ), 3);
    }

    [Fact]
    public void WholeMetreOriginReducesExactlyByAnyRenderAnchor()
    {
        // About world (-515, y, 304) with a 0.3 m lattice. A +1024 m anchor grows both coordinates.
        var region = new RegionCoord(-27, -16);
        TileWorldDocument doc = LoadedPatch(region, 0.3f);
        Grass(doc, region, 4, 4, 20, 20);
        int x = region.OriginX + 10, z = region.OriginZ + 10;
        doc.SetCornerHeightCm(x + 1, z + 1, 0, 40);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [region] = surface }, Plane(doc, x, z, 2, 2, 5f)));

        Assert.InRange(build.Origin.X, -520f, -510f);
        Assert.InRange(build.Origin.Z, 300f, 310f);
        Assert.Equal(Math.Floor(build.Origin.X), build.Origin.X);
        Assert.Equal(Math.Floor(build.Origin.Z), build.Origin.Z);
        var anchor = new Vector3(1024f, 0f, 1024f);
        Vector3 reduced = build.Origin - anchor;
        Assert.Equal((double)build.Origin.X - anchor.X, reduced.X);
        Assert.Equal((double)build.Origin.Z - anchor.Z, reduced.Z);
        Assert.Equal(build.Origin.Y, reduced.Y);
        float worldX = TileWorldSpace.WorldX(x + 0.75f, doc.TileSize), worldZ = TileWorldSpace.WorldZ(z + 0.25f, doc.TileSize);
        Assert.Equal(Oracle(surface, 5f, worldX, worldZ), Sample(build, worldX, worldZ), 4);
    }

    [Theory]
    [InlineData(31, 0)]
    [InlineData(0, -32)]
    [InlineData(1563, 0)]
    [InlineData(-1563, 1563)]
    public void OrdinaryMetreTileWaterPublishesFarFromTheWorldOrigin(int rx, int rz)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = LoadedPatch(region);
        Grass(doc, region, 4, 4, 20, 20);
        int x = region.OriginX + 10, z = region.OriginZ + 10;
        doc.SetCornerHeightCm(x + 1, z + 1, 0, 120);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [region] = surface }, Plane(doc, x, z, 2, 2, 5f)));

        Assert.True(Math.Max(Math.Abs(build.Origin.X), Math.Abs(build.Origin.Z)) > 1900f);
        Assert.Equal(Math.Floor(build.Origin.X), build.Origin.X);
        float worldX = TileWorldSpace.WorldX(x + 0.75f, 1f), worldZ = TileWorldSpace.WorldZ(z + 0.25f, 1f);
        Assert.Equal(Oracle(surface, 5f, worldX, worldZ), Sample(build, worldX, worldZ), 3);
    }

    [Theory]
    [InlineData(131072, 0, 1)]
    [InlineData(131072, 0, 10)]
    [InlineData(-131073, 0, 10)]
    [InlineData(0, -131073, 10)]
    public void OriginsAtOrBeyondTwoToTheTwentyThreeMetresAreRefused(int rx, int rz, int offset)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = LoadedPatch(region);
        Grass(doc, region, 0, 0, 24, 24);
        // Every neighbour has a present snapshot, so only the origin rule can refuse.
        var sources = new Dictionary<RegionCoord, TileGroundSurfaceSnapshot>();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++) sources[region.Offset(dx, dz)] = Surface(doc, region.Offset(dx, dz));
        TileWaterTriangleOutcome outcome = Prepare(doc, region, 0f, sources,
            Plane(doc, region.OriginX + offset, region.OriginZ + offset, 2, 2, 5f));

        Assert.Empty(outcome.Fields);
        Assert.Equal(TileWaterOpticsFailureKind.UnrepresentableSurface, outcome.Failure!.Value.Kind);
        Assert.True(TileWaterTrianglePreparation.MaxOriginMetres == 8388608d);
    }

    [Fact]
    public void OriginJustBelowTwoToTheTwentyThreeMetresPublishes()
    {
        var region = new RegionCoord(131071, 0);
        TileWorldDocument doc = LoadedPatch(region);
        Grass(doc, region, 0, 0, 24, 24);
        var sources = new Dictionary<RegionCoord, TileGroundSurfaceSnapshot>();
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++) sources[region.Offset(dx, dz)] = Surface(doc, region.Offset(dx, dz));
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f, sources,
            Plane(doc, region.OriginX + 10, region.OriginZ + 10, 2, 2, 5f)));

        Assert.InRange(build.Origin.X, 8388000f, 8388607f);
        Assert.Equal(Math.Floor(build.Origin.X), build.Origin.X);
    }

    // Local points the field evaluates exactly: u * Size reproduces the binary32 local coordinate the field
    // samples, so the binary64 oracle is compared at the same world point against the whole positional budget.
    [Theory]
    [InlineData(0, 0, 1f)]
    [InlineData(-1, 1, 0.1f)]
    [InlineData(2, -2, 0.3f)]
    [InlineData(-3000, 20, 2.54f)]
    [InlineData(1000, 1000, 1f)]
    [InlineData(131071, 0, 1f)]
    [InlineData(-131072, 131071, 1f)]
    public void PublishedFieldStaysWithinTheBudgetOfTheIndependentOracle(int rx, int rz, float tileSize)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = LoadedPatch(region, tileSize);
        Grass(doc, region, 4, 4, 20, 20);
        int x = region.OriginX + 10, z = region.OriginZ + 10;
        doc.SetCornerHeightCm(x + 1, z + 1, 0, 40);
        doc.SetCornerHeightCm(x, z + 2, 0, -25);
        doc.SetCornerHeightCm(x + 2, z, 0, 15);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [region] = surface }, Plane(doc, x, z, 2, 2, 5f)));

        double budget = Math.Min(0.0001, tileSize / 1024.0);
        Assert.True(build.Field.ReconstructionErrorBoundMetres <= budget);
        Vector2 size = build.Field.Size;
        foreach (double fx in new[] { 0.05, 0.37, 0.5, 0.83, 1.0, 1.29, 1.62, 1.95 })
            foreach (double fz in new[] { 0.05, 0.41, 0.77, 1.0, 1.18, 1.5, 1.93 })
            {
                // World X is tile X times size and world Z is minus tile Z times size.
                float u = (float)((x + fx) * tileSize - build.Origin.X) / size.X;
                float v = (float)(-(z + fz) * tileSize - build.Origin.Z) / size.Y;
                float localX = u * size.X, localZ = v * size.Y;
                Assert.True(build.Field.TrySampleDepth(u, v, out float rest));
                double depth = (double)rest + build.Level - build.Origin.Y;
                double worldX = (double)build.Origin.X + localX, worldZ = (double)build.Origin.Z + localZ;
                double oracle = 5d - TileWaterBedOracle.Height([surface], worldX, worldZ);
                Assert.True(Math.Abs(depth - oracle) <= budget,
                    $"({worldX}, {worldZ}) depth {depth} oracle {oracle} budget {budget}");
            }
    }

    [Fact]
    public void PartialTriangleRangeIsRefusedRatherThanTrimmed()
    {
        var tiles = new TileGroundSurfaceExtent(0, 0, 1, 1);
        Assert.Equal(18, TileWaterTrianglePreparation.TriangleEnd(new TileGroundSurfaceRange(tiles, 12, 6, 0, 0, 0, true)));
        Assert.Equal(12, TileWaterTrianglePreparation.TriangleEnd(new TileGroundSurfaceRange(tiles, 12, 0, 0, 0, 0, true)));
        Assert.Throws<InvalidOperationException>(() =>
            TileWaterTrianglePreparation.TriangleEnd(new TileGroundSurfaceRange(tiles, 12, 7, 0, 0, 0, true)));
        Assert.Throws<InvalidOperationException>(() =>
            TileWaterTrianglePreparation.TriangleEnd(new TileGroundSurfaceRange(tiles, 0, 2, 0, 0, 0, true)));
    }

    [Fact]
    public void UnconditionedSteepBedsAreRefusedAsUnrepresentable()
    {
        TileWorldDocument doc = LoadedPatch(tileSize: 0.01f);
        Grass(doc, default, 4, 4, 20, 20);
        doc.SetCornerHeightCm(11, 11, 0, 32000);
        TileWaterTriangleOutcome outcome = Prepare(doc, default, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = Surface(doc, default) },
            Plane(doc, 10, 10, 2, 2, 5f));

        Assert.Empty(outcome.Fields);
        Assert.Equal(TileWaterOpticsFailureKind.UnrepresentableSurface, outcome.Failure!.Value.Kind);
    }

    [Theory]
    [InlineData(0, 0, 0.1f)]
    [InlineData(2, -2, 0.1f)]
    [InlineData(-1, 1, 0.3f)]
    [InlineData(2, -2, 1.1f)]
    [InlineData(0, 0, 2.54f)]
    [InlineData(40, -40, 0.1f)]
    [InlineData(-4, 4, 2.54f)]
    [InlineData(100, -100, 1f)]
    [InlineData(4, 0, 30f)]
    [InlineData(-2, 1, 100f)]
    public void OrdinaryTileSizesAndDistancesRemainAvailable(int rx, int rz, float tileSize)
    {
        var region = new RegionCoord(rx, rz);
        TileWorldDocument doc = LoadedPatch(region, tileSize);
        Grass(doc, region, 4, 4, 20, 20);
        int x = region.OriginX + 10, z = region.OriginZ + 10;
        doc.SetCornerHeightCm(x, z, 0, 100);
        doc.SetCornerHeightCm(x + 1, z, 0, 101);
        doc.SetCornerHeightCm(x, z + 1, 0, 100);
        doc.SetCornerHeightCm(x + 1, z + 1, 0, 101);
        TileGroundSurfaceSnapshot surface = Surface(doc, region);
        TileWaterTriangleBuild build = Single(Prepare(doc, region, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [region] = surface }, Plane(doc, x, z, 2, 2, 5f)));

        float worldX = TileWorldSpace.WorldX(x + 0.5f, tileSize);
        float worldZ = TileWorldSpace.WorldZ(z + 0.5f, tileSize);
        Assert.Equal(Oracle(surface, 5f, worldX, worldZ), Sample(build, worldX, worldZ), 4);
        Assert.True(build.Field.ReconstructionErrorBoundMetres < Math.Min(0.0001, tileSize / 1024.0));
    }

    [Fact]
    public void CoarseInteriorsAndTransitionsUseTheirActualTriangles()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 0, 0, 64, 64);
        doc.SetCornerHeightCm(13, 13, 0, 120);
        doc.SetCornerHeightCm(9, 14, 0, -70);
        TileGroundSurfaceSnapshot coarse = Surface(doc, default, TileGroundLod.Coarse4);
        TileWaterTriangleBuild build = Single(Prepare(doc, default, 0f,
            new Dictionary<RegionCoord, TileGroundSurfaceSnapshot> { [default] = coarse }, Plane(doc, 8, 8, 8, 8, 1f)));
        foreach ((float x, float z) in new[] { (13f, -13f), (9.5f, -14.5f), (12.25f, -9.75f), (15.9f, -15.9f) })
            Assert.Equal(Oracle(coarse, 1f, x, z), Sample(build, x, z), 4);
    }

    [Fact]
    public void AdjacentFieldsAgreeOnTheirSharedBoundary()
    {
        TileWorldDocument doc = LoadedPatch();
        Grass(doc, default, 56, 4, 64, 20);
        Grass(doc, new RegionCoord(1, 0), 0, 4, 8, 20);
        doc.SetCornerHeightCm(64, 10, 0, 100);
        doc.SetCornerHeightCm(64, 11, 0, 300);
        var sources = new Dictionary<RegionCoord, TileGroundSurfaceSnapshot>
        {
            [default] = Surface(doc, default), [new RegionCoord(1, 0)] = Surface(doc, new RegionCoord(1, 0)),
        };
        TileWaterTriangleBuild west = Single(Prepare(doc, default, 1f, sources, Plane(doc, 61, 9, 3, 3, 5f)));
        TileWaterTriangleBuild east = Single(Prepare(doc, new RegionCoord(1, 0), 1f, sources, Plane(doc, 64, 9, 3, 3, 5f)));

        double expected = 5f - TileWaterBedOracle.Height(sources.Values, 64f, -10.25f);
        Assert.Equal(expected, Sample(west, 64f, -10.25f), 4);
        Assert.Equal(Sample(west, 64f, -10.25f), Sample(east, 64f, -10.25f), 5);
    }

    [Fact]
    public void ProjectedAreaIsDecidedExactly()
    {
        Assert.False(TileWaterTrianglePreparation.HasProjectedArea(new Vector3(0, 0, 0), new Vector3(1, 5, 0), new Vector3(2, -3, 0)));
        Assert.True(TileWaterTrianglePreparation.HasProjectedArea(new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1)));
        float tiny = MathF.BitIncrement(1f) - 1f;
        Assert.True(TileWaterTrianglePreparation.HasProjectedArea(new Vector3(1e6f, 0, 1e6f),
            new Vector3(1e6f + 64f, 0, 1e6f), new Vector3(1e6f, 0, 1e6f + tiny * 1e6f)));
    }
}
