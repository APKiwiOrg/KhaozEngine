using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal sealed partial class MetalCommandList
    {
        readonly MetalTimeline? _timestampTimeline;

        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            RequireTimestampPool(pool).Begin(_commandBuffer, count);
        }

        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            MetalTimestampPool timestamps = RequireTimestampPool(pool);
            _encoders.EnsureNoEncoder();
            try { timestamps.Write(_commandBuffer, index); }
            finally { _encoders.RecordExternalEncoderBoundary(); }
        }

        public void EndTimestamps(IGpuTimestampPool pool)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            MetalTimestampPool timestamps = RequireTimestampPool(pool);
            _encoders.EnsureNoEncoder();
            try { timestamps.End(_commandBuffer); }
            finally { _encoders.RecordExternalEncoderBoundary(); }
        }

        MetalTimestampPool RequireTimestampPool(IGpuTimestampPool pool)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Recording timestamps");
            ArgumentNullException.ThrowIfNull(pool);
            if (pool is not MetalTimestampPool timestamps)
                throw new ArgumentException("Use this Metal device's timestamp pool.", nameof(pool));
            timestamps.RequireOwner(_timestampTimeline, _liveness);
            return timestamps;
        }
    }
}
