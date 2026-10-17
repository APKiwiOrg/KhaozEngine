using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace KhaozEngine.Tests;

/// <summary>Proves the allocation measurement contract in every assembly that links the shared region.</summary>
[Collection("AllocSensitive")]
public sealed class AllocationIsolationContractTests
{
    [Fact]
    public void A_measurement_in_a_serial_collection_starts()
    {
        using var region = AllocationRegion.Start("serial contract probe");
        Assert.Equal(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
    }

    [Fact]
    public void A_copy_of_this_assembly_in_another_load_context_sees_the_same_isolation()
    {
        // Some measurements run in a fresh load context to reset statics, as AmbientFacadeAllocationTests does.
        var context = new AssemblyLoadContext("allocation isolation probe", isCollectible: true);
        try
        {
            var copy = context.LoadFromAssemblyPath(typeof(AllocationIsolationContractTests).Assembly.Location);
            var probe = copy.GetType(typeof(AllocationIsolationContractTests).FullName!, throwOnError: true)!
                .GetMethod(nameof(IsolationSeenHere), BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.Equal((true, true), ((bool, bool))probe.Invoke(null, null)!);
        }
        finally { context.Unload(); }
    }

    static (bool RunsAlone, bool Reported) IsolationSeenHere() =>
        (CollectionIsolation.RunsAlone, SerializedMessageBus.AssemblyDelivered > 0);

    [Fact]
    public void This_assembly_reports_through_the_serialized_bus() =>
        Assert.True(SerializedMessageBus.AssemblyDelivered > 0,
            "No test message reached the serialized bus, so the assembly is not running under SerializedReportingTestFramework.");
}

/// <summary>Runs in its own default collection, which xUnit runs beside the other parallel collections.</summary>
public sealed class AllocationIsolationParallelContractTests
{
    [Fact]
    public void A_default_collection_runs_alone_only_when_the_whole_assembly_runs_serially() =>
        Assert.Equal(CollectionIsolation.AssemblySerial, CollectionIsolation.RunsAlone);

    [Fact]
    public void A_measurement_outside_an_isolated_collection_is_refused_before_it_starts()
    {
        // A serial runner isolates this collection too, so take the flag down on this test's own flow to prove the
        // refusal under either runner mode.
        bool runnerDecision = CollectionIsolation.RunsAlone;
        CollectionIsolation.RunsAlone = false;
        AllocationRegion? started = null;
        try
        {
            Exception? error = Record.Exception(() => started = AllocationRegion.Start("parallel contract probe"));
            Assert.Null(started);
            Assert.Contains("DisableParallelization = true", Assert.IsType<XunitException>(error).Message);
            Assert.NotEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
        }
        finally
        {
            // A region that should never have started must not outlive this test and refuse every later one.
            if (started is not null)
            {
                try { started.Dispose(); }
                catch (XunitException) { }
            }

            CollectionIsolation.RunsAlone = runnerDecision;
        }
    }

    [Fact]
    public void The_serialized_bus_reports_on_the_thread_that_queued_the_message()
    {
        var sink = new RecordingSink();
        using var bus = new SerializedMessageBus(sink, stopOnFail: false);
        Assert.True(bus.QueueMessage(new DiagnosticMessage("probe")));
        Assert.Equal(Environment.CurrentManagedThreadId, Assert.Single(sink.Threads));
    }

    [Fact]
    public async Task Concurrent_reports_reach_the_sink_one_at_a_time()
    {
        using var secondQueued = new ManualResetEventSlim();
        var sink = new RecordingSink(onFirstMessage: () =>
        {
            // Hold the first delivery until the second thread is queueing, then give it time to overlap.
            secondQueued.Wait(TimeSpan.FromSeconds(10));
            Thread.Sleep(50);
        });
        using var bus = new SerializedMessageBus(sink, stopOnFail: false);
        var first = Task.Run(() => bus.QueueMessage(new DiagnosticMessage("first")));
        SpinWait.SpinUntil(() => sink.Entered > 0, TimeSpan.FromSeconds(10));
        var second = Task.Run(() =>
        {
            secondQueued.Set();
            return bus.QueueMessage(new DiagnosticMessage("second"));
        });
        await Task.WhenAll(first, second);
        Assert.Equal(2, sink.Threads.Count);
        Assert.Equal(1, sink.MaxConcurrent);
    }

    sealed class RecordingSink : LongLivedMarshalByRefObject, IMessageSink
    {
        readonly Action? onFirstMessage;
        int concurrent, maxConcurrent, entered;

        internal RecordingSink(Action? onFirstMessage = null) => this.onFirstMessage = onFirstMessage;

        internal ConcurrentQueue<int> Threads { get; } = new();
        internal int MaxConcurrent => Volatile.Read(ref maxConcurrent);
        internal int Entered => Volatile.Read(ref entered);

        public bool OnMessage(IMessageSinkMessage message)
        {
            int now = Interlocked.Increment(ref concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref maxConcurrent)) &&
                   Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }
            if (Interlocked.Increment(ref entered) == 1) onFirstMessage?.Invoke();
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            Interlocked.Decrement(ref concurrent);
            return true;
        }
    }
}
