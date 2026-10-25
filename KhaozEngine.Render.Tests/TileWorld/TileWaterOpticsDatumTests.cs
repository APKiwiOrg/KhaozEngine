using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>The TileWorld producer stores rest depth against the exact datum 0, so drawn heights that are
/// collinear in binary32 stay collinear in the field. Sloped corner-cut T junctions are ordinary content.</summary>
public sealed class TileWaterOpticsDatumTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly Vector3 Focus = new(11f, 0f, -11f);

    static void Fill(TileWorldDocument doc, ushort material, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(x, z, 0, material);
    }

    // Two by two water tiles around one deep shared corner with a corner cut on every tile, one per rotation. Three
    // cuts meet the deep corner, so their mid-edge points sit on sloping edges shared with an uncut neighbour.
    static TileWorldDocument SlopedCornerCuts()
    {
        var doc = new TileWorldDocument { Id = "sloped-corner-cuts", DisplayName = "Sloped corner cuts", PlaneCount = 1 };
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++) doc.GetOrCreateRegion(new RegionCoord(x, z));
        Fill(doc, TileRenderTestData.Grass, 7, 7, 15, 15);
        Fill(doc, TileRenderTestData.Water, 10, 10, 12, 12);
        doc.SetCornerHeightCm(11, 11, 0, -100);
        int rotation = 0;
        foreach ((int x, int z) in new[] { (10, 10), (11, 10), (10, 11), (11, 11) })
        {
            doc.SetOverlay(x, z, 0, TileRenderTestData.Road);
            doc.SetOverlayShape(x, z, 0, TileOverlayShape.CornerQuarter);
            doc.SetOverlayRotation(x, z, 0, rotation++);
        }
        return doc;
    }

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    static string Describe(IEnumerable<TileWaterOpticsFailure> failures) =>
        string.Join("; ", failures.Select(f => $"{f.Kind} at level {f.ReferenceSurfaceY:R}"));

    [Fact]
    public void SlopedCornerCutTJunctionsPublishAndMatchTheDrawnMesh()
    {
        TileWorldDocument doc = SlopedCornerCuts();
        var scene = new RecordingTileWorldScene();
        using var view = new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(),
            new TileWorldViewOptions { WaterDepthMode = TileWaterDepthMode.AuthoredBed });
        view.LoadRegion(Origin);
        view.Draw(Focus);
        view.Flush();

        Assert.True(view.WaterOpticsFailures.Count == 0, Describe(view.WaterOpticsFailures));
        WaterPlane[] planes = Water(view, scene);
        Assert.NotEmpty(planes);
        double budget = Math.Min(0.0001, doc.TileSize / 1024.0);
        TileGroundSurfaceSnapshot? surface = view.AcceptedGroundSurface(Origin, 0);
        int sampled = 0;
        for (int i = 0; i <= 16; i++)
            for (int j = 0; j <= 16; j++)
            {
                float x = 10f + i * 0.125f, z = -12f + j * 0.125f;
                WaterPlane plane = Array.Find(planes, p => Math.Abs(x - p.CenterX) <= p.HalfExtentX &&
                    Math.Abs(z - p.CenterZ) <= p.HalfExtentZ);
                Assert.True(plane.Optics.IsBound, $"No bound water at ({x}, {z}).");
                Assert.Equal(0f, plane.Optics.Origin.Y);
                WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
                float rest = field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
                    (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y);
                // The shader formula without its clamp: rest depth plus water level minus the binding reference.
                double depth = (double)rest + plane.SurfaceY - plane.Optics.Origin.Y;
                double oracle = plane.SurfaceY - TileWaterBedOracle.Height([surface], x, z);
                Assert.True(Math.Abs(depth - oracle) <= budget, $"({x}, {z}) depth {depth:R} oracle {oracle:R}");
                sampled++;
            }
        Assert.Equal(289, sampled);
    }

    // The exact datum keeps the mid-edge vertices on their long edges. A water-level reference rounds them off by
    // a binary32 step, which the field certifies as a shared-boundary discontinuity instead of refusing.
    [Theory]
    [InlineData(0f)]
    [InlineData(-0.02f)]
    public void TJunctionsPublishAgainstEitherDatum(float reference)
    {
        TileWorldDocument doc = SlopedCornerCuts();
        TileGroundSurfaceSnapshot surface = TileGroundCpuBuild.Build(doc, TileRenderTestData.Catalogs, Origin, 0,
            TileGroundLod.Full, new TileGroundMesherOptions(), captureOpticalSurface: true).Surface!;
        ReadOnlySpan<Vector3> positions = surface.Payload.Positions;
        ReadOnlySpan<uint> indices = surface.Payload.Indices;
        Matrix4x4 m = surface.WorldTransform;
        Assert.Equal(0f, m.M42);
        const float originX = 8f, originZ = -14f;
        var triangles = new List<WaterOpticsTriangle>();
        Span<Vector3> local = stackalloc Vector3[3];
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            bool inside = true;
            for (int k = 0; k < 3; k++)
            {
                Vector3 world = Vector3.Transform(positions[(int)indices[t + k]], m);
                inside &= world.X >= originX && world.X <= originX + 6f && world.Z >= originZ && world.Z <= originZ + 6f;
                // Binary32 rest depth as the producer stores it, against the given reference.
                local[k] = new Vector3(world.X - originX, (float)((double)reference - world.Y), world.Z - originZ);
            }
            double area = ((double)local[1].X - local[0].X) * ((double)local[2].Z - local[0].Z) -
                ((double)local[1].Z - local[0].Z) * ((double)local[2].X - local[0].X);
            if (inside && area != 0) triangles.Add(new WaterOpticsTriangle(local[0], local[1], local[2]));
        }
        Assert.NotEmpty(triangles);
        WaterOpticsRectangle[] certificate = [new(new Vector2(2f, 2f), new Vector2(4f, 4f))];

        var field = new WaterOpticsTriangleField(new Vector2(6f), 6, 6, triangles.ToArray(), certificate);
        Assert.True(field.TrySampleDepth(0.5f, 0.5f, out float deep));
        Assert.Equal((float)((double)reference + 1), deep);
        Assert.InRange(field.ReconstructionErrorBoundMetres, 0f, 0.0001f);
    }
}
