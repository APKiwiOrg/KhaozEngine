using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldInteriorCutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelevantEditsQueueOneBuild(bool profileManaged)
    {
        using var rig = new Rig(profileManaged);
        rig.Complete();
        int builds = rig.Builds;
        rig.Doc.AddObject("tree", 20, 20, 0, 0);
        rig.Doc.SetUnderlay(10, 10, 0, 2);
        rig.Dirty();
        rig.Complete();
        Assert.Equal(builds, rig.Builds);
        rig.Doc.SetSettings(11, 10, 0, TileSettings.Indoors);
        rig.Dirty();
        rig.Dirty();
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
        rig.Doc.AddObject("wall", 10, 10, 0, 0);
        rig.Dirty();
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
        rig.Catalogs.Archetype("roof_flat")!.YawOffsetDegrees = 5f;
        rig.Dirty();
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
        rig.Doc.SetCornerHeightCm(10, 10, 0, -10);
        rig.Dirty();
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
    }

    [Fact]
    public void FocusControlsRecentre()
    {
        using var rig = new Rig();
        rig.Complete();
        InteriorHeightField original = Assert.IsType<InteriorHeightField>(rig.View.AppliedInteriorCut);
        rig.View.Observer = new TileCoord(1000, 1000, 0);
        rig.View.Draw(new Vector3(63.999f, 0f, 0f));
        Assert.Equal(0, rig.Dispatcher.PendingCount);
        rig.View.Draw(new Vector3(64f, 0f, 0f));
        Assert.Equal(1, rig.Dispatcher.PendingCount);
        Assert.Same(original, rig.View.AppliedInteriorCut);
        rig.Complete(new Vector3(64f, 0f, 0f));
        Assert.Equal(new Vector2(-192f, -256f), rig.View.AppliedInteriorCut!.Window.Min);
        InteriorHeightField shifted = rig.View.AppliedInteriorCut;
        Assert.Equal(original.BaseY, shifted.BaseY);
        Assert.Equal(original.StepY, shifted.StepY);
        for (int z = 0; z < original.Window.Cells; z++)
            Assert.True(original.Texels.Span.Slice((z * 2048 + 256) * 4, 1792 * 4)
                .SequenceEqual(shifted.Texels.Span.Slice(z * 2048 * 4, 1792 * 4)));
    }

    [Fact]
    public void AppliedFieldAndPicksSharePublication()
    {
        using var rig = new Rig();
        rig.View.Draw(Vector3.Zero);
        Assert.Null(rig.View.AppliedInteriorCut);
        Assert.Empty(rig.Meshes.Threads);
        int viewThread = Environment.CurrentManagedThreadId;
        InteriorTestWorker.Run(rig.Dispatcher);
        Assert.Null(rig.View.AppliedInteriorCut);
        Assert.All(rig.Meshes.Threads, thread => Assert.NotEqual(viewThread, thread));
        Assert.NotEmpty(rig.Meshes.Threads);
        rig.Scene.OnInteriorPublication = field => Assert.Same(field, rig.View.AppliedInteriorCut);
        rig.View.Draw(Vector3.Zero);
        Assert.Same(rig.Scene.InteriorCut, rig.View.AppliedInteriorCut);
        Assert.True(rig.View.AppliedInteriorCut!.Contains(new Vector3(10.5f, 1f, -10.5f)));
        Assert.Single(rig.Logs, line => line.Contains("interior.roof-mesh", StringComparison.Ordinal));
        rig.View.Draw(new Vector3(64f, 0f, 0f));
        rig.Complete(new Vector3(64f, 0f, 0f));
        Assert.Single(rig.Logs, line => line.Contains("interior.roof-mesh", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("layers")]
    [InlineData("replace")]
    [InlineData("dispose")]
    public void StaleInteriorResultsNeverApply(string change)
    {
        using var rig = new Rig();
        rig.View.Draw(Vector3.Zero);
        if (change == "reader") rig.View.InteriorMeshes = null;
        if (change == "layers") rig.Options.PropLayers = [];
        if (change == "replace") rig.View.InteriorMeshes = new InteriorTestMeshes();
        if (change == "dispose") rig.View.Dispose();
        else rig.View.Draw(Vector3.Zero);
        InteriorTestWorker.Run(rig.Dispatcher);
        if (change != "dispose") rig.View.Draw(Vector3.Zero);
        Assert.Null(rig.Scene.InteriorCut);
        Assert.Null(rig.View.AppliedInteriorCut);
    }

    [Fact]
    public void NewerRequestsReplaceTheQueuedWindowAndNeverRunConcurrently()
    {
        using var rig = new Rig();
        rig.View.Draw(Vector3.Zero);
        rig.View.Draw(new Vector3(64f, 0f, 0f));
        rig.View.Draw(new Vector3(128f, 0f, 0f));
        Assert.Equal(1, rig.Dispatcher.PendingCount);
        InteriorTestWorker.Run(rig.Dispatcher);
        rig.View.Draw(new Vector3(128f, 0f, 0f));
        Assert.Null(rig.View.AppliedInteriorCut);
        Assert.Equal(1, rig.Dispatcher.PendingCount);
        rig.Complete(new Vector3(128f, 0f, 0f));
        Assert.Equal(2, rig.Builds);
        Assert.Equal(new Vector2(-128f, -256f), rig.View.AppliedInteriorCut!.Window.Min);
    }

    [Fact]
    public void WindowUsesMetresAtNonQuarterTilePitch()
    {
        using var rig = new Rig(tileSize: 1.2f);
        rig.Complete();
        InteriorWindow window = rig.View.AppliedInteriorCut!.Window;
        Assert.Equal(2134, window.Cells);
        Assert.Equal(0.24f, window.CellSize);
        Assert.InRange(window.Cells * window.CellSize, 512f, 512.24f);
    }

    [Fact]
    public void CappedWindowAndSourceIssuesLogOnce()
    {
        using var rig = new Rig(tileSize: 0.1f);
        rig.Doc.PlaneHeight = 1000f;
        rig.Doc.SetSettings(20, 20, 3, TileSettings.Indoors);
        rig.Complete();
        Assert.Equal(4096, rig.View.AppliedInteriorCut!.Window.Cells);
        Assert.True(rig.View.AppliedInteriorCut.StepY > 0.01f);
        rig.View.Draw(new Vector3(64f, 0f, 0f));
        rig.Complete(new Vector3(64f, 0f, 0f));
        Assert.Single(rig.Logs, line => line.Contains("interior.window", StringComparison.Ordinal));
        Assert.Contains("409.6", rig.Logs.Single(line => line.Contains("interior.window", StringComparison.Ordinal)));
        Assert.Single(rig.Logs, line => line.Contains("interior.step", StringComparison.Ordinal));
        Assert.Single(rig.Logs, line => line.Contains("interior.roof-mesh", StringComparison.Ordinal));
    }

    [Fact]
    public void LowerBuildingOutsideWindowChangesTheSourceEncoding()
    {
        using var rig = new Rig();
        rig.Complete();
        Assert.Equal(0f, rig.View.AppliedInteriorCut!.BaseY);
        var distant = new RegionCoord(10, 0);
        rig.Doc.GetOrCreateRegion(distant);
        rig.Doc.SetSettings(650, 10, 0, TileSettings.Indoors);
        for (int z = 10; z <= 11; z++)
            for (int x = 650; x <= 651; x++) rig.Doc.SetCornerHeightCm(x, z, 0, -500);
        rig.View.MarkDirty(distant, 0);
        rig.Complete();
        Assert.Equal(-5f, rig.View.AppliedInteriorCut!.BaseY);
        Assert.True(rig.View.AppliedInteriorCut.Contains(new Vector3(10.5f, 1f, -10.5f)));
        rig.Doc.RemoveRegion(distant);
        rig.View.UnloadRegion(distant);
        rig.Complete();
        Assert.Equal(0f, rig.View.AppliedInteriorCut.BaseY);
    }

    [Fact]
    public void AdjacentCornerAndWallDependenciesInvalidateTheField()
    {
        using var rig = new Rig();
        rig.Doc.SetSettings(63, 10, 0, TileSettings.Indoors);
        rig.Doc.GetOrCreateRegion(new RegionCoord(1, 0));
        rig.Complete();
        int builds = rig.Builds;
        rig.Doc.SetCornerHeightCm(64, 10, 0, 100);
        rig.View.MarkDirty(new RegionCoord(1, 0), 0);
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
        rig.Doc.AddObject("wall", 64, 10, 0, 0);
        rig.View.MarkDirty(new RegionCoord(1, 0), 0);
        rig.Complete();
        Assert.Equal(++builds, rig.Builds);
    }

    [Fact]
    public void AnimatedFoliageStaysOnItsUncutPathBesideOptedInTrees()
    {
        using var rig = new Rig();
        rig.Doc.AddObject("tree", 10, 10, 0, 0);
        rig.Doc.AddObject("bush", 10, 10, 0, 0);
        rig.Options.AnimatedFoliageArchetypes = new HashSet<string> { "bush" };
        rig.Dirty();
        rig.Complete();
        Assert.Contains(rig.Scene.FoliageDraws, draw => draw.Drawn == 1);
        Assert.DoesNotContain(rig.View.CutArchetypeIds, id => id == "bush");
        Assert.True(rig.Scene.ClusterBuildRequests.Last(request => request.Key.LayerId == "trees").Layer.CutsInsideInteriors);
    }

    [Fact]
    public void CutArchetypeIdsTrackLayerChangesAndDisableClearsTheField()
    {
        using var rig = new Rig();
        rig.Complete();
        Assert.Equal(new[] { "tree" }, rig.View.CutArchetypeIds);
        rig.Options.PropLayers = [Trees() with { ArchetypeIds = new HashSet<string> { "bush" } }];
        rig.View.Draw(Vector3.Zero);
        Assert.Equal(new[] { "bush" }, rig.View.CutArchetypeIds);
        rig.Options.PropLayers = [Trees() with { CutInsideInteriors = false }];
        rig.View.Draw(Vector3.Zero);
        Assert.Empty(rig.View.CutArchetypeIds);
        Assert.Null(rig.View.AppliedInteriorCut);
        InteriorTestWorker.Run(rig.Dispatcher);
        rig.View.Draw(Vector3.Zero);
        Assert.Null(rig.Scene.InteriorCut);
    }

    [Fact]
    public void NullReaderOrNoOptInNeverBuilds()
    {
        using var rig = new Rig();
        rig.View.InteriorMeshes = null;
        rig.Complete();
        Assert.Equal(0, rig.Builds);
        rig.View.InteriorMeshes = rig.Meshes;
        rig.Options.PropLayers = [Trees() with { CutInsideInteriors = false }];
        rig.Complete();
        Assert.Equal(0, rig.Builds);
        rig.View.SettleForCapture(default);
        Assert.Equal(0, rig.Builds);
        Assert.Empty(rig.Meshes.Threads);
    }

    [Fact]
    public void SnapshotDetachesMutableInputsAndKeepsRoofsWhoseAnchorsAreOutside()
    {
        using var rig = new Rig();
        rig.Catalogs.Archetype("roof_flat")!.SizeX = 64;
        TileObject roof = rig.Doc.AllObjects().Single();
        rig.Doc.GetOrCreateRegion(RegionCoord.Of(-270, 10));
        rig.Doc.MoveObject(roof.Id, -270, 10, 1);
        InteriorWindow window = InteriorWindow.Centred(Vector2.Zero, 0.25f, 2048);
        TileWorldDocument doc = TileWorldBuildSnapshot.CaptureInteriors(rig.Doc, window);
        TileWorldCatalogs catalogs = TileWorldBuildSnapshot.CaptureInteriorCatalogs(rig.Catalogs);
        roof.Rotation = 2;
        rig.Catalogs.Archetype("roof_flat")!.SizeX = 1;
        rig.Doc.SetSettings(10, 10, 0, TileSettings.None);
        Assert.Equal(0, Assert.Single(doc.AllObjects()).Rotation);
        Assert.Equal(64, catalogs.Archetype("roof_flat")!.SizeX);
        Assert.Equal(TileSettings.Indoors, doc.GetSettings(10, 10, 0));
    }

    [Fact]
    public void SceneAdapterPublishesTheExactField()
    {
        using var scene = new MotionTestScene();
        ITileWorldScene adapter = new Scene3DTileWorldScene(scene.Scene);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build([], new InteriorWindow(Vector2.Zero, 1f, 1),
            new InteriorEncoding(0f, 0.01f));
        adapter.SetInteriorCut(field);
        Assert.Same(field, scene.Scene.InteriorCut);
        adapter.SetInteriorCut(null);
        Assert.Null(scene.Scene.InteriorCut);
    }

    static TilePropLayerDefinition Trees() => new()
    {
        Id = "trees", ArchetypeIds = new HashSet<string> { "tree" }, DrawRadius = 192f,
        LodDistance = 64f, CutInsideInteriors = true,
    };

    sealed class Rig : IDisposable
    {
        public TileWorldDocument Doc { get; } = new();
        public TileWorldCatalogs Catalogs { get; } = TileWorldCatalogs.Greybox();
        public RecordingTileWorldScene Scene { get; } = new();
        public ManualTileBuildDispatcher Dispatcher { get; } = new();
        public InteriorTestMeshes Meshes { get; } = new();
        public List<string> Logs { get; } = new();
        public TileWorldViewOptions Options { get; }
        public TileWorldView View { get; }
        public int Builds { get; private set; }

        public Rig(bool profileManaged = false, float tileSize = 1f)
        {
            Doc.TileSize = tileSize;
            Doc.GetOrCreateRegion(default);
            Doc.SetUnderlay(10, 10, 0, 1);
            Doc.SetSettings(10, 10, 0, TileSettings.Indoors);
            Doc.AddObject("roof_flat", 10, 10, 1, 0);
            Options = new TileWorldViewOptions { PropLayers = [Trees()], Log = Logs.Add };
            View = new TileWorldView(Scene, Doc, Catalogs, new GreyboxMeshResolver(), Options, null, Dispatcher,
                key => { if (key.Kind == TileWorldBuildKind.InteriorCut) Builds++; });
            if (profileManaged) View.LoadRegion(default, TileRegionResidencyState.Gameplay);
            else View.LoadRegion(default);
            // Settle unrelated ground, cover and prop jobs before observing the interior worker.
            View.SettleForCapture(default);
            InteriorTestWorker.Run(Dispatcher);
            View.InteriorMeshes = Meshes;
        }

        public void Dirty() => View.MarkDirty(new TileRect(10, 10, 2, 2), 0);
        public void Complete(Vector3 focus = default)
        {
            View.Draw(focus);
            InteriorTestWorker.Run(Dispatcher);
            View.Draw(focus);
        }
        public void Dispose()
        {
            InteriorTestWorker.Run(Dispatcher);
            View.Dispose();
        }
    }
}
