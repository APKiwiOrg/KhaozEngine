using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Diagnostics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc;

[Collection("AllocSensitive")]
public sealed class NativeCapacityDemandWorkerAssetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedAllowance_ReportsPreviouslyAdmittedPieceOnRefusal(bool parts)
    {
        var closure = NativeRenderAssetFixtures.Create();
        var budget = new GltfDecodeBudget(new());
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], budget).Proofs!;
        using var assets = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget).Assets!;
        long bytes = assets.RetainedBytes;
        var allowance = new MapRenderAssetAllowance(new(MaxParts: parts ? 10 : 100, MaxDecodedBytes: parts ? long.MaxValue : bytes + 1));
        using var probe = new CapacityDemandProbe();
        allowance.Record(assets);
        var rejected = MapRenderAssets.LoadSelected(closure, ["prop-a"], proofs, budget, allowance.Remaining);
        Assert.NotNull(rejected.Capacity);
        var error = allowance.Refuse(rejected.Capacity!);
        string limit = parts ? "MaxParts" : "MaxDecodedBytes";
        var admitted = Assert.Single(probe.For(limit), r => Read<string>(r, "Site") == "MapRenderAssetAllowance." + limit + ".1");
        // The origin-aware decode gate publishes the one refusal. The translation adds no second event.
        var refusal = Assert.Single(probe.For(limit), r => Read<bool>(r, "Refused"));
        Assert.Equal("MapRenderAssets." + limit + ".1", Read<string>(refusal, "Site"));
        // The selected set's admitted piece, six parts or its retained bytes, plus the refused load's own total within
        // the remaining view, which already counts any earlier tier of the same piece.
        decimal held = parts ? 6 : bytes;
        Assert.Equal((held + rejected.Capacity!.Requested, parts ? 10 : bytes + 1, true),
            (Read<decimal>(refusal, "Requested"), Read<decimal>(refusal, "Allowance"), Read<bool>(refusal, "Refused")));
        Assert.True(Read<decimal>(refusal, "CurrentDemand") >= held);
        Assert.Equal(Read<long>(admitted, "AllowanceId"), Read<long>(refusal, "AllowanceId"));
        Assert.Equal(held + rejected.Capacity!.Requested, error.Capacity!.Requested);
    }

    [Fact]
    public async Task PreparationWorker_InheritsRootAndParentAndProbeSnapshotsAreStable()
    {
        using var probe = new CapacityDemandProbe();
        var owner = new object();
        int caller = Environment.CurrentManagedThreadId, worker = caller;
        using var root = CapacityDemandDiagnostics.Enter(owner, "root");
        CapacityDemandDiagnostics.Check(owner, "root-marker", "MaxWorkers", 0, 1, 2, false);
        var before = probe.Events;
        using var parent = CapacityDemandDiagnostics.Enter(owner, "parent");
        CapacityDemandDiagnostics.Check(owner, "parent-marker", "MaxWorkers", 0, 1, 2, false);
        var queue = new MapRenderBuildQueue(new(), new(), new PreparationScheduler());
        var job = queue.Enqueue(1, 0, _ =>
        {
            worker = Environment.CurrentManagedThreadId;
            using var scope = CapacityDemandDiagnostics.Enter(owner, "preparation-worker");
            CapacityDemandDiagnostics.Check(owner, "worker-marker", "MaxWorkers", 0, 1, 2, false);
            return Array.Empty<MapRenderPlan>();
        }, new EmptyLease());
        await job.Completion;
        queue.Remove(job);
        Assert.NotEqual(caller, worker);
        Assert.Single(before);
        var rows = probe.Events;
        var rootRow = Assert.Single(rows, r => Read<string>(r, "Site") == "root-marker");
        var parentRow = Assert.Single(rows, r => Read<string>(r, "Site") == "parent-marker");
        var workerRow = Assert.Single(rows, r => Read<string>(r, "Site") == "worker-marker");
        Assert.Equal(Read<long>(rootRow, "OperationId"), Read<long>(workerRow, "RootOperationId"));
        Assert.Equal(Read<long>(parentRow, "OperationId"), Read<long>(workerRow, "ParentOperationId"));
        Assert.Equal(Read<long>(rootRow, "AdmissionId"), Read<long>(workerRow, "AdmissionId"));
    }

    sealed class PreparationScheduler : TaskScheduler
    {
        protected override System.Collections.Generic.IEnumerable<Task> GetScheduledTasks() => Array.Empty<Task>();
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task) => new Thread(() => TryExecuteTask(task)).Start();
    }

    sealed class EmptyLease : IDisposable { public void Dispose() { } }
}
