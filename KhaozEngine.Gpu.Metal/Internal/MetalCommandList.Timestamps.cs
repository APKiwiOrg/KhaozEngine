using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal sealed partial class MetalCommandList
    {
        readonly MetalTimeline? _timestampTimeline;
        readonly System.Collections.Generic.HashSet<MetalTimestampPool> _frameTimestampPools = [];

        public void BeginTimestamps(IGpuTimestampPool pool, uint count)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            MetalTimestampPool timestamps = RequireTimestampPool(pool);
            timestamps.Begin(_commandBuffer, count, this, _timestampRecordingId);
            _frameTimestampPools.Add(timestamps);
        }

        public void WriteTimestamp(IGpuTimestampPool pool, uint index)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            MetalTimestampPool timestamps = RequireTimestampPool(pool);
            RefuseArmedTimestampPool(timestamps);
            WriteTimestampCore(timestamps, index);
        }

        void WriteTimestampCore(MetalTimestampPool timestamps, uint index)
        {
            _encoders.EnsureNoEncoder();
            try { timestamps.Write(_commandBuffer, index); }
            catch
            {
                // Bookkeeping is still attempted, but its invariant check must not replace the native failure.
                try { _encoders.RecordExternalEncoderBoundary(); }
                catch { }
                throw;
            }
            _encoders.RecordExternalEncoderBoundary();
        }

        public void EndTimestamps(IGpuTimestampPool pool)
        {
            using var autorelease = ObjCAutoreleasePool.Enter();
            MetalTimestampPool timestamps = RequireTimestampPool(pool);
            RefuseArmedTimestampPool(timestamps);
            EndTimestampsCore(timestamps);
        }

        void EndTimestampsCore(MetalTimestampPool timestamps)
        {
            _encoders.EnsureNoEncoder();
            try { timestamps.End(_commandBuffer); }
            catch
            {
                try { _encoders.RecordExternalEncoderBoundary(); }
                catch { }
                throw;
            }
            _encoders.RecordExternalEncoderBoundary();
        }

        MetalTimestampPool RequireTimestampPool(IGpuTimestampPool pool, [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Recording timestamps");
            ArgumentNullException.ThrowIfNull(pool);
            if (_timestampTimeline is null) throw new GpuTimestampUnsupportedException(GpuTimestampRefusal.TimestampUnsupported, "This Metal command list has no timestamp device.") { Backend = GpuBackendKind.MetalNative, Command = member };
            if (pool is not MetalTimestampPool timestamps)
                throw new GpuTimestampArgumentException(GpuTimestampRefusal.ForeignPool, "Use this Metal device's timestamp pool.", nameof(pool)) { Backend = GpuBackendKind.MetalNative, ActualType = pool.GetType() };
            timestamps.RequireOwner(_timestampTimeline, _liveness);
            return timestamps;
        }
    }
}
