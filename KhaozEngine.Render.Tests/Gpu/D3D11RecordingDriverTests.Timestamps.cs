using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    public sealed partial class D3D11RecordingDriverTests : IDisposable
    {
        readonly object _timestampLock = new();
        readonly D3D11FenceSubsystem _timestampFences;
        readonly List<D3D11TimestampPool> _timestampPools = new();

        public D3D11RecordingDriverTests()
            => _timestampFences = new D3D11FenceSubsystem(new D3D11TimestampTestTimeline(), _timestampLock);

        D3D11CommandRecorder<D3D11StreamEmitter> CreateDeferred()
            => D3D11CommandDrivers.CreateDeferred(_timestampFences);

        IGpuCommandList CreateRecording<TEmitter>(D3D11RecordMode mode, TEmitter emitter)
            where TEmitter : struct, ID3D11Emitter
            => D3D11CommandDrivers.Create(mode, emitter, _timestampFences);

        IGpuTimestampPool CreateFrameTimestamps()
        {
            var pool = new D3D11TimestampPool(new D3D11TimestampTestApi(), _timestampFences, _timestampLock, 2);
            _timestampPools.Add(pool);
            return pool;
        }

        public void Dispose()
        {
            foreach (D3D11TimestampPool pool in _timestampPools) pool.Dispose();
            _timestampFences.Dispose();
        }
    }
}
