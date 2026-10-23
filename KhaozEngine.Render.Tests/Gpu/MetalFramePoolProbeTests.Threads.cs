using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu.Metal.Diagnostics;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class MetalFramePoolProbeTests
{
    [Fact]
    public void Worker_thread_keeps_standalone_safety_while_another_thread_batches()
    {
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        MetalFramePoolCounters batched = default, standalone = default;
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var native = new Native();
                var before = MetalFramePoolProbe.Counters;
                using (MetalFramePoolProbe.Enter(true))
                {
                    entered.Set();
                    Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
                    using var local = ObjCAutoreleasePool.Enter();
                }
                batched = Delta(before);
            }
            catch (Exception error) { workerError = error; entered.Set(); }
        });
        worker.Start();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            using var native = new Native();
            var before = MetalFramePoolProbe.Counters;
            using (ObjCAutoreleasePool.Enter()) { }
            standalone = Delta(before);
        }
        finally { finished.Set(); worker.Join(); }
        Assert.Null(workerError);
        Assert.Equal(new(1, 1, 1, 1), batched);
        Assert.Equal(new(1, 1, 0, 0), standalone);
    }

    [Fact]
    public async Task Probe_state_does_not_flow_to_a_task_even_when_execution_context_flows()
    {
        Task<MetalFramePoolCounters> task;
        using (var native = new Native())
        using (MetalFramePoolProbe.Enter(true))
        task = Task.Run(() =>
        {
            using var workerNative = new Native();
            var before = MetalFramePoolProbe.Counters;
            using (ObjCAutoreleasePool.Enter()) { }
            return Delta(before);
        });
        Assert.Equal(new(1, 1, 0, 0), await task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Warm_scopes_and_counter_reads_allocate_zero_managed_bytes(bool enabled)
    {
        using var native = new Native { Record = false };
        static MetalFramePoolCounters Run(bool mode)
        {
            using (MetalFramePoolProbe.Enter(mode)) { using var local = ObjCAutoreleasePool.Enter(); }
            return MetalFramePoolProbe.Counters;
        }
        Run(enabled);
        long bytes;
        var before = MetalFramePoolProbe.Counters;
        MetalFramePoolCounters end;
        using (AllocationRegion.Start("frame pool scopes and counter reads"))
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            end = Run(enabled);
            bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        }
        Assert.Equal(0, bytes);
        Assert.Equal(before.NativePushCount + 1, end.NativePushCount);
        Assert.Equal(before.NativePopCount + 1, end.NativePopCount);
    }
}
