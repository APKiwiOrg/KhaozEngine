using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11ComputePipelineLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipelineDisposalDuringCompileJoin_RefusesBeforeNativeShaderCreation(bool explicitLiveness)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var scheduler = new HeldJoinScheduler(entered, release);
        byte[] bytes = [0x44, 0x58, 0x42, 0x43, 1];
        var preparation = D3D11ShaderCompilation<D3D11PreparedCompute>.Start(
            () => new(new(bytes, 8, 4, 2), null), scheduler: scheduler);
        var liveness = new DeviceLiveness();
        var handle = new NativeHandle();
        int creates = 0;
        using var shader = new D3D11ComputeShader(liveness, preparation, _ =>
        {
            Interlocked.Increment(ref creates);
            return handle;
        });
        var description = new GpuComputePipelineDescription(shader);
        using var pipeline = explicitLiveness
            ? new D3D11ComputePipeline(liveness, description)
            : new D3D11ComputePipeline(description);
        var bind = Task.Run(() => Record.Exception(() =>
        {
            _ = ((ID3D11ComputePipelineState)pipeline).ComputeShader;
        }));
        try
        {
            entered.Wait();
            Assert.False(bind.IsCompleted);
            pipeline.Dispose();
            Assert.True(pipeline.IsDisposed);
            Assert.False(shader.IsDisposed);
            Assert.True(liveness.IsAlive);
        }
        finally { release.Set(); }

        Assert.IsType<ObjectDisposedException>(await bind);
        Assert.Equal(0, creates);
        Assert.Equal(bytes, shader.Bytecode.ToArray());
        Assert.Same(handle, shader.ShaderHandle);
        Assert.Equal(1, creates);
        pipeline.Dispose();
        Assert.Equal(0, handle.Disposals);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BoundPipeline_ShaderDisposalOrDeviceDeathRefusesAnotherBind(bool explicitLiveness, bool deviceDeath)
    {
        var liveness = new DeviceLiveness();
        var handle = new NativeHandle();
        using var shader = new D3D11ComputeShader(liveness, handle, [1], 8, 4, 2);
        var description = new GpuComputePipelineDescription(shader);
        using var pipeline = explicitLiveness
            ? new D3D11ComputePipeline(liveness, description)
            : new D3D11ComputePipeline(description);
        var state = (ID3D11ComputePipelineState)pipeline;
        Assert.Same(handle, state.ComputeShader);

        if (deviceDeath) liveness.MarkDead();
        else shader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => state.ComputeShader);
    }

    // The compilation scheduler seam holds the consumer inside its first join, before executing the compile.
    sealed class HeldJoinScheduler(ManualResetEventSlim entered, ManualResetEventSlim release) : TaskScheduler
    {
        protected override IEnumerable<Task> GetScheduledTasks() => [];
        protected override void QueueTask(Task task) { }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            entered.Set();
            release.Wait();
            return TryExecuteTask(task);
        }
    }

    sealed class NativeHandle : IDisposable
    {
        internal int Disposals { get; private set; }
        public void Dispose() => Disposals++;
    }
}
