using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11ShaderCompilationTests
{
    [Fact]
    public void Start_QueuesBeforeJoinAndPreservesCompilerBytes()
    {
        var scheduler = new HeldScheduler();
        byte[] bytes = [0x44, 0x58, 0x42, 0x43, 1, 2, 3];
        bool compiled = false;
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() =>
        {
            compiled = true;
            return bytes;
        }, scheduler: scheduler);

        Assert.Equal(1, scheduler.QueuedCount);
        Assert.False(compiled);
        scheduler.RunNext();
        Assert.True(compiled);
        Assert.Same(bytes, compilation.Join());
        Assert.Same(bytes, compilation.Join());
    }

    [Fact]
    public async Task IndependentPrograms_OverlapAndRespectTheWorkerBound()
    {
        var pool = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2);
        using var release = new ManualResetEventSlim();
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int thirdEntered = 0;
        byte[] Compile(byte value)
        {
            int count = Interlocked.Increment(ref active);
            if (count == 2) bothEntered.SetResult();
            release.Wait();
            Interlocked.Decrement(ref active);
            return [value];
        }

        var first = D3D11ShaderCompilation<byte[]>.Start(() => Compile(1), scheduler: pool.ConcurrentScheduler);
        var second = D3D11ShaderCompilation<byte[]>.Start(() => Compile(2), scheduler: pool.ConcurrentScheduler);
        try
        {
            await bothEntered.Task;
            var third = D3D11ShaderCompilation<byte[]>.Start(() =>
            {
                Interlocked.Increment(ref thirdEntered);
                return new byte[] { 3 };
            }, scheduler: pool.ConcurrentScheduler);
            Assert.Equal(2, Volatile.Read(ref active));
            Assert.Equal(0, Volatile.Read(ref thirdEntered));
            release.Set();
            Assert.Equal(new byte[] { 1 }, first.Join());
            Assert.Equal(new byte[] { 2 }, second.Join());
            Assert.Equal(new byte[] { 3 }, third.Join());
        }
        finally
        {
            release.Set();
            pool.Complete();
            await pool.Completion;
        }
    }

    [Fact]
    public void FailedCompile_RethrowsTheOriginalValidationExceptionAtJoin()
    {
        var scheduler = new HeldScheduler();
        var failure = new ShaderValidationException("program: FXC rejected HLSL");
        var compilation = D3D11ShaderCompilation<byte[]>.Start(() => throw failure, scheduler: scheduler);
        scheduler.RunNext();

        Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => compilation.Join()));
        Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => compilation.Join()));
    }

    [Fact]
    public void ConcurrentRequestsForOneCacheKey_ShareOneCompileAndWrite()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-d3d11-single-flight-" + Guid.NewGuid().ToString("N"));
        var scheduler = new HeldScheduler();
        var cache = new D3D11DxbcCache(directory);
        byte[] bytes = [0x44, 0x58, 0x42, 0x43, 7];
        int writes = 0;
        byte[] Compile()
        {
            Assert.True(cache.TryWrite("stage", bytes));
            writes++;
            return bytes;
        }
        try
        {
            var first = D3D11ShaderCompilation<byte[]>.Start(Compile, directory + "/program", scheduler);
            var second = D3D11ShaderCompilation<byte[]>.Start(Compile, directory + "/program", scheduler);
            Assert.Same(first, second);
            Assert.Equal(1, scheduler.QueuedCount);
            scheduler.RunNext();
            Assert.Same(bytes, first.Join());
            Assert.Same(bytes, second.Join());
            Assert.Equal(bytes, cache.TryRead("stage"));
            Assert.Equal(1, writes);

            // Completed flights are evicted. The next request checks the disk again rather than retaining DXBC forever.
            var next = D3D11ShaderCompilation<byte[]>.Start(() => cache.TryRead("stage")!, directory + "/program", scheduler);
            Assert.NotSame(first, next);
            scheduler.RunNext();
            Assert.Equal(bytes, next.Join());
            Assert.Equal(1, writes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CacheOff_DoesNotShareCompiles_AndFailedFlightsCanBeRecompiled()
    {
        var scheduler = new HeldScheduler();
        string key = Guid.NewGuid().ToString("N");
        var failed = D3D11ShaderCompilation<byte[]>.Start(() => throw new ShaderValidationException("bad"), key, scheduler);
        scheduler.RunNext();
        Assert.Throws<ShaderValidationException>(() => failed.Join());
        var recovered = D3D11ShaderCompilation<byte[]>.Start(() => [9], key, scheduler);
        var uncached = D3D11ShaderCompilation<byte[]>.Start(() => [8], scheduler: scheduler);
        var otherUncached = D3D11ShaderCompilation<byte[]>.Start(() => [7], scheduler: scheduler);
        Assert.NotSame(uncached, otherUncached);
        Assert.Equal(3, scheduler.QueuedCount);
        scheduler.RunNext();
        scheduler.RunNext();
        scheduler.RunNext();
        Assert.Equal(new byte[] { 9 }, recovered.Join());
        Assert.Equal(new byte[] { 8 }, uncached.Join());
        Assert.Equal(new byte[] { 7 }, otherUncached.Join());
    }

    [Fact]
    public void Diagnostics_FollowTheScheduledCompileEvenAfterTheCallerScopeEnds()
    {
        var scheduler = new HeldScheduler();
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        D3D11ShaderCompilation<byte[]> compilation;
        using (diagnostics.Use())
            compilation = D3D11ShaderCompilation<byte[]>.Start(() =>
            {
                using var measurement = D3D11ShaderCompileDiagnostics.MeasureCompile("key", "program", D3D11ShaderStage.Fragment, "abc");
                return [1];
            }, scheduler: scheduler);
        scheduler.RunNext();
        Assert.Equal(new byte[] { 1 }, compilation.Join());
        var record = Assert.Single(diagnostics.GetRecords());
        Assert.Equal("key", record.SourceKey);
        Assert.Equal(3, record.HlslByteLength);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", record.HlslSha256);
        Assert.False(record.CacheHit);
    }

    [Fact]
    public void WindowsWorkers_PreserveTheSynchronousGraphicsAndComputeDxbc()
    {
        if (!KhaozEngineD3D11.IsPlatformSupported) return;
        VerifyWindowsWorkerDxbc();
    }

    [SupportedOSPlatform("windows")]
    static void VerifyWindowsWorkerDxbc()
    {
        const string vertex = "#version 450\nlayout(location=0) in vec3 P; void main(){gl_Position=vec4(P,1);}";
        const string fragment = "#version 450\nlayout(location=0) out vec4 C; void main(){C=vec4(1);}";
        const string compute = "#version 450\nlayout(local_size_x=2,local_size_y=3,local_size_z=4) in; void main(){}";
        const uint flags = D3D11ShaderDebug.Optimized;
        var baselinePair = D3D11ShaderBuild.Pair(vertex, fragment, flags, null);
        var baselineCompute = D3D11ShaderBuild.Compute(compute, flags, null);
        var pair = D3D11ShaderCompilation<D3D11CompiledPair>.Start(
            () => D3D11ShaderBuild.Pair(vertex, fragment, flags, null));
        var kernel = D3D11ShaderCompilation<D3D11CompiledCompute>.Start(
            () => D3D11ShaderBuild.Compute(compute, flags, null));

        var actualPair = pair.Join();
        var actualCompute = kernel.Join();
        Assert.Equal(baselinePair.VertexDxbc, actualPair.VertexDxbc);
        Assert.Equal(baselinePair.FragmentDxbc, actualPair.FragmentDxbc);
        Assert.Equal(baselineCompute.Dxbc, actualCompute.Dxbc);
        Assert.Equal(2u, actualCompute.ThreadGroupSizeX);
        Assert.Equal(3u, actualCompute.ThreadGroupSizeY);
        Assert.Equal(4u, actualCompute.ThreadGroupSizeZ);
    }

    sealed class HeldScheduler : TaskScheduler
    {
        readonly Queue<Task> queued = new();
        internal int QueuedCount => queued.Count;
        protected override IEnumerable<Task> GetScheduledTasks() => queued.ToArray();
        protected override void QueueTask(Task task) => queued.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        internal void RunNext() => Assert.True(TryExecuteTask(queued.Dequeue()));
    }
}
