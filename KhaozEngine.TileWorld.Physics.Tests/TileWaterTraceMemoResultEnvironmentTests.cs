using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Numerics;
using KhaozEngine.Locomotion;
using System.Threading;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterTraceMemoResultEnvironmentTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FourClaimedThreadsReplayAndTheUnclaimedFifthRecomputes(bool flat)
    {
        var field = TileWaterTraceMemoResultTests.Field(flat);
        var work = new int[5];
        var errors = new Exception?[5];
        using var ready = new Barrier(5);
        var threads = Enumerable.Range(0, 5).Select(i => new Thread(() =>
        {
            try
            {
                TileWaterTraceMemoResultTests.Check(field, TileWaterTraceMemoResultTests.Query(i));
                ready.SignalAndWait();
                work[i] = TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, TileWaterTraceMemoResultTests.Query(i)));
            }
            catch (Exception error) { errors[i] = error; ready.RemoveParticipant(); }
        })).ToArray();
        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        Assert.All(errors, Assert.Null);
        Assert.Equal(4, work.Count(value => value == 0));
        Assert.Single(work, value => value > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ControlsAreReadAfreshAndAnUnsupportedReaderNeverReplays(bool flat)
    {
        var field = TileWaterTraceMemoResultTests.Field(flat);
        var q = TileWaterTraceMemoResultTests.Query();
        var previous = TileMediumSampler.ControlReaderForTests;
        ulong token = 7;
        bool available = true;
        TileMediumSampler.ControlReaderForTests = (out ulong value) => { value = token; return available; };
        try
        {
            Read(false);
            Read(true);
            token++;
            Read(false);
            Read(true);
            available = false;
            Read(false);
            Read(false);
            available = true;
            Read(true);
        }
        finally { TileMediumSampler.ControlReaderForTests = previous; }
        void Read(bool hit) => Assert.Equal(hit, TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)) == 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmFlatAndLargeReplaysAllocateNoManagedStorage(bool flat)
    {
        var field = TileWaterTraceMemoResultTests.Field(flat);
        var q = TileWaterTraceMemoResultTests.Query();
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var identity = MovementWaterFieldTests.Identity;
        field.TraceWater(q, Vector3.Zero, identity, spans, contacts);
        field.TraceWater(q, Vector3.Zero, identity, spans, contacts);
        MovementCoverageResult result = default;
        AllocationMeasurement.NoPerCallAllocation("a warm flat or large exact-result replay", () =>
            result = field.TraceWater(q, Vector3.Zero, identity, spans, contacts));
        Assert.Equal(MovementAvailability.Known, result.Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiscardedSamplerAndResultStorageAreCollectedTogether(bool flat)
    {
        var references = Release(flat);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.False(references.Sampler.IsAlive);
        Assert.False(references.Table.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (WeakReference Sampler, WeakReference Table) Release(bool flat)
    {
        var field = TileWaterTraceMemoResultTests.Field(flat);
        TileWaterTraceMemoResultTests.Check(field, TileWaterTraceMemoResultTests.Query());
        var table = field.CaptureResultMemoTableForTests();
        Assert.NotNull(table);
        return (new(field), table);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void NativeControlChangesMatchTheIndependentFlatAndPrismEvaluators(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null || !TileMediumSampler.TryReadTraceControls(out _)) return;
        Assert.True(environment.Set("nearest"));
        foreach (bool flat in new[] { false, true })
        {
            var field = TileWaterTraceMemoResultTests.Field(flat);
            var q = TileWaterTraceMemoResultTests.Query();
            TileWaterTraceMemoResultTests.Check(field, q);
            Assert.Equal(0, TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)));
            if (!environment.Set(mode)) { output.WriteLine($"Unavailable independent mode {mode}"); continue; }
            Assert.True(TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)) > 0);
            Assert.Equal(0, TileWaterTraceMemoResultTests.Work(TileWaterTraceMemoResultTests.Check(field, q)));
            Assert.True(environment.Set("nearest"));
        }
    }
}
