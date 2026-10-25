using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>An independent bed oracle: binary64 vertical-line intersection with every triangle of the actual
/// surface geometry, in world space. It shares nothing with the field producer.</summary>
internal static class TileWaterBedOracle
{
    internal static double Height(IEnumerable<TileGroundSurfaceSnapshot?> surfaces, double x, double z)
    {
        var heights = new List<double>();
        foreach (TileGroundSurfaceSnapshot? surface in surfaces)
            if (surface is not null)
                Collect(surface.Payload.Positions.ToArray(), surface.Payload.Indices.ToArray(), surface.WorldTransform,
                    x, z, heights);
        Assert.NotEmpty(heights);
        foreach (double height in heights) Assert.InRange(height, heights[0] - 1e-6, heights[0] + 1e-6);
        return heights[0];
    }

    internal static double Height(GltfMesh mesh, Matrix4x4 world, double x, double z)
    {
        var heights = new List<double>();
        Collect(mesh.Vertices.Select(v => v.Position).ToArray(), mesh.Indices32, world, x, z, heights);
        Assert.NotEmpty(heights);
        foreach (double height in heights) Assert.InRange(height, heights[0] - 1e-6, heights[0] + 1e-6);
        return heights[0];
    }

    static void Collect(Vector3[] positions, uint[] indices, Matrix4x4 m, double x, double z, List<double> into)
    {
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            (double ax, double ay, double az) = World(positions[indices[i]], m);
            (double bx, double by, double bz) = World(positions[indices[i + 1]], m);
            (double cx, double cy, double cz) = World(positions[indices[i + 2]], m);
            double d = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            if (Math.Abs(d) < 1e-12) continue;
            double l1 = ((x - ax) * (cz - az) - (z - az) * (cx - ax)) / d;
            double l2 = ((bx - ax) * (z - az) - (bz - az) * (x - ax)) / d;
            double l0 = 1 - l1 - l2;
            if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
            into.Add(l0 * ay + l1 * by + l2 * cy);
        }
    }

    static (double X, double Y, double Z) World(Vector3 p, Matrix4x4 m) => (
        (double)p.X * m.M11 + (double)p.Y * m.M21 + (double)p.Z * m.M31 + m.M41,
        (double)p.X * m.M12 + (double)p.Y * m.M22 + (double)p.Z * m.M32 + m.M42,
        (double)p.X * m.M13 + (double)p.Y * m.M23 + (double)p.Z * m.M33 + m.M43);
}

public sealed class TileWorldViewWaterOpticsTriangleTests
{
    static readonly RegionCoord Origin = new(0, 0);
    static readonly RegionCoord East = new(1, 0);
    static readonly Vector3 Focus = new(64f, 0f, -13f);
    readonly ITestOutputHelper _output;

    public TileWorldViewWaterOpticsTriangleTests(ITestOutputHelper output) => _output = output;

    static TileWorldViewOptions Authored() => new() { WaterDepthMode = TileWaterDepthMode.AuthoredBed };

    static TileWorldDocument Doc(params RegionCoord[] regions)
    {
        var doc = new TileWorldDocument { Id = "triangle-publication", DisplayName = "Triangle publication", PlaneCount = 1 };
        foreach (RegionCoord region in regions) doc.GetOrCreateRegion(region);
        return doc;
    }

    static void Fill(TileWorldDocument doc, ushort material, int x0, int z0, int x1, int z1)
    {
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++) doc.SetUnderlay(x, z, 0, material);
    }

    // Water at Origin's east border with grass around it on both sides. Padding reaches into East.
    static TileWorldDocument Border()
    {
        TileWorldDocument doc = Doc(Origin, East);
        Fill(doc, TileRenderTestData.Grass, 48, 4, 80, 24);
        Fill(doc, TileRenderTestData.Water, 62, 12, 64, 15);
        return doc;
    }

    static TileWorldDocument Interior()
    {
        TileWorldDocument doc = Doc(Origin);
        Fill(doc, TileRenderTestData.Grass, 4, 4, 24, 24);
        Fill(doc, TileRenderTestData.Water, 10, 10, 13, 13);
        doc.SetCornerHeightCm(11, 11, 0, -100);
        return doc;
    }

    static TileWorldView View(ITileWorldScene scene, TileWorldDocument doc, TileWorldViewOptions? options = null,
        ManualTileBuildDispatcher? dispatcher = null, ProfileBuildWitness? witness = null) =>
        new(scene, doc, TileRenderTestData.Catalogs, new GreyboxMeshResolver(), options ?? Authored(),
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 8, MaxFullGroundAppliesPerPump = 8, MaxCoarseGroundAppliesPerPump = 8,
            }, dispatcher, witness is null ? null : witness.Record);

    static WaterPlane[] Water(TileWorldView view, RecordingTileWorldScene scene)
    {
        scene.ClearWater();
        view.DrawWaterPlanes();
        return scene.WaterDraws.ToArray();
    }

    static float FieldDepth(RecordingTileWorldScene scene, WaterPlane plane, float x, float z)
    {
        Assert.True(plane.Optics.IsBound);
        WaterOpticsTriangleField field = scene.TriangleFields[plane.Optics.Field];
        Assert.Equal(field.Size, plane.Optics.Size);
        // Physical depth as the shader forms it, without the clamp: rest depth plus level minus the datum.
        return field.SampleDepth((x - plane.Optics.Origin.X) / plane.Optics.Size.X,
            (z - plane.Optics.Origin.Z) / plane.Optics.Size.Y) + plane.SurfaceY - plane.Optics.Origin.Y;
    }

    static double OracleDepth(TileWorldView view, WaterPlane plane, float x, float z, params RegionCoord[] regions) =>
        plane.SurfaceY - TileWaterBedOracle.Height(regions.Select(r => view.AcceptedGroundSurface(r, 0)), x, z);

    static void Settle(TileWorldView view, ManualTileBuildDispatcher dispatcher)
    {
        for (int i = 0; i < 4; i++)
        {
            view.Draw(Focus);
            dispatcher.RunAll();
        }
        view.Draw(Focus);
    }

    [Fact]
    public void FirstPreparationUploadsOneTriangleFieldBeforeAnySubmission()
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, Interior());
        view.LoadRegion(Origin);
        Assert.Single(scene.TriangleLoads);
        Assert.Empty(scene.WaterDraws);
        WaterPlane plane = Assert.Single(Water(view, scene));
        Assert.Equal(OracleDepth(view, plane, 11f, -11f, Origin), FieldDepth(scene, plane, 11f, -11f), 4);
        Assert.Equal(OracleDepth(view, plane, 11.3f, -12.6f, Origin), FieldDepth(scene, plane, 11.3f, -12.6f), 4);
    }

    [Fact]
    public void DefaultModeNeedsNoOpticsAndDoesNoOpticalWork()
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, Interior(), new TileWorldViewOptions());
        view.LoadRegion(Origin);
        view.Draw(Focus);
        Assert.False(Assert.Single(Water(view, scene)).Optics.IsBound);
        Assert.Empty(scene.TriangleLoads);
        Assert.Null(view.AcceptedGroundSurface(Origin, 0));
        Assert.Equal(0, view.WaterRegistry.EpochCount);
        Assert.Equal(new TileWaterOpticsSourceBytes(0, 0), view.WaterOpticsSourceBytes);
    }

    [Fact]
    public void SceneWithOnlyBilinearOpticsIsRefusedBeforeAnyUpload()
    {
        var scene = new BilinearOnlyScene();
        Assert.Throws<NotSupportedException>(() => View(scene, Interior()));
        Assert.Equal(0, scene.Uploads);
        using var legacy = View(scene, Interior(), new TileWorldViewOptions());
        legacy.LoadRegion(Origin);
        Assert.Equal(0, scene.OpticalUploads);
    }

    [Fact]
    public void SharedRowRunsBindOneTriangleField()
    {
        TileWorldDocument doc = Doc(Origin);
        Fill(doc, TileRenderTestData.Grass, 4, 4, 24, 24);
        for (int z = 10; z < 15; z++)
            for (int x = 10; x < (z < 12 ? 15 : 13); x++) doc.SetUnderlay(x, z, 0, TileRenderTestData.Water);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        Assert.Equal(planes[0].Optics.Field, planes[1].Optics.Field);
        WaterOpticsTriangleField field = Assert.Single(scene.TriangleFields).Value;
        Assert.Equal(2, field.CertifiedRectangles.Length);
        foreach (WaterPlane plane in planes)
            Assert.Equal(OracleDepth(view, plane, plane.CenterX, plane.CenterZ, Origin),
                FieldDepth(scene, plane, plane.CenterX, plane.CenterZ), 4);
    }

    [Fact]
    public void DifferentLevelsUseDistinctReferenceFields()
    {
        TileWorldDocument doc = Doc(Origin);
        Fill(doc, TileRenderTestData.Grass, 4, 4, 40, 24);
        Fill(doc, TileRenderTestData.Water, 10, 10, 12, 12);
        Fill(doc, TileRenderTestData.Water, 30, 10, 32, 12);
        for (int z = 8; z <= 14; z++)
            for (int x = 28; x <= 34; x++) doc.SetCornerHeightCm(x, z, 0, 250);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        Assert.NotEqual(planes[0].SurfaceY, planes[1].SurfaceY);
        Assert.NotEqual(planes[0].Optics.Field, planes[1].Optics.Field);
        Assert.Equal(2, scene.TriangleFields.Count);
        // The datum makes rest depth level-independent, so the level key is the only witness of which field a
        // plane binds. Build each level's field from the same accepted surface and match it by Level.
        TileWaterTriangleOutcome expected = TileWaterTrianglePreparation.Prepare(new TileWaterPreparation(
                TileWaterPlanes.Collect(doc, TileRenderTestData.Catalogs, Origin, 0).ToArray(),
                new TileWaterSourceStamp(doc, [], null), 0f, null), Origin, 0, r => view.AcceptedGroundSurface(r, 0),
            new TileWaterSourceContext(doc, TileRenderTestData.Catalogs, new TileGroundMesherOptions(), null));
        Assert.Null(expected.Failure);
        Assert.Equal(2, expected.Fields.Length);
        foreach (WaterPlane plane in planes)
        {
            TileWaterTriangleBuild own = Assert.Single(expected.Fields, b => b.Level == plane.SurfaceY);
            TileWaterTriangleBuild other = Assert.Single(expected.Fields, b => b.Level != plane.SurfaceY);
            WaterOpticsTriangleField bound = scene.TriangleFields[plane.Optics.Field];
            Assert.Equal(own.Origin, plane.Optics.Origin);
            Assert.NotEqual(other.Origin, plane.Optics.Origin);
            Assert.Equal(own.Field.Size, bound.Size);
            Assert.Equal(own.Field.CertifiedRectangles.ToArray(), bound.CertifiedRectangles.ToArray());
            Assert.Equal(0f, plane.Optics.Origin.Y);
            Assert.Equal(OracleDepth(view, plane, plane.CenterX, plane.CenterZ, Origin),
                FieldDepth(scene, plane, plane.CenterX, plane.CenterZ), 4);
        }
    }

    [Fact]
    public void ColourOnlyChangeReusesTriangleBytes()
    {
        var scene = new RecordingTileWorldScene();
        var options = Authored();
        using var view = View(scene, Interior(), options);
        view.LoadRegion(Origin);
        WaterOpticsHandle first = Water(view, scene)[0].Optics.Field;
        options.WaterLook = new WaterLook { Opacity = 0.4f, SwellAmplitude = 0f };
        view.Flush();
        WaterPlane next = Water(view, scene)[0];
        Assert.Same(options.WaterLook, next.Look);
        Assert.Equal(first, next.Optics.Field);
        Assert.Single(scene.TriangleLoads);
    }

    [Fact]
    public void FieldSamplesTheActualTriangleDiagonalNotBilinearHeight()
    {
        TileWorldDocument doc = Doc(Origin);
        Fill(doc, TileRenderTestData.Grass, 8, 8, 13, 13);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 10, 0, -100);
        doc.SetCornerHeightCm(10, 11, 0, -100);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        WaterPlane plane = Assert.Single(Water(view, scene));
        double oracle = OracleDepth(view, plane, 10.5f, -10.5f, Origin);
        double bilinear = plane.SurfaceY - doc.HeightAt(10.5f, -10.5f, 0);
        Assert.Equal(-0.5, doc.HeightAt(10.5f, -10.5f, 0), 5);
        Assert.Equal(oracle, FieldDepth(scene, plane, 10.5f, -10.5f), 4);
        Assert.True(Math.Abs(oracle - bilinear) > 0.4, $"oracle {oracle}, bilinear {bilinear}");
    }

    [Fact]
    public void NeighborLodAndDependentFieldsPublishTogether()
    {
        TileWorldDocument doc = Border();
        doc.SetCornerHeightCm(65, 13, 0, 100);
        var scene = new RecordingTileWorldScene { OpticalPadding = 2f };
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = View(scene, doc, dispatcher: dispatcher);
        view.LoadRegion(East, TileRegionResidencyState.Decor);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        Settle(view, dispatcher);
        TileGroundSurfaceSnapshot coarse = view.AcceptedGroundSurface(East, 0)!;
        Assert.Equal(TileGroundLod.Coarse4, coarse.Lod);
        WaterPlane before = Assert.Single(Water(view, scene));
        Assert.Contains(coarse.Id, view.WaterSourcesOf(Origin, 0));
        double coarseDepth = OracleDepth(view, before, 65f, -13f, Origin, East);
        Assert.Equal(coarseDepth, FieldDepth(scene, before, 65f, -13f), 4);
        TileGroundSurfaceSnapshot originSurface = view.AcceptedGroundSurface(Origin, 0)!;

        var observed = new List<(TileGroundLod Lod, WaterOpticsHandle Field)>();
        view.LoadRegion(East, TileRegionResidencyState.Gameplay);
        view.Draw(Focus);
        scene.BeforeOpticalLoad = () =>
        {
            WaterPlane visible = Assert.Single(Water(view, scene));
            observed.Add((view.AcceptedGroundSurface(East, 0)!.Lod, visible.Optics.Field));
        };
        dispatcher.RunAll();
        view.Draw(Focus);
        scene.BeforeOpticalLoad = null;

        // The dependent upload ran while the coarse source and its matching field were still the visible pair.
        Assert.Equal((TileGroundLod.Coarse4, before.Optics.Field), Assert.Single(observed));
        TileGroundSurfaceSnapshot full = view.AcceptedGroundSurface(East, 0)!;
        Assert.Equal(TileGroundLod.Full, full.Lod);
        Assert.Same(originSurface, view.AcceptedGroundSurface(Origin, 0));
        WaterPlane after = Assert.Single(Water(view, scene));
        Assert.NotEqual(before.Optics.Field, after.Optics.Field);
        Assert.Contains(before.Optics.Field, scene.OpticalUnloads);
        Assert.Equal(new[] { originSurface.Id, full.Id }.OrderBy(i => i.Generation),
            view.WaterSourcesOf(Origin, 0).OrderBy(i => i.Generation));
        double fullDepth = OracleDepth(view, after, 65f, -13f, Origin, East);
        Assert.Equal(fullDepth, FieldDepth(scene, after, 65f, -13f), 4);
        Assert.True(Math.Abs(fullDepth - coarseDepth) > 0.1, $"full {fullDepth}, coarse {coarseDepth}");
        Assert.Single(scene.TriangleFields);
    }

    [Fact]
    public void TopologyOnlyEditInvalidatesOptics()
    {
        TileWorldDocument doc = Doc(Origin);
        Fill(doc, TileRenderTestData.Grass, 8, 8, 13, 13);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Water);
        doc.SetCornerHeightCm(11, 10, 0, -100);
        doc.SetCornerHeightCm(10, 11, 0, -100);
        doc.SetOverlay(10, 10, 0, TileRenderTestData.Road);
        doc.SetOverlayShape(10, 10, 0, TileOverlayShape.DiagonalHalf);
        doc.SetOverlayRotation(10, 10, 0, 0);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        WaterPlane first = Assert.Single(Water(view, scene));
        float before = FieldDepth(scene, first, 10.5f, -10.5f);
        Assert.Equal(OracleDepth(view, first, 10.5f, -10.5f, Origin), before, 4);
        float heightBefore = doc.HeightAt(10.5f, -10.5f, 0);

        doc.SetOverlayRotation(10, 10, 0, 1);
        view.MarkDirty(Origin, 0);
        Assert.Empty(Water(view, scene));
        view.Flush();
        WaterPlane next = Assert.Single(Water(view, scene));
        float after = FieldDepth(scene, next, 10.5f, -10.5f);
        Assert.Equal(heightBefore, doc.HeightAt(10.5f, -10.5f, 0));
        Assert.Equal(OracleDepth(view, next, 10.5f, -10.5f, Origin), after, 4);
        Assert.True(Math.Abs(after - before) > 0.5f, $"before {before}, after {after}");
        Assert.Single(scene.TriangleFields);
    }

    [Fact]
    public void StaleSurfaceNeverBecomesHaloSource()
    {
        TileWorldDocument doc = Border();
        var scene = new RecordingTileWorldScene { OpticalPadding = 2f };
        var dispatcher = new ManualTileBuildDispatcher();
        using var view = View(scene, doc, dispatcher: dispatcher);
        view.LoadRegion(East, TileRegionResidencyState.Gameplay);
        Settle(view, dispatcher);
        TileGroundSurfaceSnapshot stale = view.AcceptedGroundSurface(East, 0)!;
        view.LoadRegion(Origin);
        Assert.Contains(stale.Id, view.WaterSourcesOf(Origin, 0));

        doc.SetCornerHeightCm(65, 13, 0, 100);
        view.MarkDirty(East, 0);
        view.Flush();
        // East's replacement is still pending. Its accepted surface no longer matches the source it supports.
        Assert.Same(stale, view.AcceptedGroundSurface(East, 0));
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, failure.Kind);
        Assert.Equal(East, failure.MissingRegion);
        Assert.Empty(Water(view, scene));
        Assert.Empty(scene.TriangleFields);
        Assert.NotNull(view.AcceptedGroundSurface(Origin, 0));

        dispatcher.RunAll();
        view.Draw(Focus);
        TileGroundSurfaceSnapshot current = view.AcceptedGroundSurface(East, 0)!;
        Assert.NotSame(stale, current);
        WaterPlane plane = Assert.Single(Water(view, scene));
        Assert.DoesNotContain(stale.Id, view.WaterSourcesOf(Origin, 0));
        Assert.Contains(current.Id, view.WaterSourcesOf(Origin, 0));
        Assert.Equal(OracleDepth(view, plane, 65f, -13f, Origin, East), FieldDepth(scene, plane, 65f, -13f), 4);
        Assert.Empty(view.WaterOpticsFailures);
    }

    [Fact]
    public void UploadFailureRollsBackWholePublicationGroup()
    {
        // East is the source of a dependent field on each side: Origin's water at its west border, Far's at its east.
        var far = new RegionCoord(2, 0);
        TileWorldDocument doc = Doc(Origin, East, far);
        Fill(doc, TileRenderTestData.Grass, 48, 4, 144, 24);
        Fill(doc, TileRenderTestData.Water, 62, 12, 64, 15);
        Fill(doc, TileRenderTestData.Water, 128, 12, 130, 15);
        var scene = new RecordingTileWorldScene { OpticalPadding = 2f };
        using var view = View(scene, doc);
        view.LoadRegion(East);
        view.LoadRegion(Origin);
        view.LoadRegion(far);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        WaterOpticsHandle[] fields = Water(view, scene).Select(p => p.Optics.Field).OrderBy(h => h.Index).ToArray();
        Assert.Equal(2, fields.Length);
        TileGroundSurfaceSnapshot east = view.AcceptedGroundSurface(East, 0)!;
        Assert.Contains(east.Id, view.WaterSourcesOf(Origin, 0));
        Assert.Contains(east.Id, view.WaterSourcesOf(far, 0));
        int meshes = scene.MeshLoads.Count, loads = scene.TriangleLoads.Count;

        // An edit north of East remeshes East without changing either dependent's own inputs. The first dependent
        // uploads its replacement and the second dependent's upload throws.
        var north = new TileRect(96, 64, 1, 1);
        scene.ThrowOnOpticalLoad = loads + 2;
        view.MarkDirty(north, 0);
        Assert.Throws<InvalidOperationException>(() => view.Flush());
        Assert.Same(east, view.AcceptedGroundSurface(East, 0));
        Assert.Equal(fields, Water(view, scene).Select(p => p.Optics.Field).OrderBy(h => h.Index));
        Assert.Equal(fields, scene.TriangleFields.Keys.OrderBy(h => h.Index));
        Assert.Equal(meshes, scene.MeshLoads.Count);
        // The first dependent's staged replacement was released with the group.
        Assert.Equal(loads + 1, scene.TriangleLoads.Count);
        Assert.Equal(new[] { scene.TriangleLoads[^1] }, scene.OpticalUnloads);

        scene.ThrowOnOpticalLoad = 0;
        view.MarkDirty(north, 0);
        view.Flush();
        TileGroundSurfaceSnapshot replaced = view.AcceptedGroundSurface(East, 0)!;
        Assert.NotSame(east, replaced);
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        Assert.All(planes, p => Assert.DoesNotContain(p.Optics.Field, fields));
        Assert.All(fields, f => Assert.Contains(f, scene.OpticalUnloads));
        Assert.Contains(replaced.Id, view.WaterSourcesOf(Origin, 0));
        Assert.Contains(replaced.Id, view.WaterSourcesOf(far, 0));
        Assert.Equal(2, scene.TriangleFields.Count);
    }

    [Fact]
    public void PaddingRefreshUploadsOneFieldAndNoMesh()
    {
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, Interior());
        view.LoadRegion(Origin);
        WaterOpticsHandle first = Assert.Single(Water(view, scene)).Optics.Field;
        int meshes = scene.MeshLoads.Count, loads = scene.TriangleLoads.Count;

        // The wider displacement no longer fits the old certificates, so the field is withdrawn and replaced.
        scene.OpticalPadding = 1f;
        view.Flush();
        Assert.Equal(loads + 1, scene.TriangleLoads.Count);
        Assert.Equal(meshes, scene.MeshLoads.Count);
        Assert.Equal(0, view.PendingRebuilds);
        Assert.Equal(new[] { first }, scene.OpticalUnloads);
        WaterPlane plane = Assert.Single(Water(view, scene));
        Assert.NotEqual(first, plane.Optics.Field);
        Assert.Equal(OracleDepth(view, plane, 11f, -11f, Origin), FieldDepth(scene, plane, 11f, -11f), 4);
        view.Draw(Focus);
        Assert.Equal(meshes, scene.MeshLoads.Count);
        Assert.Equal(loads + 1, scene.TriangleLoads.Count);
    }

    [Fact]
    public void MissingReachableSurfaceRefusesWithoutGuessing()
    {
        TileWorldDocument doc = Border();
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        TileWaterOpticsFailure failure = Assert.Single(view.WaterOpticsFailures);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, failure.Kind);
        Assert.Equal(East, failure.MissingRegion);
        Assert.NotNull(failure.Domain);
        Assert.Empty(scene.TriangleLoads);
        view.Draw(Focus);
        Assert.Empty(scene.WaterDraws);
        Assert.NotEmpty(scene.Drawn);

        view.LoadRegion(East);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        Assert.True(Assert.Single(Water(view, scene)).Optics.IsBound);

        // A NoDraw hole inside the certified domain is refused rather than filled.
        TileWorldDocument holed = Interior();
        holed.SetSettings(13, 11, 0, TileSettings.NoDraw);
        var holedScene = new RecordingTileWorldScene();
        using var holedView = View(holedScene, holed);
        holedView.LoadRegion(Origin);
        Assert.Equal(TileWaterOpticsFailureKind.IncompleteSurfaceCoverage, Assert.Single(holedView.WaterOpticsFailures).Kind);
        Assert.Empty(holedScene.TriangleLoads);
    }

    [Fact]
    public void UnrelatedEmptyHaloDoesNotRefuseInteriorWater()
    {
        // No neighbour is loaded, zero-underlay cells surround the bank and a NoDraw hole sits elsewhere.
        TileWorldDocument doc = Interior();
        doc.SetSettings(40, 40, 0, TileSettings.NoDraw);
        doc.SetUnderlay(40, 40, 0, TileRenderTestData.Grass);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        Assert.Empty(view.WaterOpticsFailures);
        WaterPlane plane = Assert.Single(Water(view, scene));
        Assert.Equal(new[] { view.AcceptedGroundSurface(Origin, 0)!.Id }, view.WaterSourcesOf(Origin, 0));
        Assert.Equal(OracleDepth(view, plane, 12f, -12f, Origin), FieldDepth(scene, plane, 12f, -12f), 4);
    }

    [Fact]
    public void AdjacentFieldsUseIdenticalAcceptedSources()
    {
        TileWorldDocument doc = Doc(Origin, East);
        Fill(doc, TileRenderTestData.Grass, 48, 4, 80, 24);
        Fill(doc, TileRenderTestData.Water, 60, 12, 68, 15);
        doc.SetCornerHeightCm(64, 13, 0, -60);
        var scene = new RecordingTileWorldScene { OpticalPadding = 1f };
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        view.LoadRegion(East);
        view.Flush();
        Assert.Empty(view.WaterOpticsFailures);
        WaterPlane[] planes = Water(view, scene);
        Assert.Equal(2, planes.Length);
        var accepted = new[] { view.AcceptedGroundSurface(Origin, 0)!.Id, view.AcceptedGroundSurface(East, 0)!.Id };
        Assert.Equal(accepted.OrderBy(i => i.Generation), view.WaterSourcesOf(Origin, 0).OrderBy(i => i.Generation));
        Assert.Equal(accepted.OrderBy(i => i.Generation), view.WaterSourcesOf(East, 0).OrderBy(i => i.Generation));
        foreach ((float x, float z) in new[] { (64f, -13f), (63.6f, -13.4f), (64.4f, -12.2f) })
        {
            double bed = TileWaterBedOracle.Height(new[] { Origin, East }.Select(r => view.AcceptedGroundSurface(r, 0)), x, z);
            foreach (WaterPlane plane in planes)
                Assert.Equal(bed, plane.SurfaceY - FieldDepth(scene, plane, x, z), 4);
            Assert.Equal(planes[0].SurfaceY - FieldDepth(scene, planes[0], x, z),
                planes[1].SurfaceY - FieldDepth(scene, planes[1], x, z), 5);
        }
    }

    [Fact]
    public void PendingWitnessSurvivesMetadataPruning()
    {
        var far = new RegionCoord(3, 0);
        TileWorldDocument doc = Interior();
        doc.GetOrCreateRegion(far);
        Fill(doc, TileRenderTestData.Grass, 196, 4, 216, 24);
        Fill(doc, TileRenderTestData.Water, 200, 10, 203, 13);
        var scene = new RecordingTileWorldScene();
        var dispatcher = new ManualTileBuildDispatcher();
        var witness = new ProfileBuildWitness();
        using var view = View(scene, doc, dispatcher: dispatcher, witness: witness);
        view.LoadRegion(far);
        view.LoadRegion(Origin, TileRegionResidencyState.Gameplay);
        Assert.Equal(1, view.PendingWaterCount);
        // Releasing the unrelated region prunes its metadata while Origin's request is still pending.
        view.UnloadRegion(far);
        dispatcher.RunAll();
        view.Draw(Focus);
        Assert.True(Assert.Single(Water(view, scene)).Optics.IsBound);
        Assert.Single(witness.Builds, b => b.Key == new TileWorldBuildKey(Origin, 0, TileWorldBuildKind.FullGround));
        view.Flush();
        Assert.Equal(0, view.PendingWaterCount);
    }

    [Fact]
    public void ReleasedEpochNeverRestoresAnOlderWitness()
    {
        // Edit and revert yields equal values but a fresh epoch. Released and recreated entries never restore it.
        TileWorldDocument doc = Interior();
        var registry = new TileWaterDependencyRegistry();
        var dependency = new TileWaterOpticsDependency(Origin, 0);
        var pending = new TileWaterSourceStamp(doc, [dependency], registry);
        registry.Sweep([pending]);
        Assert.True(pending.IsCurrent(doc, registry));
        registry.Changed(dependency);
        registry.Changed(dependency);
        Assert.False(pending.IsCurrent(doc, registry));
        var current = new TileWaterSourceStamp(doc, [dependency], registry);
        registry.Sweep([]);
        Assert.Equal(0, registry.EpochCount);
        var recreated = new TileWaterSourceStamp(doc, [dependency], registry);
        Assert.True(recreated.IsCurrent(doc, registry));
        Assert.False(current.IsCurrent(doc, registry));
        Assert.False(pending.IsCurrent(doc, registry));
    }

    [Fact]
    public void LastDependentReleasePrunesEpochsAndRefusals()
    {
        var refused = new RegionCoord(2, 0);
        TileWorldDocument doc = Interior();
        doc.GetOrCreateRegion(refused);
        Fill(doc, TileRenderTestData.Grass, 176, 4, 192, 24);
        Fill(doc, TileRenderTestData.Water, 190, 12, 192, 15);
        var scene = new RecordingTileWorldScene();
        using var view = View(scene, doc);
        view.LoadRegion(Origin);
        view.LoadRegion(refused);
        Assert.Equal(TileWaterOpticsFailureKind.MissingSurfaceData, Assert.Single(view.WaterOpticsFailures).Kind);
        Assert.Equal(1, view.WaterRefusalCount);
        Assert.Equal(2, view.WaterRegistry.DependentCount);
        Assert.True(view.WaterRegistry.EpochCount > 0);
        TileWaterOpticsSourceBytes bytes = view.WaterOpticsSourceBytes;
        Assert.Equal(view.AcceptedGroundSurface(Origin, 0)!.RetainedBytes + view.AcceptedGroundSurface(refused, 0)!.RetainedBytes,
            bytes.Resident);
        Assert.Equal(0, bytes.Pending);
        _output.WriteLine($"retained source bytes resident={bytes.Resident} pending={bytes.Pending} " +
            $"origin={view.AcceptedGroundSurface(Origin, 0)!.RetainedBytes} refused={view.AcceptedGroundSurface(refused, 0)!.RetainedBytes} " +
            $"field={Assert.Single(scene.TriangleFields).Value.RetainedBytes} epochs={view.WaterRegistry.EpochCount}");

        view.UnloadRegion(refused);
        Assert.Empty(view.WaterOpticsFailures);
        Assert.Equal(0, view.WaterRefusalCount);
        Assert.Equal(1, view.WaterRegistry.DependentCount);
        view.UnloadRegion(Origin);
        Assert.Equal(0, view.WaterRegistry.EpochCount);
        Assert.Equal(0, view.WaterRegistry.SourceKeyCount);
        Assert.Equal(0, view.WaterRegistry.DependentCount);
        Assert.Equal(new TileWaterOpticsSourceBytes(0, 0), view.WaterOpticsSourceBytes);
        Assert.Empty(scene.TriangleFields);
    }

    // Bilinear upload alone is not the capability. The view needs ITileWorldWaterTriangleOpticsScene.
    sealed class BilinearOnlyScene : ITileWorldScene
    {
        public int Uploads { get; private set; }
        public int OpticalUploads { get; private set; }
        public MeshHandle LoadMesh(GltfMesh mesh) => new(Uploads++, 1);
        public void UnloadMesh(MeshHandle handle) { }
        public void DrawMesh(MeshHandle handle, Matrix4x4 world) { }
        public IReadOnlyList<MeshHandle> LoadPropMeshes(IReadOnlyList<GltfMeshPart> parts) => Array.Empty<MeshHandle>();
        public void UnloadPropMeshes(IReadOnlyList<MeshHandle> handles) { }
        public int DrawProps(IReadOnlyList<PropPlacement> placements,
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> parts, Vector3 focus, float drawRadius) => 0;
        public WaterOpticsHandle LoadWaterOpticsField(WaterOpticsField field)
        {
            OpticalUploads++;
            return new WaterOpticsHandle(Guid.NewGuid(), 0, 1);
        }
        public void UnloadWaterOpticsField(WaterOpticsHandle handle) { }
    }
}
