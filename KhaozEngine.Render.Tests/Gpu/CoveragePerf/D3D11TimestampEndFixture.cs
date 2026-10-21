using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

internal sealed class D3D11TimestampEndFixture : IDisposable
{
    internal readonly object Gate = new();
    internal readonly D3D11TimestampTestApi Api = new();
    internal readonly D3D11TimestampTestTimeline Timeline = new();
    internal readonly D3D11NativeCallLog Log = new();
    internal readonly D3D11FenceSubsystem Fences;
    internal readonly D3D11TimestampPool Pool;
    internal readonly D3D11TimestampTestEmitter Emitter;
    internal readonly IGpuCommandList List;
    internal D3D11CommandRecorder<D3D11StreamEmitter>? Deferred => List as D3D11CommandRecorder<D3D11StreamEmitter>;
    internal bool IsSealed => Deferred?.IsSealed ?? ((D3D11CommandRecorder<D3D11TimestampTestEmitter>)List).IsSealed;
    internal bool IsRecording => Deferred?.IsRecording ?? ((D3D11CommandRecorder<D3D11TimestampTestEmitter>)List).IsRecording;

    internal D3D11TimestampEndFixture(bool deferred)
    {
        Fences = new D3D11FenceSubsystem(Timeline, Gate);
        Pool = NewPool();
        Emitter = new D3D11TimestampTestEmitter(Log, Api);
        List = D3D11CommandDrivers.Create(deferred ? D3D11RecordMode.Deferred : D3D11RecordMode.Immediate, Emitter, Fences);
    }

    internal D3D11TimestampPool NewPool() => new(Api, Fences, Gate, 3);
    internal void Start() { List.Begin(); List.BeginTimestamps(Pool, 2); List.WriteTimestamp(Pool, 0); }
    internal void Arm(uint finalIndex = 1) => Assert.IsAssignableFrom<IGpuCommandListTimestampEnd>(List).FinishTimestampsOnEnd(Pool, finalIndex);
    internal void Submit()
    {
        var emitter = Emitter;
        D3D11CommandDrivers.Submit(Gate, List, ref emitter);
    }
    public void Dispose() { List.Dispose(); Pool.Dispose(); Fences.Dispose(); }
}
