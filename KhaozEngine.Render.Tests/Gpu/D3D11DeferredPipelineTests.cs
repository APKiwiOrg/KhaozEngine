using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11DeferredPipelineTests
{
    [Fact]
    public async Task PipelineBatch_StartsEveryCompileBeforeAnyJoin()
    {
        const int count = 4;
        using var entered = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        var pool = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, count);
        var liveness = new DeviceLiveness();
        var creation = new D3D11CreationGate(driverConcurrentCreates: false);
        int joins = 0;
        var compiles = new D3D11ShaderCompilation<byte[]>[count];
        var pipelines = new D3D11DeferredResource<byte[], Native>[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                byte value = (byte)i;
                var compile = compiles[i] = D3D11ShaderCompilation<byte[]>.Start(() =>
                {
                    entered.Signal();
                    release.Wait();
                    return [value];
                }, scheduler: pool.ConcurrentScheduler);
                pipelines[i] = new(liveness, () =>
                {
                    Assert.True(release.IsSet, "Pipeline creation must not join a pending compile.");
                    Assert.False(creation.IsEnteredByCurrentThread);
                    Interlocked.Increment(ref joins);
                    return compile.Join();
                }, bytes =>
                {
                    using var scope = creation.Enter();
                    Assert.True(creation.IsEnteredByCurrentThread);
                    return new Native(bytes);
                });
            }
            entered.Wait();
            Assert.Equal(0, joins);
            release.Set();
            for (int i = 0; i < count; i++)
            {
                var native = pipelines[i].Value;
                Assert.Equal(new byte[] { (byte)i }, native.Bytes);
                Assert.Same(native, pipelines[i].Value);
            }
            Assert.Equal(count, joins);
        }
        finally
        {
            release.Set();
            foreach (var compile in compiles.Where(c => c is not null)) compile.Join();
            foreach (var pipeline in pipelines.Where(p => p is not null)) pipeline.Dispose();
            pool.Complete();
            await pool.Completion;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeOrDeviceDeathDuringJoin_PreventsDeferredCreation(bool deviceDeath)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var liveness = new DeviceLiveness();
        int creates = 0;
        using var pipeline = new D3D11DeferredResource<byte[], Native>(liveness, () =>
        {
            entered.Set();
            release.Wait();
            return [1];
        }, bytes => { Interlocked.Increment(ref creates); return new Native(bytes); });
        var bind = Task.Run(() =>
        {
            try { return Assert.Throws<ObjectDisposedException>(() => pipeline.Value); }
            finally { entered.Set(); }
        });
        try
        {
            entered.Wait();
            if (deviceDeath) liveness.MarkDead();
            else pipeline.Dispose();
        }
        finally { release.Set(); }
        await bind;
        Assert.Equal(0, creates);
    }

    [Fact]
    public void FailureAtBind_KeepsItsTypeMessageAndIdentity()
    {
        var failure = new ShaderValidationException("program: original FXC diagnostic");
        var compile = D3D11ShaderCompilation<byte[]>.Start(() => throw failure);
        using var pipeline = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(), compile.Join,
            bytes => throw new InvalidOperationException("must not create"));
        Assert.Same(failure, Assert.Throws<ShaderValidationException>(() => pipeline.Value));
    }

    [Fact]
    public void DisposeBeforeBind_DoesNotPrepareAndReleasesRealizedStateOnlyOnce()
    {
        int prepares = 0;
        using var unused = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(),
            () => { prepares++; return [1]; }, bytes => new Native(bytes));
        unused.Dispose();
        Assert.Throws<ObjectDisposedException>(() => unused.Value);
        Assert.Equal(0, prepares);

        using var bound = new D3D11DeferredResource<byte[], Native>(new DeviceLiveness(),
            () => [2], bytes => new Native(bytes));
        var native = bound.Value;
        bound.Dispose();
        bound.Dispose();
        Assert.Equal(1, native.Disposals);
    }

    sealed class Native(byte[] bytes) : IDisposable
    {
        internal byte[] Bytes { get; } = bytes;
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
