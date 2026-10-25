using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldOfflineCaptureTests
{
    [Fact]
    public void A_consumer_can_construct_and_settle_a_view_without_friend_assembly_access()
    {
        const string source = """
            using System.Numerics;
            using KhaozEngine.TileWorld;
            public static class OfflineConsumer
            {
                public static void Capture(ITileWorldScene scene, TileWorldDocument doc,
                    TileWorldCatalogs catalogs, ITileMeshResolver resolver, TileWorldViewOptions options)
                {
                    using var view = new TileWorldView(scene, doc, catalogs, resolver, options);
                    view.LoadRegion(default);
                    view.SettleForCapture(default);
                    view.SettleForCapture(default, Vector3.Zero);
                    view.SettleInteriorCutForCapture(Vector3.Zero);
                    view.Draw(Vector3.Zero);
                }
            }
            """;
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<MetadataReference> references = platform
            .Concat(Directory.EnumerateFiles(AppContext.BaseDirectory, "KhaozEngine.*.dll"))
            .Distinct(StringComparer.Ordinal).Select(path => MetadataReference.CreateFromFile(path));
        CSharpCompilation compilation = CSharpCompilation.Create("OfflineTileWorldConsumer",
            [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Settle_applies_ground_cover_and_every_selected_cluster_and_is_idempotent(bool held)
    {
        TileWorldDocument doc = Meadow();
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions { PropLayers = [ProfileFixture.Trees()] };
        var dispatcher = new ManualTileBuildDispatcher();
        using TileWorldView view = CreateView(scene, doc, options, held ? dispatcher : null);
        for (int x = 0; x < 4; x++)
        {
            var region = new RegionCoord(x, 0);
            if (held) view.LoadRegion(region, x == 3 ? TileRegionResidencyState.Decor : TileRegionResidencyState.Gameplay);
            else view.LoadRegion(region);
        }
        var focus = new Vector3(32f, 0f, -32f);
        if (held)
        {
            view.Draw(focus);
            Assert.True(dispatcher.PendingCount > 0);
            Assert.Empty(scene.Drawn);
            Assert.Empty(scene.ClusterMeshLoads);
            Assert.Equal(0, view.GeneratedCoverCount);
        }

        view.SettleForCapture(default);

        scene.ClearFrame();
        view.Draw(focus);
        Assert.Equal(4, scene.Drawn.Count);
        Assert.Equal(4, scene.LiveClusterMeshCount);
        Assert.True(view.GeneratedCoverCount > 0);
        Assert.True(view.LastDrawnCover > 0);
        Assert.Equal(held ? 3 : 4, scene.FoliageDraws.Count);
        Assert.All(scene.FoliageDraws, draw => Assert.NotEmpty(draw.Instances));
        Assert.Equal(view.GeneratedCoverCount, scene.FoliageDraws.Sum(draw => draw.Instances.Count));
        Assert.Equal(0, view.PendingRebuilds);
        MeshHandle[] ground = scene.Drawn.Select(draw => draw.Handle).ToArray();
        int uploads = scene.MeshLoads.Count;
        int builds = scene.ClusterBuildRequests.Count;
        int coverCount = view.GeneratedCoverCount;
        IReadOnlyList<GroundCoverInstance>[] cover = scene.FoliageDraws.Select(draw => draw.Instances).ToArray();

        view.SettleForCapture(default);

        scene.ClearFrame();
        view.Draw(focus);
        Assert.Equal(ground, scene.Drawn.Select(draw => draw.Handle).ToArray());
        Assert.Equal(uploads, scene.MeshLoads.Count);
        Assert.Equal(builds, scene.ClusterBuildRequests.Count);
        Assert.Equal(coverCount, view.GeneratedCoverCount);
        Assert.Empty(scene.FoliageReleases);
        Assert.Equal(cover.Length, scene.FoliageDraws.Count);
        for (int i = 0; i < cover.Length; i++) Assert.Same(cover[i], scene.FoliageDraws[i].Instances);

        scene.ClusterMergedDraws.Clear();
        view.Draw(new Vector3(500f, 0f, -32f));
        Assert.Equal(4, scene.ClusterMergedDraws.Count);

        view.UnloadRegion(default);
        if (held) view.LoadRegion(new RegionCoord(4, 0), TileRegionResidencyState.Gameplay);
        else view.LoadRegion(new RegionCoord(4, 0));
        view.SettleForCapture(new RegionCoord(4, 0));

        scene.ClearFrame();
        scene.ClusterMergedDraws.Clear();
        view.Draw(new Vector3(288f, 0f, -32f));
        Assert.Equal(4, scene.Drawn.Count);
        Assert.Equal(4, scene.LiveClusterMeshCount);
        Assert.DoesNotContain(default(RegionCoord), view.LoadedRegions);
        Assert.Contains(new RegionCoord(4, 0), view.LoadedRegions);
        Assert.Equal(held ? 3 : 4, scene.FoliageDraws.Count);
        Assert.Contains(scene.FoliageDraws, draw => draw.Instances.Any(instance => instance.Position.X >= 256f));
        scene.ClusterMergedDraws.Clear();
        view.Draw(new Vector3(560f, 0f, -32f));
        Assert.Equal(4, scene.ClusterMergedDraws.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interior_only_settle_publishes_the_moved_focus_without_rebuilding_ground_or_clusters(bool held)
    {
        var doc = new TileWorldDocument { PlaneCount = 2 };
        doc.GetOrCreateRegion(default);
        doc.SetUnderlay(10, 10, 0, TileRenderTestData.Grass);
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("tree", 12, 10, 0, 0);
        var scene = new RecordingTileWorldScene();
        var options = new TileWorldViewOptions
        {
            InteriorMeshes = new InteriorTestMeshes(),
            PropLayers = [ProfileFixture.Trees() with { CutInsideInteriors = true }],
        };
        using TileWorldView view = CreateView(scene, doc, options, held ? new ManualTileBuildDispatcher() : null);
        if (held) view.LoadRegion(default, TileRegionResidencyState.Gameplay);
        else view.LoadRegion(default);
        var focus = new Vector3(64f, 0f, 0f);

        view.SettleForCapture(default, focus);

        InteriorHeightField original = Assert.IsType<InteriorHeightField>(view.AppliedInteriorCut);
        Assert.Same(original, scene.InteriorCut);
        Assert.Equal(new Vector2(-192f, -256f), original.Window.Min);
        Assert.True(original.Contains(new Vector3(10.5f, 1f, -10.5f)));
        int uploads = scene.MeshLoads.Count;
        int builds = scene.ClusterBuildRequests.Count;
        int publications = scene.InteriorPublications.Count;

        view.SettleInteriorCutForCapture(new Vector3(128f, 0f, 0f));

        InteriorHeightField moved = Assert.IsType<InteriorHeightField>(view.AppliedInteriorCut);
        Assert.NotSame(original, moved);
        Assert.Same(moved, scene.InteriorCut);
        Assert.Equal(new Vector2(-128f, -256f), moved.Window.Min);
        Assert.True(moved.Contains(new Vector3(10.5f, 1f, -10.5f)));
        Assert.Equal(publications + 1, scene.InteriorPublications.Count);
        Assert.Equal(uploads, scene.MeshLoads.Count);
        Assert.Equal(builds, scene.ClusterBuildRequests.Count);

        view.SettleInteriorCutForCapture(new Vector3(128f, 0f, 0f));

        Assert.Same(moved, view.AppliedInteriorCut);
        Assert.Equal(publications + 1, scene.InteriorPublications.Count);
    }

    static TileWorldView CreateView(RecordingTileWorldScene scene, TileWorldDocument doc,
        TileWorldViewOptions options, ManualTileBuildDispatcher? dispatcher) => dispatcher is null
        ? new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new CaptureMeshResolver(), options)
        : new TileWorldView(scene, doc, TileRenderTestData.Catalogs, new CaptureMeshResolver(), options,
            new TileWorldBuildQueueOptions
            {
                MaxConcurrentBuilds = 1,
                MaxFullGroundAppliesPerPump = 0,
                MaxCoarseGroundAppliesPerPump = 0,
                MaxCoverAppliesPerPump = 0,
                MaxHlodAppliesPerPump = 0,
                MaxInteriorCutAppliesPerPump = 0,
            }, dispatcher);

    static TileWorldDocument Meadow()
    {
        var doc = new TileWorldDocument { PlaneCount = 1 };
        for (int region = 0; region < 5; region++)
        {
            doc.GetOrCreateRegion(new RegionCoord(region, 0));
            for (int z = 0; z < TileRegion.Size; z++)
                for (int x = 0; x < TileRegion.Size; x++)
                    doc.SetUnderlay(region * TileRegion.Size + x, z, 0, TileRenderTestData.Grass);
            doc.AddObject("tree", region * TileRegion.Size + 32, 32, 0, 0);
        }
        doc.SetFoliageLayer(ProfileFixture.Meadow(5, 1));
        return doc;
    }

    sealed class CaptureMeshResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GltfMesh _box = MeshPrimitives.Box(4f);
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            [new GltfMeshPart(_box, default)];
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) => Resolve(archetype);
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _box;
    }
}
