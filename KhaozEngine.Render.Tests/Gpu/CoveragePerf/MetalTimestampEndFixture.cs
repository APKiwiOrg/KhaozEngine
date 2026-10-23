using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

internal sealed class MetalTimestampEndFixture : IDisposable
{
    internal readonly MetalRingHarness Harness = new();
    internal readonly MetalTimestampTestNative Native;
    internal readonly FakeMetalCommandBufferSource Buffers = new();
    internal readonly FakeMetalEncoderCalls Calls = new();
    internal readonly FakeMetalRenderCalls Render = new();
    internal readonly MetalUncommittedBuffers Uncommitted = new(3, new RecordingLogger());
    internal readonly MetalCommandList List;
    internal readonly MetalTimestampPool Pool;
    internal readonly MetalSetupCommands Setup;

    internal MetalTimestampEndFixture(bool stage = true, bool capability = true)
    {
        Native = new MetalTimestampTestNative { Stage = stage, Blit = !stage };
        Pool = NewPool();
        List = Harness.NewList(new object(), Buffers, Calls, Uncommitted, render: Render,
            timestampTimeline: capability ? Harness.Timeline : null);
        Setup = new MetalSetupCommands(new FakeMetalSetupNative(), Harness.Liveness);
    }

    internal MetalTimestampPool NewPool() => new(new MetalTimestampApi(Native), Harness.Timeline, Harness.Liveness, 3);
    internal void Start() { List.Begin(); List.BeginTimestamps(Pool, 2); List.WriteTimestamp(Pool, 0); }
    internal void Arm(uint finalIndex = 1) => Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(List).FinishTimestampsOnEnd(Pool, finalIndex);
    internal void PendingClear()
    {
        List.Passes.SetFramebuffer(new MetalBoundFramebuffer(1, 16, 16,
            new[] { new MetalAttachment(new IntPtr(1), GpuPixelFormat.R8G8B8A8UNorm) }, default, false));
        List.Passes.ClearColourTarget(0, Color.Black);
    }
    internal void Submit() => MetalGpuDevice.PrepareForCommit(List, Setup, alive: true);
    public void Dispose() { List.Dispose(); Pool.Dispose(); Setup.Dispose(); Harness.Dispose(); }
}
