using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>Feathered overlay subdivisions on a sloping edge shared with an uncut neighbour draw T junctions a few
/// hundredths of a micrometre off the long edge. The field certifies that seam inside the producer budget.</summary>
public sealed class TileWaterOpticsFeatherTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(11f, 0f, -11f);

    // The 6E acceptance fixture: two by two water tiles around an ordinary -0.70 m deep corner with grass banks,
    // and a feathered full road overlay on the south pair, whose sixteenths meet the uncut north pair at z = 11.
    static TileWorldDocument FeatherBlock()
    {
        var doc = new TileWorldDocument { Id = "feather-block", DisplayName = "Feather block", PlaneCount = 1 };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        for (int z = 7; z < 15; z++)
            for (int x = 7; x < 15; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Grass);
        for (int z = 10; z < 12; z++)
            for (int x = 10; x < 12; x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 11, 0, -70);
        foreach (int x in new[] { 10, 11 })
        {
            doc.SetOverlay(x, 10, 0, TileRenderTestData.Road);
            doc.SetSettings(x, 10, 0, doc.GetSettings(x, 10, 0) | TileSettings.FeatherOverlay);
        }
        return doc;
    }

    [Fact]
    public void FeatherSubdivisionTJunctionsPublishWithinTheBudget()
    {
        TileWorldDocument doc = FeatherBlock();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed });
        view.LoadRegion(Origin);
        view.Draw(Focus);
        view.Flush();

        Assert.True(view.WaterOpticsFailures.Count == 0,
            string.Join("; ", view.WaterOpticsFailures.Select(f => $"{f.Kind} at level {f.ReferenceSurfaceY:R}")));
        scene.ClearWater();
        view.DrawWaterPlanes();
        WaterPlane[] planes = scene.WaterDraws.ToArray();
        Assert.NotEmpty(planes);
        double budget = Math.Min(0.0001, doc.TileSize / 1024.0);
        TileGroundSurfaceSnapshot? surface = view.AcceptedGroundSurface(Origin, 0);
        var fields = new HashSet<WaterOpticsTriangleField>();
        int sampled = 0;
        for (int i = 0; i <= 32; i++)
            for (int j = 0; j <= 32; j++)
            {
                float x = 10f + i * 0.0625f, z = -12f + j * 0.0625f;
                WaterPlane plane = Array.Find(planes, p => Math.Abs(x - p.CenterX) <= p.HalfExtentX &&
                    Math.Abs(z - p.CenterZ) <= p.HalfExtentZ);
                Assert.True(plane.Optics.IsBound, $"No bound water at ({x}, {z}).");
                WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
                fields.Add(field);
                float rest = field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
                    (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y);
                double depth = (double)rest + plane.SurfaceY - plane.Optics.Origin.Y;
                double oracle = plane.SurfaceY - TileWaterBedOracle.Height([surface], x, z);
                Assert.True(Math.Abs(depth - oracle) <= budget, $"({x}, {z}) depth {depth:R} oracle {oracle:R}");
                sampled++;
            }
        Assert.Equal(33 * 33, sampled);
        WaterOpticsTriangleField feathered = Assert.Single(fields);
        Assert.True(feathered.TriangleCount > 64, $"Only {feathered.TriangleCount} triangles, so the feather did not subdivide.");
        Assert.True(feathered.ReconstructionErrorBoundMetres >= 2.6e-8f,
            $"Bound {feathered.ReconstructionErrorBoundMetres:R} does not charge the seam.");
    }
}
