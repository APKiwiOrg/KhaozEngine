using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>A fixed native query pool, one unresolved batch and cached terminal results.</summary>
    internal sealed class VulkanTimestampPool : IGpuTimestampPool
    {
        readonly IVulkanTimestampApi _api;
        readonly VulkanTimeline _timeline;
        readonly VulkanResourceOwner _owner;
        readonly GpuTimestampPoolState _state;
        readonly int _validBits;
        readonly double _period;
        readonly ulong _handle;
        readonly ulong[] _values;
        readonly ulong[] _availability;
        ulong _recordingBuffer;
        bool _ended;
        bool _disposed;
        GpuTimestampStatus? _resolved;

        internal VulkanTimestampPool(IVulkanTimestampApi api, VulkanTimeline timeline, VulkanResourceOwner owner,
            uint capacity, int validBits, double tickPeriodNanoseconds)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (!ReferenceEquals(timeline, owner.Timeline))
                throw new ArgumentException("A timestamp pool must use its resource owner's timeline.", nameof(timeline));
            _state = new GpuTimestampPoolState(capacity);
            if (!new VulkanTimestampSupport(validBits, tickPeriodNanoseconds).IsSupported)
                throw new NotSupportedException("This Vulkan queue has no usable timestamp counter.");
            RequireLive();
            Capacity = capacity;
            _validBits = validBits;
            _period = tickPeriodNanoseconds;
            _values = new ulong[capacity];
            _availability = new ulong[capacity];
            _handle = api.CreatePool(capacity);
        }

        public uint Capacity { get; }

        internal void RequireOwner(VulkanTimeline timeline)
        {
            RequireLive();
            if (!ReferenceEquals(_timeline, timeline))
                throw new ArgumentException("The timestamp pool belongs to another Vulkan device.", "pool");
        }

        internal void Begin(ulong commandBuffer, uint count)
        {
            RequireLive();
            _state.Begin(count);
            _resolved = null;
            _ended = false;
            _recordingBuffer = commandBuffer;
            _api.Reset(commandBuffer, _handle, count);
        }

        internal void Write(ulong commandBuffer, uint index)
        {
            RequireRecording(commandBuffer);
            _state.Write(index);
            _api.Write(commandBuffer, _handle, index);
        }

        internal void End(ulong commandBuffer)
        {
            RequireRecording(commandBuffer);
            _state.End();
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
            if (completion is not VulkanGpuFence fence)
                throw new ArgumentException("Use this Vulkan device's completion fence.", nameof(completion));
            fence.RequireOwner(_timeline);
            if (_resolved is null)
            {
                if (!fence.Signaled) return GpuTimestampStatus.Pending;
                // A semaphore poll can itself latch device loss. Do not query a dead device afterwards.
                RequireLive();
                Span<ulong> values = _values.AsSpan(0, count);
                Span<ulong> availability = _availability.AsSpan(0, count);
                availability.Clear();
                if (!_api.TryRead(_handle, _state.Count, values, availability)) return GpuTimestampStatus.Pending;
                for (int i = 0; i < count; i++)
                    if (availability[i] == 0) return GpuTimestampStatus.Pending;
                _resolved = GpuTimestampResults.TryNormalize(values, _validBits, values)
                    ? GpuTimestampStatus.Ready : GpuTimestampStatus.Invalid;
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
            _owner.RetireTerminal(() => _api.DestroyPool(_handle));
        }

        void RequireLive()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_timeline.IsDeviceDead)
                throw new InvalidOperationException("The native Vulkan timestamp device is disposed or lost.");
        }

        void RequireRecording(ulong commandBuffer)
        {
            RequireLive();
            if (_ended || commandBuffer != _recordingBuffer)
                throw new InvalidOperationException("Write and end a timestamp batch in the command buffer that began it.");
        }
    }
}
