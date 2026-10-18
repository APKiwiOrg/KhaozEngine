using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>One outstanding batch, calibrated hardware clocks and cached terminal results.</summary>
    internal sealed class MetalTimestampPool : IGpuTimestampPool
    {
        readonly IMetalTimestampApi _api;
        readonly MetalTimeline _timeline;
        readonly IDeviceLiveness _liveness;
        readonly GpuTimestampPoolState _state;
        readonly MetalTimestampResources _resources;
        readonly ulong[] _values;
        readonly bool _stageBoundary;
        ulong _cpuStart, _gpuStart, _eventValue;
        IntPtr _recordingBuffer;
        double _period;
        bool _ended, _disposed;
        GpuTimestampStatus? _resolved;

        internal MetalTimestampPool(IMetalTimestampApi api, MetalTimeline timeline, IDeviceLiveness liveness, uint capacity)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));
            _state = new GpuTimestampPoolState(capacity);
            RequireLive();
            if (!api.SupportsTimestampCounters) throw new NotSupportedException("This Metal device has no usable timestamp counters.");
            Capacity = capacity;
            _stageBoundary = api.SupportsStageBoundary;
            _values = new ulong[checked((int)capacity)];
            _resources = api.CreatePool(capacity, _stageBoundary);
        }

        public uint Capacity { get; }

        internal void RequireOwner(MetalTimeline? timeline, IDeviceLiveness liveness)
        {
            RequireLive();
            if (!ReferenceEquals(_timeline, timeline) || !ReferenceEquals(_liveness, liveness))
                throw new ArgumentException("The timestamp pool belongs to another Metal device.", "pool");
        }

        internal void Begin(IntPtr commandBuffer, uint count)
        {
            RequireLive();
            _state.Begin(count);
            _resolved = null;
            _ended = false;
            _period = 0d;
            _recordingBuffer = commandBuffer;
            _api.SampleClocks(out _cpuStart, out _gpuStart);
        }

        internal void Write(IntPtr commandBuffer, uint index)
        {
            RequireRecording(commandBuffer);
            ulong before = checked(_eventValue + 1), after = checked(_eventValue + 2);
            _state.Write(index);
            _eventValue = after;
            _api.WriteBoundary(commandBuffer, _resources, index, before, after, _stageBoundary);
        }

        internal void End(IntPtr commandBuffer)
        {
            RequireRecording(commandBuffer);
            _state.End();
            _api.Resolve(commandBuffer, _resources, _state.Count);
            _ended = true;
        }

        public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds)
        {
            tickPeriodNanoseconds = 0d;
            RequireLive();
            ArgumentNullException.ThrowIfNull(completion);
            if (!_ended) throw new InvalidOperationException("End a timestamp batch before resolving it.");
            int count = (int)_state.Count;
            if (ticks.Length < count) throw new ArgumentException("The output cannot hold the timestamp batch.", nameof(ticks));
            if (completion is not MetalGpuFence fence)
                throw new ArgumentException("Use this Metal device's completion fence.", nameof(completion));
            fence.RequireOwner(_timeline);
            if (_resolved is null)
            {
                bool signaled = fence.Signaled;
                RequireLive(); // A completion poll can discover a command-buffer error.
                if (!signaled) return GpuTimestampStatus.Pending;
                _api.SampleClocks(out ulong cpuEnd, out ulong gpuEnd);
                RequireLive();
                bool valid = cpuEnd > _cpuStart && gpuEnd > _gpuStart;
                Span<ulong> values = _values.AsSpan(0, count);
                if (valid)
                {
                    _api.CopyResolved(_resources, _state.Count, values);
                    for (int i = 0; i < count; i++)
                        valid &= values[i] != 0 && values[i] != ulong.MaxValue
                            && values[i] >= _gpuStart && values[i] <= gpuEnd
                            && (i == 0 || values[i] >= values[i - 1]);
                    _period = (double)(cpuEnd - _cpuStart) / (gpuEnd - _gpuStart);
                    valid &= double.IsFinite(_period) && _period > 0d;
                    if (valid) valid = GpuTimestampResults.TryNormalize(values, 64, values);
                }
                _resolved = valid ? GpuTimestampStatus.Ready : GpuTimestampStatus.Invalid;
                if (!valid) _period = 0d;
                _state.Resolved();
            }
            if (_resolved == GpuTimestampStatus.Ready)
            {
                _values.AsSpan(0, count).CopyTo(ticks);
                tickPeriodNanoseconds = _period;
            }
            return _resolved.Value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _api.DestroyPool(_resources); // Metal command buffers retain referenced resources until completion.
        }

        void RequireLive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_liveness.IsDead || _timeline.IsDeviceDead)
                throw new InvalidOperationException("The native Metal timestamp device is disposed or lost.");
        }

        void RequireRecording(IntPtr commandBuffer)
        {
            RequireLive();
            if (_ended || commandBuffer == IntPtr.Zero || commandBuffer != _recordingBuffer)
                throw new InvalidOperationException("Write and end a timestamp batch in the command buffer that began it.");
        }
    }
}
