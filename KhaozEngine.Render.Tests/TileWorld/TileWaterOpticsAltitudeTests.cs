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
/// centimetre height limit still publish within the positional budget of the drawn mesh.</summary>
public sealed class TileWaterOpticsAltitudeTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(11f, 0f, -11f);
    const double Binary64Slack = 1d / (1L << 50);
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
        double worstError = 0, worstBound = 0, worstConversion = 0;
        int sampled = 0;
        foreach (WaterPlane plane in planes)
        {
            Assert.True(plane.Optics.IsBound);
            Assert.Equal(0f, plane.Optics.Origin.Y);
            WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
            Conversion terms = ConversionTerms(surface, plane.Optics.Origin, field.Size);
            Assert.True(terms.Total + field.ReconstructionErrorBoundMetres <= budget);
            worstBound = Math.Max(worstBound, field.ReconstructionErrorBoundMetres);
            worstConversion = Math.Max(worstConversion, terms.Total);
            _output.WriteLine($"{fixture} {offsetCm / 100.0:F2} m level {plane.SurfaceY:R} size {field.Size} " +
                $"triangles {field.TriangleCount} reconstruction {field.ReconstructionErrorBoundMetres:E3} " +
                $"conversion depth {terms.Depth:E3} horizontal {terms.Horizontal:E3} slope {terms.Slope:R} " +
                $"total {terms.Total:E3} budget {budget:E3} headroom {budget - terms.Total - field.ReconstructionErrorBoundMetres:E3}");
            for (int i = 0; i <= 16; i++)
                for (int j = 0; j <= 16; j++)
                {
                    float x = plane.CenterX - plane.HalfExtentX + i * (2f * plane.HalfExtentX / 16f);
                    float z = plane.CenterZ - plane.HalfExtentZ + j * (2f * plane.HalfExtentZ / 16f);
                    float rest = field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
                        (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y);
                    // The shader formula without its clamp: rest depth plus water level minus the binding reference.
                    double depth = (double)rest + plane.SurfaceY - plane.Optics.Origin.Y;
                    double oracle = plane.SurfaceY - TileWaterBedOracle.Height([surface], x, z);
                    Assert.True(Math.Abs(depth - oracle) <= budget, $"({x}, {z}) depth {depth:R} oracle {oracle:R}");
                    worstError = Math.Max(worstError, Math.Abs(depth - oracle));
                    sampled++;
                }
        }
        _output.WriteLine($"{fixture} {offsetCm / 100.0:F2} m planes {planes.Length} samples {sampled} " +
            $"worst sampled error {worstError:E3} worst reconstruction {worstBound:E3} worst conversion {worstConversion:E3}");
    }

    readonly record struct Conversion(double Depth, double Horizontal, double Slope, double Total);

    // A test-side mirror of the producer's conversion charge, reported beside the field's own bound. It covers the
    // drawn triangles inside the field extent: depth error plus slope times horizontal error, maximised per triangle.
    static Conversion ConversionTerms(TileGroundSurfaceSnapshot surface, Vector3 origin, Vector2 size)
    {
        ReadOnlySpan<Vector3> positions = surface.Payload.Positions;
        ReadOnlySpan<uint> indices = surface.Payload.Indices;
        Matrix4x4 m = surface.WorldTransform;
        bool exactY = m.M12 == 0f && m.M22 == 1f && m.M32 == 0f && m.M42 == 0f;
        double depthMax = 0, horizontalMax = 0, slopeMax = 0, total = 0;
        Span<Vector3> local = stackalloc Vector3[3];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            double depth = 0, horizontal = 0;
            bool inside = true;
            for (int k = 0; k < 3; k++)
            {
                Vector3 p = positions[(int)indices[t + k]];
                double x = (double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31 + m.M41;
                double y = (double)p.X * m.M12 + (double)p.Y * m.M22 + (double)p.Z * m.M32 + m.M42;
                double z = (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33 + m.M43;
                double magnitude = Math.Abs((double)p.X) * (Math.Abs(m.M11) + Math.Abs(m.M12) + Math.Abs(m.M13)) +
                    Math.Abs((double)p.Y) * (Math.Abs(m.M21) + Math.Abs(m.M22) + Math.Abs(m.M23)) +
                    Math.Abs((double)p.Z) * (Math.Abs(m.M31) + Math.Abs(m.M32) + Math.Abs(m.M33)) +
                    Math.Abs(m.M41) + Math.Abs(m.M42) + Math.Abs(m.M43);
                double slack = Binary64Slack * magnitude;
                double exactX = x - origin.X, exactZ = z - origin.Z;
                local[k] = new Vector3((float)exactX, (float)-y, (float)exactZ);
                inside &= local[k].X >= 0f && local[k].X <= size.X && local[k].Z >= 0f && local[k].Z <= size.Y;
                depth = Math.Max(depth, Math.Abs(local[k].Y + y) + (exactY ? 0d : slack));
                horizontal = Math.Max(horizontal, Math.Max(
                    Math.Abs(local[k].X - exactX) + slack + Binary64Slack * (Math.Abs(x) + Math.Abs(origin.X)),
                    Math.Abs(local[k].Z - exactZ) + slack + Binary64Slack * (Math.Abs(z) + Math.Abs(origin.Z))));
            }
            if (!inside || !TileWaterTrianglePreparation.HasProjectedArea(local[0], local[1], local[2])) continue;
            double dx = (double)local[1].X - local[0].X, dz = (double)local[1].Z - local[0].Z;
            double ex = (double)local[2].X - local[0].X, ez = (double)local[2].Z - local[0].Z;
            double db = (double)local[1].Y - local[0].Y, dc = (double)local[2].Y - local[0].Y;
            double determinant = dx * ez - dz * ex;
            double slope = (Math.Abs((db * ez - dc * dz) / determinant) + Math.Abs((dx * dc - ex * db) / determinant)) *
                (1 + 1e-9);
            depthMax = Math.Max(depthMax, depth);
            horizontalMax = Math.Max(horizontalMax, horizontal);
            slopeMax = Math.Max(slopeMax, slope);
            total = Math.Max(total, depth + slope * horizontal);
        }
        return new Conversion(depthMax, horizontalMax, slopeMax, total);
    }
}
