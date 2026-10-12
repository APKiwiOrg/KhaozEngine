using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

/// <summary>A Decor to Gameplay promotion streams its full ground and cover through the shared queue. The
/// prime or update that promotes builds nothing on its own thread, the coarse ground and far clusters keep
/// drawing, and the frame apply caps bound the replacement.</summary>
public sealed class TileWorldProfilePromotionTests
{
    static readonly RegionCoord SetupFocus = new(5, 1);

    [Fact]
    public async Task Promotion_only_prime_has_no_caller_ground_or_cover_builds()
    {
        using var tmp = new TempDir();
        using var fixture = ProfileFixture.PrimedMeadow(tmp, SetupFocus);
        RecordingTileWorldScene scene = fixture.Scene;
        TileWorldView view = fixture.View;
        RegionCoord[] promotedRegions = ProfileFixture.Column(0, 5);
        Dictionary<RegionCoord, int> coarse = fixture.DrawnGround(ProfileFixture.Focus(SetupFocus));
        Dictionary<RegionCoord, MeshHandle> clusters = fixture.ClusterHandles();
        int meshLoadsBefore = scene.MeshLoads.Count;
        int coverCountBefore = view.GeneratedCoverCount;
        int clusterUnloadsBefore = scene.ClusterMeshUnloads.Count;
        Assert.All(promotedRegions, r => Assert.Equal(TileRegionResidencyState.Decor, view.ResidencyOf(r)));
        Assert.All(promotedRegions, r => Assert.True(clusters.ContainsKey(r)));
        Dictionary<RegionCoord, TileRegionResidencyState> residencyBefore = fixture.Residencies();
        fixture.Witness.Clear();
        int primeThread = Environment.CurrentManagedThreadId;
        int pendingBefore = fixture.Dispatcher.PendingCount;

        fixture.Residency.PrimeAround(TileRenderTestData.CentreOf(new RegionCoord(2, 1)));

        fixture.AssertOnlyPromoted(residencyBefore, promotedRegions);
        Assert.Empty(fixture.Witness.Builds); // dispatcher is paused throughout PrimeAround or Update
        Assert.True(fixture.Dispatcher.PendingCount > pendingBefore);
        Assert.Equal(meshLoadsBefore, scene.MeshLoads.Count);
        Assert.Equal(coverCountBefore, view.GeneratedCoverCount);
        Assert.All(promotedRegions, r => Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(r)));

        // The next frame, workers still paused, builds and uploads nothing and keeps the coarse ground.
        Vector3 focus = ProfileFixture.Focus(new RegionCoord(2, 1));
        Dictionary<RegionCoord, int> drawn = fixture.DrawnGround(focus);
        Assert.Empty(fixture.Witness.Builds);
        Assert.Equal(meshLoadsBefore, scene.MeshLoads.Count);
        Assert.All(promotedRegions, r => Assert.Equal(coarse[r], drawn[r]));
        Assert.Equal(coverCountBefore, view.GeneratedCoverCount);

        // The far clusters stay accepted and keep drawing merged from inside the layer's HLOD range.
        int mergedBefore = scene.ClusterMergedDraws.Count;
        scene.ClearFrame();
        view.Draw(ProfileFixture.Focus(new RegionCoord(6, 2)));
        HashSet<MeshHandle> merged = scene.ClusterMergedDraws.Skip(mergedBefore).Select(d => d.Handle).ToHashSet();
        Assert.Equal(clusterUnloadsBefore, scene.ClusterMeshUnloads.Count);
        Assert.All(promotedRegions, r => Assert.Contains(clusters[r], merged));
        Assert.Empty(fixture.Witness.Builds);

        // The queued bodies run on a worker, never the thread that primed.
        await ProfileFixture.RunOnWorker(fixture.Dispatcher);
        Assert.NotEmpty(fixture.Witness.Builds);
        Assert.All(fixture.Witness.Builds, b => Assert.NotEqual(primeThread, b.Thread));
        Assert.Contains(fixture.Witness.Builds, b => b.Key.Kind == TileWorldBuildKind.Cover);
        Assert.Contains(fixture.Witness.Builds, b => b.Key.Kind == TileWorldBuildKind.FullGround);

        // Completion is applied a bounded amount per frame, ground and cover each within their own cap.
        bool settled = false;
        for (int frame = 0; frame < 64 && !settled; frame++)
        {
            await ProfileFixture.RunOnWorker(fixture.Dispatcher);
            int groundBefore = fixture.GroundUploads;
            Dictionary<RegionCoord, IReadOnlyList<GroundCoverInstance>> coverBefore =
                promotedRegions.ToDictionary(r => r, view.CoverIn);
            scene.ClearFrame();
            view.Draw(focus);
            Assert.InRange(fixture.GroundUploads - groundBefore, 0, ProfileFixture.QueueOptions.MaxFullGroundAppliesPerPump);
            Assert.InRange(promotedRegions.Count(r => !ReferenceEquals(coverBefore[r], view.CoverIn(r))),
                0, ProfileFixture.QueueOptions.MaxCoverAppliesPerPump);
            settled = fixture.Dispatcher.PendingCount == 0 && view.TrackedProfileBuilds == 0;
        }
        Assert.True(settled);
        Assert.All(fixture.Witness.Builds, b => Assert.NotEqual(primeThread, b.Thread));

        Dictionary<RegionCoord, int> full = fixture.DrawnGround(focus);
        foreach (RegionCoord region in promotedRegions)
        {
            Assert.NotEqual(coarse[region], full[region]);
            Assert.Single(scene.MeshUnloads, h => h.Index == coarse[region]);
            Assert.Equal(fixture.FullVertexCount(region), scene.GroundMeshes[full[region]].Vertices.Length);
            Assert.NotEmpty(view.CoverIn(region));
            Assert.Equal(fixture.ExpectedCover(region), view.CoverIn(region).Count);
        }
        Assert.Equal(coverCountBefore + promotedRegions.Sum(r => view.CoverIn(r).Count), view.GeneratedCoverCount);
        Assert.Equal(clusterUnloadsBefore, scene.ClusterMeshUnloads.Count);
    }

    [Fact]
    public void Border_update_promoting_five_regions_has_no_caller_builds()
    {
        using var tmp = new TempDir();
        using var fixture = ProfileFixture.PrimedMeadow(tmp, SetupFocus);
        RecordingTileWorldScene scene = fixture.Scene;
        TileWorldView view = fixture.View;
        RegionCoord[] promotedRegions = ProfileFixture.Column(0, 5);
        Dictionary<RegionCoord, int> coarse = fixture.DrawnGround(ProfileFixture.Focus(SetupFocus));
        int meshLoadsBefore = scene.MeshLoads.Count;
        int coverCountBefore = view.GeneratedCoverCount;
        int clusterLoadsBefore = scene.ClusterMeshLoads.Count;
        Dictionary<RegionCoord, TileRegionResidencyState> residencyBefore = fixture.Residencies();
        fixture.Witness.Clear();

        fixture.Residency.Update(TileRenderTestData.CentreOf(new RegionCoord(4, 1)));

        fixture.AssertOnlyPromoted(residencyBefore, promotedRegions);
        Assert.Empty(fixture.Witness.Builds); // dispatcher is paused throughout PrimeAround or Update
        Assert.Equal(meshLoadsBefore, scene.MeshLoads.Count);
        Assert.Equal(coverCountBefore, view.GeneratedCoverCount);
        Assert.All(promotedRegions, r => Assert.Equal(TileRegionResidencyState.Gameplay, view.ResidencyOf(r)));

        Dictionary<RegionCoord, int> drawn = fixture.DrawnGround(ProfileFixture.Focus(new RegionCoord(4, 1)));
        Assert.Empty(fixture.Witness.Builds);
        Assert.Equal(meshLoadsBefore, scene.MeshLoads.Count);
        Assert.Equal(clusterLoadsBefore, scene.ClusterMeshLoads.Count);
        Assert.All(promotedRegions, r => Assert.Equal(coarse[r], drawn[r]));
    }
}

/// <summary>Records every ground and cover CPU build the view starts, with the thread it ran on, and can refuse
/// selected builds to stand in for a mesher or distribution failure.</summary>
internal sealed class ProfileBuildWitness
{
    public ConcurrentQueue<(TileWorldBuildKey Key, int Thread)> Builds { get; } = new();

    public Func<TileWorldBuildKey, bool>? Refuse { get; set; }

    public void Record(TileWorldBuildKey key)
    {
        Builds.Enqueue((key, Environment.CurrentManagedThreadId));
        if (Refuse?.Invoke(key) == true) throw new InvalidOperationException($"the witness refused {key}.");
    }

    public void Clear() => Builds.Clear();
}

/// <summary>A meadow world with one HLOD tree per region, streamed through an explicit profile on a paused
/// manual dispatcher.</summary>
internal sealed class ProfileFixture : IDisposable
{
    public const int RegionsX = 6;
    public const int RegionsZ = 5;

    public static TileWorldBuildQueueOptions QueueOptions { get; } = new()
    {
        MaxConcurrentBuilds = 64,
        MaxFullGroundAppliesPerPump = 2,
        MaxCoverAppliesPerPump = 2,
        MaxCoarseGroundAppliesPerPump = 2,
        MaxHlodAppliesPerPump = 64,
    };

    public RecordingTileWorldScene Scene { get; } = new();
    public ManualTileBuildDispatcher Dispatcher { get; } = new();
    public ProfileBuildWitness Witness { get; } = new();
    public TileWorldSource Source { get; }
    public TileWorldView View { get; }
    public TileRegionResidency Residency { get; }
    public TileWorldDocument Document => Source.Document;

    ProfileFixture(string directory, TileRegionResidencyProfile profile, TileResidencyConfig config,
                   TileWorldViewOptions? options)
    {
        Source = TileWorldSource.Open(directory);
        View = CreateView(Scene, Source.Document, Dispatcher, Witness, options ?? new TileWorldViewOptions
        {
            PropLayers = new[] { Trees() },
        });
        Residency = new TileRegionResidency(Source, View, config, profile);
    }

    /// <summary>The 6 by 5 meadow, primed and settled at <paramref name="focus"/> with radii 4, 10 and 12.</summary>
    public static ProfileFixture PrimedMeadow(TempDir tmp, RegionCoord focus)
    {
        var fixture = new ProfileFixture(SaveMeadow(tmp), new TileRegionResidencyProfile(4, 10, 12),
            new TileResidencyConfig(LoadRadius: 10, UnloadRadius: 12, MaxLoadsPerUpdate: int.MaxValue), null);
        fixture.Residency.PrimeAround(TileRenderTestData.CentreOf(focus));
        fixture.Settle(Focus(focus));
        Assert.Equal(RegionsX * RegionsZ, fixture.View.LoadedRegionCount);
        Assert.Equal(RegionsX * RegionsZ, fixture.Scene.LiveClusterMeshCount);
        return fixture;
    }

    /// <summary>A fixture over any saved world and profile, without priming.</summary>
    public static ProfileFixture Open(string directory, TileRegionResidencyProfile profile,
                                      TileWorldViewOptions? options = null) =>
        new(directory, profile, new TileResidencyConfig(LoadRadius: 2, UnloadRadius: 3, MaxLoadsPerUpdate: int.MaxValue),
            options);

    public static TileWorldView CreateView(RecordingTileWorldScene scene, TileWorldDocument document,
                                           ManualTileBuildDispatcher dispatcher, ProfileBuildWitness witness,
                                           TileWorldViewOptions? options = null,
                                           TileWorldBuildQueueOptions? queue = null) =>
        new(scene, document, TileRenderTestData.Catalogs, new BoxResolver(), options, queue ?? QueueOptions,
            dispatcher, witness.Record);

    static string SaveMeadow(TempDir tmp)
    {
        string directory = TileRenderTestData.SaveGrid(tmp, RegionsX, RegionsZ);
        TileWorldDocument authored = TileWorldFile.Load(directory);
        authored.SetFoliageLayer(Meadow(RegionsX, RegionsZ));
        for (int rz = 0; rz < RegionsZ; rz++)
            for (int rx = 0; rx < RegionsX; rx++)
                authored.AddObject("tree", rx * TileRegion.Size + 32, rz * TileRegion.Size + 32, 0, 0);
        TileWorldFile.Save(authored, directory);
        return directory;
    }

    /// <summary>Adds the full-density meadow to a saved world.</summary>
    public static void AddMeadow(string directory, int regionsX, int regionsZ)
    {
        TileWorldDocument authored = TileWorldFile.Load(directory);
        authored.SetFoliageLayer(Meadow(regionsX, regionsZ));
        TileWorldFile.Save(authored, directory);
    }

    /// <summary>Full-density bush cover over a regionsX by regionsZ block from the origin.</summary>
    public static TileFoliageLayer Meadow(int regionsX, int regionsZ, float spacing = 4f)
    {
        int width = regionsX * TileRegion.Size + 1;
        int height = regionsZ * TileRegion.Size + 1;
        return new TileFoliageLayer("meadow", 0, 0f, -(regionsZ * TileRegion.Size), 1f, width, height,
            Enumerable.Repeat((byte)255, width * height).ToArray(), 19, spacing, 1f, 1f, 0f,
            [new TileFoliageArchetype("bush", 1f)], [TileRenderTestData.Grass], true, true, 0f, 0f);
    }

    public static TilePropLayerDefinition Trees() => new()
    {
        Id = "trees",
        ArchetypeIds = new HashSet<string>(StringComparer.Ordinal) { "tree" },
        DrawRadius = 576f,
        LodDistance = 64f,
        LodCrossfadeWidth = 16f,
        HlodDistance = 192f,
        HlodCrossfadeWidth = 32f,
        HlodWeldCell = 1.5f,
    };

    /// <summary>The residency of every loaded region.</summary>
    public Dictionary<RegionCoord, TileRegionResidencyState> Residencies() =>
        View.LoadedRegions.ToDictionary(r => r, View.ResidencyOf);

    /// <summary>Asserts that no region arrived or departed since <paramref name="before"/>, that exactly
    /// <paramref name="expected"/> moved from Decor to Gameplay, and that every other region kept its residency.</summary>
    public void AssertOnlyPromoted(Dictionary<RegionCoord, TileRegionResidencyState> before,
                                   IEnumerable<RegionCoord> expected)
    {
        Dictionary<RegionCoord, TileRegionResidencyState> after = Residencies();
        Assert.Equal(Sorted(before.Keys), Sorted(after.Keys));
        RegionCoord[] promoted = before.Keys.Where(r => before[r] == TileRegionResidencyState.Decor &&
                                                        after[r] == TileRegionResidencyState.Gameplay).ToArray();
        Assert.Equal(Sorted(expected), Sorted(promoted));
        Assert.All(before.Keys.Except(promoted), r => Assert.Equal(before[r], after[r]));
    }

    static RegionCoord[] Sorted(IEnumerable<RegionCoord> regions) =>
        regions.OrderBy(r => r.Rz).ThenBy(r => r.Rx).ToArray();

    public static RegionCoord[] Column(int rx, int regionsZ) =>
        Enumerable.Range(0, regionsZ).Select(rz => new RegionCoord(rx, rz)).ToArray();

    public static Vector3 Focus(RegionCoord region) => new(
        region.OriginX + TileRegion.Size * 0.5f, 0f, -(region.OriginZ + TileRegion.Size * 0.5f));

    /// <summary>Runs every held body on a dedicated worker thread, never the calling one.</summary>
    public static Task RunOnWorker(ManualTileBuildDispatcher dispatcher) =>
        Task.Factory.StartNew(dispatcher.RunAll, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Ground uploads, which excludes the cluster meshes sharing the scene's mesh counter.</summary>
    public int GroundUploads => Scene.MeshLoads.Count - Scene.ClusterMeshLoads.Count;

    /// <summary>Runs workers and draws until nothing is queued, held or ready, a bounded number of frames.</summary>
    public void Settle(Vector3 focus)
    {
        for (int frame = 0; frame < 256; frame++)
        {
            Dispatcher.RunAll();
            Scene.ClearFrame();
            View.Draw(focus);
            if (Dispatcher.PendingCount == 0 && View.TrackedProfileBuilds == 0 && View.PendingRebuilds == 0)
                return;
        }
        throw new InvalidOperationException($"the profile queue did not settle, {View.TrackedProfileBuilds} tracked.");
    }

    /// <summary>Draws one frame and returns the ground handle index each region drew, by world transform.</summary>
    public Dictionary<RegionCoord, int> DrawnGround(Vector3 focus)
    {
        Scene.ClearFrame();
        View.Draw(focus);
        var drawn = new Dictionary<RegionCoord, int>();
        foreach (RegionCoord region in View.LoadedRegions)
        {
            Matrix4x4 world = TileGroundMesher.WorldMatrix(Document, region);
            (MeshHandle Handle, Matrix4x4 World)[] matches = Scene.Drawn.Where(d => d.World == world).ToArray();
            if (matches.Length == 1) drawn[region] = matches[0].Handle.Index;
        }
        return drawn;
    }

    /// <summary>The live merged cluster handle of each region, located by its merged tree's position.</summary>
    public Dictionary<RegionCoord, MeshHandle> ClusterHandles()
    {
        var live = Scene.ClusterMeshLoads.Where(h => !Scene.ClusterMeshUnloads.Contains(h));
        var handles = new Dictionary<RegionCoord, MeshHandle>();
        foreach (MeshHandle handle in live)
        {
            GltfMesh mesh = Scene.GroundMeshes[handle.Index];
            float x = mesh.Vertices.Average(v => v.Position.X);
            float z = mesh.Vertices.Average(v => v.Position.Z);
            handles.Add(RegionCoord.Of((int)MathF.Floor(x), (int)MathF.Floor(-z)), handle);
        }
        return handles;
    }

    public int FullVertexCount(RegionCoord region) =>
        TileGroundMesher.Build(Document, TileRenderTestData.Catalogs, region, 0, TileGroundLod.Full)!.Vertices.Length;

    public int ExpectedCover(RegionCoord region) =>
        TileCoverBuildInput.Generate(Document, TileRenderTestData.Catalogs, region,
            new TileGroundMesherOptions().SmoothNormals, Document.FoliageLayers).Count;

    public void Dispose() => View.Dispose();

    sealed class BoxResolver : ITileMeshResolver, ITileLodMeshResolver
    {
        readonly GltfMesh _box = MeshPrimitives.Box(4f);
        public IReadOnlyList<GltfMeshPart>? Resolve(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_box, default) };
        public IReadOnlyList<GltfMeshPart>? ResolveLod(TileObjectArchetype archetype) =>
            new[] { new GltfMeshPart(_box, default) };
        public GltfMesh? ResolveFlatForHlod(TileObjectArchetype archetype) => _box;
    }
}
