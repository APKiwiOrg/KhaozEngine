using System;
using System.Runtime;
using Xunit.Sdk;

namespace KhaozEngine.Tests;

/// <summary>Protects synchronous allocation counters from collection-time allocation-context reconciliation.
/// The budget is process-wide, so a window opens only in a collection that runs alone, and the assembly reports
/// through <see cref="SerializedReportingTestFramework"/> so no reporter backlog drains into it.</summary>
internal sealed class AllocationRegion : IDisposable
{
    internal const long DefaultBudget = 4 * 1024 * 1024;
    static readonly object StartGate = new();
    readonly string description;
    readonly long budget;
    long processBefore, threadBefore, reportedBefore;
    int gen0, gen1, gen2;
    bool active;

    AllocationRegion(string description, long budget)
    {
        this.description = description;
        this.budget = budget;
    }

    internal static AllocationRegion Start(string description, long totalSize = DefaultBudget, bool enabled = true)
    {
        var region = new AllocationRegion(description, totalSize);
        if (!enabled) return region;
        if (!CollectionIsolation.RunsAlone)
            throw new XunitException($"{description}: allocation measurement must run in a collection defined with " +
                "DisableParallelization = true, in an assembly that imports test-shared/AllocationMeasurement.props. " +
                "The no-GC budget is process-wide, so a concurrent test invalidates the window.");
        lock (StartGate)
        {
            // A failed nested runtime start can discard the outer region's bookkeeping.
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion)
                throw new XunitException($"{description}: allocation measurement could not start because another no-GC region is active.");
            bool started;
            try { started = GC.TryStartNoGCRegion(totalSize, disallowFullBlockingGC: false); }
            catch (InvalidOperationException error)
            {
                throw new XunitException($"{description}: allocation measurement could not start its no-GC region. {error.Message}");
            }
            if (!started)
                throw new XunitException($"{description}: allocation measurement could not reserve its {totalSize}-byte process-wide no-GC budget.");
        }
        region.active = true;
        region.gen0 = GC.CollectionCount(0);
        region.gen1 = GC.CollectionCount(1);
        region.gen2 = GC.CollectionCount(2);
        region.reportedBefore = ReportedMessages();
        region.processBefore = GC.GetTotalAllocatedBytes(precise: true);
        region.threadBefore = GC.GetAllocatedBytesForCurrentThread();
        return region;
    }

    static long ReportedMessages() => SerializedMessageBus.AssemblyDelivered ?? 0;

    public void Dispose()
    {
        if (!active) return;
        active = false;
        int collections0 = GC.CollectionCount(0) - gen0;
        int collections1 = GC.CollectionCount(1) - gen1;
        int collections2 = GC.CollectionCount(2) - gen2;
        bool intact = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
        long processBytes = GC.GetTotalAllocatedBytes(precise: true) - processBefore;
        long threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        long reported = ReportedMessages() - reportedBefore;
        string? runtimeReason = null;
        try { GC.EndNoGCRegion(); }
        catch (InvalidOperationException error) { runtimeReason = error.Message; }
        if (runtimeReason is null && intact && collections0 == 0 && collections1 == 0 && collections2 == 0 &&
            processBytes <= budget) return;
        string cause = processBytes >= budget ? "the process-wide allocation budget was exhausted" :
            collections0 != 0 || collections1 != 0 || collections2 != 0 ? "a garbage collection interrupted the window" :
            "the no-GC region ended before the window completed";
        throw new XunitException($"{description}: invalid allocation measurement because {cause}. " +
            $"Budget={budget} bytes, process={processBytes} bytes, thread={threadBytes} bytes, " +
            $"other threads={processBytes - threadBytes} bytes, test messages reported in the window={reported}, " +
            $"collections(gen0/gen1/gen2)={collections0}/{collections1}/{collections2}. Runtime reason: {runtimeReason ?? "none"}");
    }
}
