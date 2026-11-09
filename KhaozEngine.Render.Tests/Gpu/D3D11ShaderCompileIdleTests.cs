using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Background shader compiles allocate on worker threads, and nothing joins a discarded shader's compile.
/// An allocation window that opens beside one can lose its no-GC region, so the window waits for them.</summary>
[Collection("AllocSensitive")]
public sealed class D3D11ShaderCompileIdleTests
{
    [Fact]
    public void WaitForIdle_RefusesWhileACompileIsOutstanding_AndReturnsOnceItFinishes()
    {
        using var release = new ManualResetEventSlim();
        var compile = D3D11ShaderCompilation<int>.Start(() => { release.Wait(); return 7; });

        Assert.Throws<TimeoutException>(() => D3D11ShaderCompileWorkers.WaitForIdleForTests(TimeSpan.Zero));

        release.Set();
        D3D11ShaderCompileWorkers.WaitForIdleForTests(TimeSpan.FromSeconds(60));
        Assert.Equal(7, compile.Join());
    }

    [Fact]
    public void WaitForIdle_IncludesPreparationThatDependsOnACompile()
    {
        using var release = new ManualResetEventSlim();
        bool prepared = false;
        var compile = D3D11ShaderCompilation<int>.Start(() => { release.Wait(); return 3; });
        var preparation = compile.Select(value => { Volatile.Write(ref prepared, true); return value * 2; });

        release.Set();
        D3D11ShaderCompileWorkers.WaitForIdleForTests(TimeSpan.FromSeconds(60));

        Assert.True(Volatile.Read(ref prepared));
        Assert.Equal(6, preparation.Join());
    }

    [Fact]
    public async Task AnAllocationWindowOpensOnlyAfterOutstandingCompilesFinish()
    {
        using var release = new ManualResetEventSlim();
        var compile = D3D11ShaderCompilation<int>.Start(() => { release.Wait(); return 1; });

        Task<AllocationRegion> open = Task.Run(() => AllocationRegion.Start("outstanding compile probe"));
        Assert.False(ReferenceEquals(open, await Task.WhenAny(open, Task.Delay(250))),
            "The allocation window opened while a shader compile was still outstanding.");

        release.Set();
        using AllocationRegion region = await open;
        Assert.Equal(1, compile.Join());
    }

    [Fact]
    public void WaitForIdle_LeavesWorkOnACallersOwnSchedulerToThatCaller()
    {
        var compile = D3D11ShaderCompilation<int>.Start(() => 1, scheduler: new NeverRunsScheduler());
        _ = compile.Select(value => value);

        Assert.Null(Record.Exception(() => D3D11ShaderCompileWorkers.WaitForIdleForTests(TimeSpan.FromSeconds(60))));
    }

    sealed class NeverRunsScheduler : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];
        protected override void QueueTask(Task task) { }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
    }
}
