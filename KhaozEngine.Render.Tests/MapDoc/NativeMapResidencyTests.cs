using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapResidencyTests
{
    [Fact]
    public void DisposeWithHeldWorker_ReleasesItsChargesOnlyAfterCancellationUnwinds()
    {
        using var f = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device, null, scheduler);
        Assert.True(residency.Lifetime.CpuBytes > 0); Assert.True(residency.Lifetime.QueuedBytes > 0);
        residency.Dispose();
        Assert.True(residency.Lifetime.CpuBytes > 0);
        scheduler.RunAll();
        Assert.Equal(0, residency.Lifetime.CpuBytes); Assert.Equal(0, residency.Lifetime.QueuedBytes);
        Assert.Equal(0, residency.Lifetime.WorkerCount); Assert.Equal(0, device.Begins);
    }

    [Fact]
    public void SceneWithoutWater_DoesNotRequireWaterDisplacementPadding()
    {
        using var f = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        var scope = ResidencyFixture.Window() with { MotionPaddingMetres = 0 };
        using var residency = new MapRenderResidency(f.State(scope), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Draws);
    }

    [Fact]
    public void DifferentPlantWindows_KeepOneModelOwnerAndIndependentInstanceMemberships()
    {
        var closure = NativeRenderAssetFixtures.Create("foliage");
        using var f = ResidencyFixture.Placement(closure);
        f.Document.Placements = [new() { Id = "left", AssetId = "A", Kind = "plant", NumericId = 1, X = 0, Y = 0 },
            new() { Id = "right", AssetId = "A", Kind = "plant", NumericId = 2, X = 6, Y = 0 }];
        var policy = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale }), new Dictionary<string, KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent>
        { ["A"] = new("A", closure.GetAsset("A").RenderBounds, 1, 0) });
        var left = ResidencyFixture.Window(-2, 2); var right = ResidencyFixture.Window(4, 8);
        var state = f.State(left, right) with { Policy = ResidencyFixture.Policy(foliage: policy, animated: new HashSet<string> { "A" }) };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(4, device.Live.Count);
        Assert.Equal(2, ResidencyFixture.Draw(residency).Draws.Count);
        residency.SetWindows([right]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(3, device.Live.Count);
        Assert.Equal("right", Assert.Single(ResidencyFixture.Draw(residency).Draws).Resource.Key.OwnerId);
    }

    [Fact]
    public void PublishedComposite_EnablesHlodOnlyAfterEveryResourceIsReady()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.Placements(closure);
        using var f = new ResidencyFixture(fixture.Document, closure);
        var scope = ResidencyFixture.Window(-16, 16);
        var state = f.State(scope) with
        {
            Policy = new(NativeMapPlacementTests.Policy(true), new HashSet<string>(),
            ResidencyFixture.Policy().Foliage, ResidencyFixture.Policy().Water, [], new Dictionary<ushort, string>())
        };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        Assert.Empty(ResidencyFixture.Draw(residency).Draws);
        ResidencyFixture.Complete(residency, scheduler);
        var recorder = new MapDrawRecorder(); var focus = new MapFramePoint(WorldFrame.Origin, new(18, 0, 32));
        residency.Record(new(WorldFrame.Origin, focus, MapRoofMode.AlwaysVisible, 0, 0, focus), recorder);
        Assert.Contains(recorder.Snapshot().Draws, d => d.Resource.Key.PartId == "hlod");
    }

    [Fact]
    public void PolicyDigest_AccountsForVectorFieldsAndIgnoresDictionaryInsertionOrder()
    {
        NativeFoliagePolicy Foliage(Vector2 wind) => new(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale, WindDirection = wind }), new Dictionary<string, KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent>());
        var a = ResidencyFixture.Policy(foliage: Foliage(Vector2.UnitX));
        var b = ResidencyFixture.Policy(foliage: Foliage(Vector2.UnitY));
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void AnchorOutsideWindow_LastMembershipOwnsOneResource()
    {
        using var f = ResidencyFixture.Placement();
        f.Document.Placements[0].X = -1;
        var a = ResidencyFixture.Window(-1.1f, -.99f);
        var b = ResidencyFixture.Window(-.99f, -.9f);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(a, b), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        int owners = device.Live.Count;
        Assert.True(owners > 0);
        Assert.Single(ResidencyFixture.Draw(residency).Draws.Where(d => d.Rigid.HasValue).Select(d => d.Resource.Key.OwnerId).Distinct());
        residency.SetWindows([b]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(owners, device.Live.Count);
        Assert.Equal(owners, device.Begins);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Empty(device.Live);
        Assert.Equal(0, residency.Lifetime.LiveHandles);
    }

    [Fact]
    public void CanonicalWaterCell_OverlappingWindowsRetainOneHandle()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        using var f = new ResidencyFixture(fixture.Document, fixture.Assets);
        var a = NativeSceneFixtures.WaterWindow(0, 1.25f) with { MotionPaddingMetres = 1 };
        var b = NativeSceneFixtures.WaterWindow(.75f, 2) with { MotionPaddingMetres = 1 };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(a, b), device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        Assert.Single(device.Live);
        Assert.Single(ResidencyFixture.Draw(residency).Draws);
        residency.SetWindows([b]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Single(device.Live); Assert.Equal(1, device.Begins);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Empty(device.Live); Assert.Equal(1, device.Releases);
    }

    [Fact]
    public void DeepLayerResidency_LoadsOnlyApprovedWorkingSet()
    {
        var closure = NativeRenderAssetFixtures.Create("distant");
        using var f = ResidencyFixture.Placement(closure, true);
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device,
            new(MaxResidentCpuBytes: 64 * 1024 * 1024), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var set = ResidencyFixture.Draw(residency);
        Assert.NotEmpty(set.Draws);
        Assert.All(set.Resources, r => Assert.StartsWith("near", r.Reference.Key.OwnerId, StringComparison.Ordinal));
        Assert.Equal(new[] { "prop-a" }, residency.AcceptedPlans.Single().Input.Assets.ById.Keys);
        using var cache = new MapRenderAssetCache();
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 4_194_304));
        using var selected = cache.Acquire(closure, ["prop-a"], [], residency.DesiredState.Extents, budget).Input!;
        Assert.Equal(new[] { "prop-a" }, selected.Assets.ById.Keys);
        long retained = residency.AcceptedPlans.Single().Input.RetainedBytes;
        Assert.InRange(retained, 1, 4 * 1024 * 1024);
        Assert.DoesNotContain("prop-23", residency.AcceptedPlans.Single().Input.Assets.ById.Keys);
        residency.SetWindows([]); ResidencyFixture.Complete(residency, scheduler);
        Assert.Equal(0, residency.AssetCache.RetainedBytes);
    }

    [Fact]
    public void AggregateBudgetsIncludeOldReplacementAndRetirement()
    {
        var admission = new MapRenderAdmission(new(MaxResidentCpuBytes: 1000, MaxResidentGpuBytes: 1000));
        var demand = new SceneUploadDemand("invented", 350, 100, 100, 350, 100);
        using var accepted = admission.TryReserve(demand)!;
        accepted.Claim(demand); accepted.Published(new(1));
        using var retired = admission.TryReserve(demand)!;
        retired.Claim(demand); retired.Published(new(2)); retired.Retiring();
        Assert.Equal(900, admission.GpuBytes);
        Assert.Null(admission.TryReserve(demand));
        retired.Dispose(); Assert.Null(admission.TryReserve(demand));
        retired.Released();
        using var replacement = admission.TryReserve(demand);
        Assert.NotNull(replacement);
        replacement.Dispose(); accepted.Retiring(); accepted.Released();
        Assert.Equal(0, admission.GpuBytes); Assert.Equal(0, admission.CpuBytes);
    }

    [Fact]
    public void AggregateCap_BackpressuresBeforeFactoriesUntilHeldRetirementCompletes()
    {
        using var f = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice { HoldRetirement = true };
        using var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device,
            new(MaxResidentGpuBytes: 1600, MaxAppliesPerUpdate: 3), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var accepted = residency.Update().Transition.AcceptedWitness;
        f.Document.Placements[0].X = .1f;
        var changed = f.State(ResidencyFixture.Window());
        residency.RequestReplacement(changed); scheduler.RunAll();
        residency.Update();
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.InRange(residency.Lifetime.GpuBytes, 1, 1600);
        Assert.Equal(10, device.Begins);
        // Cancel the partially uploaded generation. Its GPU credit remains held by the device.
        f.Document.Placements[0].X = .2f;
        residency.RequestReplacement(f.State(ResidencyFixture.Window())); scheduler.RunAll(); residency.Update(); residency.Update();
        Assert.Equal(14, device.Begins);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        device.CompleteRetirements();
        ResidencyFixture.Complete(residency, scheduler);
        Assert.NotEqual(accepted, residency.Update().Transition.AcceptedWitness);
    }

    [Fact]
    public void UploadBudgetIsIncrementalAndPublicationIsAtomic()
    {
        using var f = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice { TransferBytes = 5_000_000 };
        using var residency = new MapRenderResidency(f.State(ResidencyFixture.Window()), device, null, scheduler);
        residency.Update(); scheduler.RunAll();
        for (int i = 0; i < 12; i++)
        {
            long before = residency.Lifetime.UploadBytes;
            var state = residency.Update();
            Assert.InRange(residency.Lifetime.UploadBytes - before, 0, 4_194_304);
            Assert.Equal(state.Transition.AcceptedWitness is null ? 0 : 3, ResidencyFixture.Draw(residency).Draws.Count);
            if (state.Transition.Status == MapSceneStatus.CompleteForScope) break;
        }
        Assert.NotNull(residency.Update().Transition.AcceptedWitness);
    }
}

internal sealed class ResidencyScheduler : TaskScheduler
{
    readonly Queue<Task> _tasks = new();
    protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
    protected override void QueueTask(Task task) => _tasks.Enqueue(task);
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    internal void RunAll() { while (_tasks.TryDequeue(out var task)) TryExecuteTask(task); }
}

internal sealed class ResidencyDevice : IMapRenderDevice
{
    internal readonly Dictionary<MapRenderHandle, MapPreparedResource> Live = new();
    readonly Dictionary<MapRenderHandle, ISceneUploadReservation> _leases = new();
    readonly List<ISceneUploadReservation> _retirements = [];
    long _next;
    internal int Begins, Publications, Releases;
    internal long TransferBytes = 32;
    internal bool HoldRetirement;
    public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => new(resource.PayloadDigest, 100, 10, 100, 100, TransferBytes);
    public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation)
    { reservation.Claim(MeasureUpload(resource)); Begins++; return new Upload(this, resource, reservation); }
    public void Release(MapRenderHandle handle)
    {
        Assert.True(Live.Remove(handle)); Releases++;
        var lease = _leases[handle]; _leases.Remove(handle); Retire(lease);
    }
    void Retire(ISceneUploadReservation lease)
    { lease.Retiring(); if (HoldRetirement) _retirements.Add(lease); else lease.Released(); }
    internal void CompleteRetirements() { foreach (var lease in _retirements) lease.Released(); _retirements.Clear(); }
    sealed class Upload(ResidencyDevice owner, MapPreparedResource resource, ISceneUploadReservation lease) : IMapRenderUpload
    {
        long _remaining = owner.TransferBytes;
        bool _published, _disposed;
        public bool Ready => _remaining == 0;
        public long Advance(long transferBytes)
        { long copied = Math.Min(transferBytes, _remaining); _remaining -= copied; return copied; }
        public MapRenderHandle Publish()
        {
            Assert.True(Ready); Assert.False(_disposed); Assert.False(_published);
            _published = true; var handle = new MapRenderHandle(++owner._next);
            lease.Published(new(handle.Value)); owner.Live.Add(handle, resource); owner._leases.Add(handle, lease);
            owner.Publications++; return handle;
        }
        public void Dispose() { if (_disposed) return; _disposed = true; if (!_published) owner.Retire(lease); }
    }
}

internal sealed class ResidencyFixture : IDisposable
{
    readonly List<MapDocumentSource> _sources = [];
    internal MapDocument Document { get; }
    internal MapAssetClosure Closure { get; }
    internal ResidencyFixture(MapDocument document, MapAssetClosure closure) { Document = document; Closure = closure; }
    internal static ResidencyFixture Placement(MapAssetClosure? closure = null, bool distant = false)
    {
        closure ??= NativeRenderAssetFixtures.Create();
        using var empty = NativeSceneFixtures.Create("empty");
        var doc = empty.Document; doc.NativeAssets = closure.Roots.ToList(); doc.NumericIdHighWaterMark = 24;
        doc.Placements = closure.Assets.Where(a => distant || a.Id == "prop-a").OrderBy(a => a.Id == "prop-a" ? 0 : 1).Select((a, i) => new MapPlacement
        { Id = i == 0 ? "near" : "remote-" + i, AssetId = a.Id, Kind = "cosmetic", NumericId = i + 1, Y = 0, X = i == 0 ? 0 : 1000 + i * 64 }).ToList();
        return new(doc, closure);
    }
    internal static MapSceneScope Window(float min = -4, float max = 4) => new(new(WorldFrame.Origin,
        new(min, -4), new(max, 4), null, null, Enum.GetValues<MapSurfaceRole>(), null, new()),
        new(min, -500, -4, max, 500, 4), MapSceneChannels.Placements | MapSceneChannels.Annotations, 1);
    internal static MapRenderPolicy Policy(float radius = 100, NativeFoliagePolicy? foliage = null, IReadOnlySet<string>? animated = null,
        IReadOnlyList<string>? materials = null, IReadOnlyDictionary<ushort, string>? terrain = null) => new(new([], radius),
        animated ?? new HashSet<string>(), foliage ?? new(MapFrozenFoliageOptions.Capture(new GroundCoverRenderOptions
        { UseGpuBatches = true, FadeMode = GroundCoverFadeMode.HeightScale }), new Dictionary<string, KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent>()),
        MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = 0, WaveSource = WaterWaveSource.Procedural }), materials ?? [],
        terrain ?? new Dictionary<ushort, string>());
    internal MapRenderSourceState State(params MapSceneScope[] windows)
    {
        var document = MapDocumentSource.FromDocument(Document); _sources.Add(document);
        var captured = MapSceneSource.Capture(document, Closure);
        Assert.Equal(MapSceneSourceStatus.Ready, captured.Status);
        var proofs = MapRenderExtentValidator.Validate(Closure, captured.Source!.RequiredAssetIds, new(new())).Proofs!;
        return new(captured.Source.WithRenderExtents(proofs).Source!, Closure, proofs, Policy(), windows);
    }
    internal static void Complete(MapRenderResidency residency, ResidencyScheduler scheduler)
    {
        residency.Update(); scheduler.RunAll();
        for (int i = 0; i < 64; i++)
        {
            var state = residency.Update();
            if (state.Transition.Status != MapSceneStatus.Incomplete)
            { Assert.Equal(MapSceneStatus.CompleteForScope, state.Transition.Status); return; }
            scheduler.RunAll();
        }
        Assert.Fail("The bounded preparation/upload schedule did not complete.");
    }
    internal static MapDrawSet Draw(MapRenderResidency residency)
    {
        var recorder = new MapDrawRecorder(); var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
        residency.Record(new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 1, 1, point), recorder);
        return recorder.Snapshot();
    }
    public void Dispose() { foreach (var source in _sources) source.Dispose(); }
}
