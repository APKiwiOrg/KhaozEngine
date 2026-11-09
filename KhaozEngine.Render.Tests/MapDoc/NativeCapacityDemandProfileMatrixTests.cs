using System;
using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>One accepted and one refused evaluation per residency, asset and decode profile field. Each expectation
/// comes from the test's own ceiling and a fixture fact, never from the event under test.</summary>
[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandProfileMatrixTests
{
    static object[] At(CapacityDemandProbe probe, string site, string limit) =>
        probe.For(limit).Where(r => Read<string>(r, "Site") == site).ToArray();

    static void Row(object row, decimal current, decimal increment, decimal allowance, bool refused)
    {
        Assert.Equal(current, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(increment, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(allowance, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
    }

    [Fact]
    public void QueueWorkersPlansAndBytes()
    {
        using var probe = new CapacityDemandProbe();
        var scheduler = new ResidencyScheduler();
        var workers = new MapRenderBuildQueue(new(MaxWorkers: 1), new(), scheduler);
        Assert.True(workers.CanEnqueue(1, 0));
        workers.Enqueue(1, 0, _ => [], new Lease());
        Assert.False(workers.CanEnqueue(1, 0));
        var plans = new MapRenderBuildQueue(new(MaxWorkers: 2, MaxQueuedPlans: 3), new(), scheduler);
        Assert.True(plans.CanEnqueue(2, 0));
        plans.Enqueue(2, 0, _ => [], new Lease());
        Assert.False(plans.CanEnqueue(2, 0));
        var bytes = new MapRenderBuildQueue(new(MaxWorkers: 2, MaxQueuedBytes: 10), new(), scheduler);
        Assert.True(bytes.CanEnqueue(1, 7));
        bytes.Enqueue(1, 7, _ => [], new Lease());
        Assert.False(bytes.CanEnqueue(1, 4));
        var w = At(probe, "MapRenderBuildQueue.Workers", "MaxWorkers").Where(r => Read<decimal>(r, "Allowance") == 1).ToArray();
        Row(w[0], 0, 1, 1, false);
        Row(w[^1], 1, 1, 1, true);
        var p = At(probe, "MapRenderBuildQueue.Plans", "MaxQueuedPlans").Where(r => Read<decimal>(r, "Allowance") == 3).ToArray();
        Row(p[0], 0, 2, 3, false);
        Row(p[^1], 2, 2, 3, true);
        var b = At(probe, "MapRenderBuildQueue.Bytes", "MaxQueuedBytes").Where(r => Read<decimal>(r, "Allowance") == 10).ToArray();
        Row(b[0], 0, 7, 10, false);
        Row(b[^1], 7, 4, 10, true);
    }

    [Theory]
    [InlineData("MaxResidentGpuBytes", "MapRenderAdmission.MaxResidentGpuBytes.1", 60, 10, 1, 70)]
    [InlineData("MaxResidentCpuBytes", "MapRenderAdmission.MaxResidentCpuBytes.1", 0, 0, 7, 7)]
    [InlineData("MaxInFlightUploadCpuBytes", "MapRenderAdmission.MaxInFlightUploadCpuBytes.1", 0, 0, 7, 7)]
    [InlineData("MaxInFlightUploadStagingGpuBytes", "MapRenderAdmission.MaxInFlightUploadStagingGpuBytes.1", 0, 7, 0, 7)]
    public void UploadAdmission_EachResidentAndInFlightCeiling(string limit, string site, long owned, long staging, long cpu, int increment)
    {
        long ceiling = limit == "MaxResidentGpuBytes" ? 100 : 10;
        var admission = new MapRenderAdmission(new MapRenderBudgets(
            MaxResidentGpuBytes: limit == "MaxResidentGpuBytes" ? ceiling : 1000, MaxResidentCpuBytes: limit == "MaxResidentCpuBytes" ? ceiling : 1000,
            MaxInFlightUploadCpuBytes: limit == "MaxInFlightUploadCpuBytes" ? ceiling : 1000,
            MaxInFlightUploadStagingGpuBytes: limit == "MaxInFlightUploadStagingGpuBytes" ? ceiling : 1000));
        var demand = new SceneUploadDemand("payload", owned, staging, cpu, 0, 0);
        using var probe = new CapacityDemandProbe();
        Assert.NotNull(admission.TryReserve(demand));
        Assert.Null(admission.TryReserve(demand));
        var rows = At(probe, site, limit);
        Row(rows[0], 0, increment, ceiling, false);
        Row(rows[1], increment, increment, ceiling, true);
    }

    [Fact]
    public void ResidentCpuLeaseAndPlanWindow()
    {
        using var probe = new CapacityDemandProbe();
        var residency = new MapRenderAdmission(new MapRenderBudgets(MaxResidentCpuBytes: 10));
        using var lease = residency.TryReserveCpu(7)!;
        Assert.Null(residency.TryReserveCpu(4));
        var cpu = At(probe, "MapRenderAdmission.MaxResidentCpuBytes.2", "MaxResidentCpuBytes");
        Row(cpu[0], 0, 7, 10, false);
        Row(cpu[1], 7, 4, 10, true);
        // A residency build opens one MaxPlanBytes window per scene window inside its shared admission.
        var scene = new MapSceneAdmission(new(MaxPreparedBytes: 100));
        using var window = scene.LimitPreparation(10, nameof(MapRenderBudgets.MaxPlanBytes));
        using var owner = scene.Workspace();
        owner.Reserve(7);
        Assert.Throws<MapSceneCapacityException>(() => owner.Reserve(5));
        var plan = At(probe, "MapPreparationLedger.Window.Add", "MaxPlanBytes");
        Row(plan[0], 0, 7, 10, false);
        Row(plan[1], 7, 5, 10, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResidencyWindowCount(bool refused)
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window(), ResidencyFixture.Window(-3, 5));
        using var probe = new CapacityDemandProbe();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxActiveWindows: refused ? 1 : 2), new ResidencyScheduler());
        // A refused replacement becomes the residency's refused transition rather than a thrown exception.
        Assert.Equal(refused, residency.Update().Transition.Status == MapSceneStatus.CapacityExceeded);
        Row(At(probe, "MapRenderResidency.MaxActiveWindows.1", "MaxActiveWindows").First(), 0, 2, refused ? 1 : 2, refused);
    }

    [Fact]
    public void PerUpdateAppliesUploadsAndDisposals()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        using (var probe = new CapacityDemandProbe())
        {
            var scheduler = new ResidencyScheduler();
            using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxAppliesPerUpdate: 1, MaxUploadBytesPerUpdate: 40), scheduler);
            ResidencyFixture.Complete(residency, scheduler);
            // The window publishes more than one resource, so one apply per update defers the second.
            var applies = At(probe, "MapRenderResidency.Applies", "MaxAppliesPerUpdate");
            Row(applies[0], 0, 1, 1, false);
            Row(applies[1], 1, 1, 1, true);
            // Each test upload transfers 32 bytes, so the first update writes 32 of its 40.
            Row(At(probe, "MapRenderResidency.UploadBytes", "MaxUploadBytesPerUpdate")[0], 0, 32, 40, false);
        }
        using (var probe = new CapacityDemandProbe())
        {
            var scheduler = new ResidencyScheduler();
            using var residency = new MapRenderResidency(state, new OverwritingDevice(), new(MaxUploadBytesPerUpdate: 40), scheduler);
            for (int i = 0; i < 8; i++) { residency.Update(); scheduler.RunAll(); }
            // The device contract violation refuses the replacement at its first upload.
            Row(At(probe, "MapRenderResidency.UploadBytes", "MaxUploadBytesPerUpdate").Single(), 0, 41, 40, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            var scheduler = new ResidencyScheduler();
            using var residency = new MapRenderResidency(state, new ResidencyDevice(), new(MaxDisposalsPerUpdate: 1), scheduler);
            ResidencyFixture.Complete(residency, scheduler);
            residency.RequestReplacement(state with { Windows = [] });
            ResidencyFixture.Complete(residency, scheduler);
            var disposals = At(probe, "MapRenderResidency.Disposals", "MaxDisposalsPerUpdate");
            Assert.Contains(disposals, r => Read<decimal>(r, "CurrentDemand") == 0 && Read<decimal>(r, "ProposedIncrement") == 1 &&
                Read<decimal>(r, "Allowance") == 1 && !Read<bool>(r, "Refused"));
            Assert.Contains(disposals, r => Read<decimal>(r, "CurrentDemand") == 1 && Read<decimal>(r, "ProposedIncrement") == 1 &&
                Read<decimal>(r, "Allowance") == 1 && Read<bool>(r, "Refused"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SelectedAssetPartsBytesAndLods(bool parts)
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        using var assets = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget).Assets!;
        long bytes = assets.RetainedBytes;
        // The invented prop has six parts across its tiers.
        decimal piece = parts ? 6 : bytes, ceiling = 2 * piece - 1;
        var allowance = new MapRenderAssetAllowance(parts ? new(MaxParts: (int)ceiling) : new(MaxDecodedBytes: (long)ceiling));
        using var probe = new CapacityDemandProbe();
        allowance.Record(assets);
        Assert.Throws<MapRenderAssetRefusal>(() => allowance.Record(assets));
        string limit = parts ? "MaxParts" : "MaxDecodedBytes";
        var rows = At(probe, "MapRenderAssetAllowance." + limit + ".1", limit);
        Row(rows[0], 0, piece, ceiling, false);
        Row(rows[1], piece, piece, ceiling, true);
        if (!parts) return;
        int lods = 1 + closure.GetAsset("prop-a").LodResourceIds.Count;
        using var lodProbe = new CapacityDemandProbe();
        using (var accepted = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, new(MaxLods: lods)).Assets!) { }
        Assert.NotNull(MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, new(MaxLods: lods - 1)).Capacity);
        var tiers = At(lodProbe, "MapRenderAssets.MaxLods.1", "MaxLods");
        Row(tiers[0], 0, lods, lods, false);
        Row(tiers[1], 0, lods, lods - 1, true);
    }

    [Theory]
    [InlineData("MaxVertices", "GltfDecodeAdmission.MaxVertices.2", 6)]
    [InlineData("MaxIndices", "GltfDecodeAdmission.MaxIndices.1", 6)]
    [InlineData("MaxImagePixels", "GltfDecodeAdmission.MaxImagePixels.1", 6)]
    [InlineData("MaxImages", "GltfDecodeAdmission.MaxImages.1", 1)]
    public void DecodeCounts(string limit, string site, int demand)
    {
        // Two three-corner primitives, six corners and indices, and one 2 by 3 base colour image.
        byte[] glb = NativeRenderAssetFixtures.Glb("valid", 2, 3);
        GltfDecodeLimits Limits(int ceiling) => limit switch
        {
            "MaxVertices" => new(MaxVertices: ceiling),
            "MaxIndices" => new(MaxIndices: ceiling),
            "MaxImagePixels" => new(MaxImagePixels: ceiling),
            _ => new(MaxImages: ceiling),
        };
        using var probe = new CapacityDemandProbe();
        Assert.True(GltfDecodeAdmission.Measure(glb, new GltfDecodeBudget(Limits(demand))).Allowed);
        Assert.False(GltfDecodeAdmission.Measure(glb, new GltfDecodeBudget(Limits(demand - 1))).Allowed);
        var rows = At(probe, site, limit);
        Row(rows[0], 0, demand, demand, false);
        Row(rows[1], 0, demand, demand - 1, true);
    }

    [Fact]
    public void DecodePeakReservation()
    {
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 10));
        using var probe = new CapacityDemandProbe();
        Assert.True(budget.TryReserve(7, 0));
        Assert.False(budget.TryReserve(0, 4));
        var rows = At(probe, "GltfDecodeBudget.MaxPeakBytes.1", "MaxPeakBytes");
        Row(rows[0], 0, 7, 10, false);
        Row(rows[1], 7, 4, 10, true);
    }

    sealed class Lease : IDisposable { public void Dispose() { } }

    // Reports one byte more than the per-update allowance it was offered.
    sealed class OverwritingDevice : IMapRenderDevice
    {
        readonly ResidencyDevice _inner = new();
        public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => _inner.MeasureUpload(resource);
        public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation) =>
            new Upload(_inner.BeginUpload(resource, reservation));
        public void Release(MapRenderHandle handle) => _inner.Release(handle);
        sealed class Upload(IMapRenderUpload inner) : IMapRenderUpload
        {
            public bool Ready => inner.Ready;
            public long Advance(long transferBytes) { inner.Advance(transferBytes); return transferBytes + 1; }
            public MapRenderHandle Publish() => inner.Publish();
            public void Dispose() => inner.Dispose();
        }
    }
}
