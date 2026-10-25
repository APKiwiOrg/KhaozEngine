using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Rest depth is <c>-y</c> against the exact datum 0, so the certified reconstruction error scales with
/// absolute vertex height instead of depth. Ordinary sloped and steep fixtures raised close to the <c>short</c>
/// centimetre height limit still publish, and sampled depth stays within the producer's certified reconstruction
/// bound plus its conversion charge of the binary64 drawn mesh, also at off-grid points.</summary>
public sealed class TileWaterOpticsAltitudeTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(11f, 0f, -11f);
    // Covers the binary64 evaluation of the drawn-mesh oracle at heights near 300 m, far below any certified bound.
    const double OracleSlack = 1e-9;
    readonly ITestOutputHelper _output;

    public TileWaterOpticsAltitudeTests(ITestOutputHelper output) => _output = output;

    public enum Fixture { SlopedCornerCuts, SteepBank }

    // A one-tile pool whose drawn diagonal runs between two corners two metres above the other two, so each bank
    // rises two metres per metre of tile.
    static TileWorldDocument SteepBank()
    {
        var doc = new TileWorldDocument { Id = "steep-bank", DisplayName = "Steep bank", PlaneCount = 1 };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        for (int z = 8; z < 13; z++)
            for (int x = 8; x < 13; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 10, 0, 200);
        doc.SetCornerHeightCm(10, 11, 0, 200);
        return doc;
    }

    // Every corner of the loaded three by three patch moves by the same offset, so no cliff enters any range.
    static TileWorldDocument Raised(Fixture fixture, short offsetCm)
    {
        TileWorldDocument doc = fixture == Fixture.SteepBank ? SteepBank() : TileWaterOpticsDatumTests.SlopedCornerCuts();
        for (int rz = -1; rz <= 1; rz++)
            for (int rx = -1; rx <= 1; rx++)
            {
                var region = new RegionCoord(rx, rz);
                for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
                    for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                        doc.SetCornerHeightCm(x, z, 0, checked((short)(doc.CornerHeightCm(x, z, 0) + offsetCm)));
            }
        return doc;
    }

    [Theory]
    [InlineData(Fixture.SlopedCornerCuts, (short)0)]
    [InlineData(Fixture.SlopedCornerCuts, (short)30000)]
    [InlineData(Fixture.SlopedCornerCuts, (short)-30000)]
    [InlineData(Fixture.SteepBank, (short)0)]
    [InlineData(Fixture.SteepBank, (short)30000)]
    [InlineData(Fixture.SteepBank, (short)-30000)]
    public void RaisedFixturePublishesWithinTheBudgetOfTheDrawnMesh(Fixture fixture, short offsetCm)
    {
        TileWorldDocument doc = Raised(fixture, offsetCm);
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed });
        view.LoadRegion(Origin);
        view.Draw(Focus);
        view.Flush();

        Assert.True(view.WaterOpticsFailures.Count == 0, string.Join("; ", view.WaterOpticsFailures.Select(f =>
            $"{f.Kind} at level {f.ReferenceSurfaceY:R}")));
        scene.ClearWater();
        view.DrawWaterPlanes();
        WaterPlane[] planes = scene.WaterDraws.ToArray();
        Assert.NotEmpty(planes);
        double budget = Math.Min(0.0001, doc.TileSize / 1024.0);
        TileGroundSurfaceSnapshot surface = view.AcceptedGroundSurface(Origin, 0)!;
        // The producer's own build over the same accepted surface carries the conversion charge it certified.
        TileWaterTriangleOutcome prepared = TileWaterTrianglePreparation.Prepare(new TileWaterPreparation(
                TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0).ToArray(),
                new TileWaterSourceStamp(doc, [], null), scene.OpticalPadding, null), Origin, 0,
            r => view.AcceptedGroundSurface(r, 0),
            new TileWaterSourceContext(doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions(), null));
        Assert.Null(prepared.Failure);
        double worstGrid = 0, worstOffGrid = 0, worstBound = 0, worstConversion = 0;
        int grid = 0, offGrid = 0;
        foreach (WaterPlane plane in planes)
        {
            Assert.True(plane.Optics.IsBound);
            Assert.Equal(0f, plane.Optics.Origin.Y);
            Assert.InRange(plane.SurfaceY, offsetCm / 100f - 2f, offsetCm / 100f + 3f);
            WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
            TileWaterTriangleBuild build = Assert.Single(prepared.Fields, b => b.Level == plane.SurfaceY);
            Assert.Equal(build.Origin, plane.Optics.Origin);
            Assert.Equal(build.Field.Size, field.Size);
            Assert.Equal(build.Field.TriangleCount, field.TriangleCount);
            Assert.Equal(build.Field.ReconstructionErrorBoundMetres, field.ReconstructionErrorBoundMetres);
            double conversion = build.ConversionErrorBoundMetres;
            double certified = field.ReconstructionErrorBoundMetres + conversion;
            Assert.True(certified <= budget, $"reconstruction {field.ReconstructionErrorBoundMetres:R} conversion " +
                $"{conversion:R} budget {budget:R}");
            double tolerance = certified + OracleSlack;
            worstBound = Math.Max(worstBound, field.ReconstructionErrorBoundMetres);
            worstConversion = Math.Max(worstConversion, conversion);
            _output.WriteLine($"{fixture} {offsetCm / 100.0:F2} m level {plane.SurfaceY:R} size {field.Size} " +
                $"triangles {field.TriangleCount} reconstruction {field.ReconstructionErrorBoundMetres:E3} " +
                $"producer conversion {conversion:E3} certified {certified:E3} budget {budget:E3} " +
                $"headroom {budget - certified:E3}");
            double Error(float x, float z, string kind)
            {
                float rest = field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
                    (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y);
                // The shader formula without its clamp: rest depth plus water level minus the binding reference.
                double depth = (double)rest + plane.SurfaceY - plane.Optics.Origin.Y;
                double oracle = plane.SurfaceY - TileWaterBedOracle.Height([surface], x, z);
                double error = Math.Abs(depth - oracle);
                Assert.True(error <= tolerance, $"{kind} ({x:R}, {z:R}) depth {depth:R} oracle {oracle:R} " +
                    $"error {error:R} tolerance {tolerance:R}");
                return error;
            }
            for (int i = 0; i <= 16; i++)
                for (int j = 0; j <= 16; j++)
                {
                    float x = plane.CenterX - plane.HalfExtentX + i * (2f * plane.HalfExtentX / 16f);
                    float z = plane.CenterZ - plane.HalfExtentZ + j * (2f * plane.HalfExtentZ / 16f);
                    worstGrid = Math.Max(worstGrid, Error(x, z, "grid"));
                    grid++;
                }
            foreach ((float x, float z, string kind) in OffGridSamples(plane, surface))
            {
                worstOffGrid = Math.Max(worstOffGrid, Error(x, z, kind));
                offGrid++;
            }
        }
        Assert.True(offGrid > grid, $"off-grid samples {offGrid} grid samples {grid}");
        _output.WriteLine($"{fixture} {offsetCm / 100.0:F2} m planes {planes.Length} grid samples {grid} worst " +
            $"{worstGrid:E3} off-grid samples {offGrid} worst {worstOffGrid:E3} worst reconstruction {worstBound:E3} " +
            $"worst producer conversion {worstConversion:E3}");
    }

    // Points that are not binary32 lattice values of the field: an R2 low-discrepancy set over the plane, and per
    // drawn triangle its centroid, an irrational interior point, points just inside each vertex and just inside
    // each edge. Only points inside the plane's extent are kept.
    static IEnumerable<(float X, float Z, string Kind)> OffGridSamples(WaterPlane plane, TileGroundSurfaceSnapshot surface)
    {
        double minX = plane.CenterX - plane.HalfExtentX, maxX = plane.CenterX + plane.HalfExtentX;
        double minZ = plane.CenterZ - plane.HalfExtentZ, maxZ = plane.CenterZ + plane.HalfExtentZ;
        var points = new List<(float X, float Z, string Kind)>();
        void Add(double x, double z, string kind)
        {
            float fx = (float)x, fz = (float)z;
            if (fx >= minX && fx <= maxX && fz >= minZ && fz <= maxZ) points.Add((fx, fz, kind));
        }
        const double A1 = 0.7548776662466927, A2 = 0.5698402909980532;
        for (int n = 1; n <= 256; n++)
        {
            double u = (0.5 + n * A1) % 1d, v = (0.5 + n * A2) % 1d;
            Add(minX + u * (maxX - minX), minZ + v * (maxZ - minZ), "irrational");
        }
        Vector3[] positions = surface.Payload.Positions.ToArray();
        uint[] indices = surface.Payload.Indices.ToArray();
        Matrix4x4 m = surface.WorldTransform;
        double[] nearVertex = [1e-3 / Math.PI, Math.Sqrt(2) * 1e-6];
        double[] nearEdge = [1e-4 / Math.E, Math.Sqrt(5) * 1e-7];
        var corner = new (double X, double Z)[3];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = positions[indices[t + k]];
                corner[k] = ((double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31 + m.M41,
                    (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33 + m.M43);
            }
            double gx = (corner[0].X + corner[1].X + corner[2].X) / 3, gz = (corner[0].Z + corner[1].Z + corner[2].Z) / 3;
            Add(gx, gz, "centroid");
            const double W0 = 0.6180339887498949, W1 = 0.2360679774997897;
            Add(W0 * corner[0].X + W1 * corner[1].X + (1 - W0 - W1) * corner[2].X,
                W0 * corner[0].Z + W1 * corner[1].Z + (1 - W0 - W1) * corner[2].Z, "interior");
            for (int k = 0; k < 3; k++)
            {
                (double ax, double az) = corner[k];
                (double bx, double bz) = corner[(k + 1) % 3];
                (double cx, double cz) = corner[(k + 2) % 3];
                foreach (double s in nearVertex) Add(ax + s * (gx - ax), az + s * (gz - az), "near vertex");
                double ex = ax + (bx - ax) / Math.Sqrt(3), ez = az + (bz - az) / Math.Sqrt(3);
                foreach (double s in nearEdge) Add(ex + s * (cx - ex), ez + s * (cz - ez), "near edge");
            }
        }
        return points;
    }
}
