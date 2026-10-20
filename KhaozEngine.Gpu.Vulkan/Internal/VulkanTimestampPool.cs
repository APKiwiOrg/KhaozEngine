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
        readonly ulong[] _values;
        readonly ulong[] _availability;
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
            VulkanTimestampRecording.RequireDeviceLive(timeline);
            Capacity = capacity;
            _validBits = validBits;
            _period = tickPeriodNanoseconds;
            _values = new ulong[capacity];
            _availability = new ulong[capacity];
            Recording = new VulkanTimestampRecording(api, timeline, _state) { Handle = api.CreatePool(capacity) };
        }

        public uint Capacity { get; }
        internal VulkanTimestampRecording Recording { get; }

        internal void RequireOwner(VulkanTimeline timeline)
        {
            Recording.RequireLive();
            if (!ReferenceEquals(_timeline, timeline))
                throw new ArgumentException("The timestamp pool belongs to another Vulkan device.", "pool");
        }

        internal void Begin(ulong commandBuffer, uint count, VulkanCommandList list, ulong recordingId)
        {
            Recording.Begin(commandBuffer, count, list, recordingId);
            _resolved = null;
            _api.Reset(commandBuffer, Recording.Handle, count);
        }

        internal void ValidateTimestampEnd(VulkanCommandList list, ulong recordingId, ulong commandBuffer, uint finalIndex)
            => Recording.ValidateTimestampEnd(list, recordingId, commandBuffer, finalIndex);

        internal void Write(ulong commandBuffer, uint index)
            => Recording.Write(commandBuffer, index);

        internal void End(ulong commandBuffer)
            => Recording.End(commandBuffer);

        public GpuTimestampStatus TryResolve(IGpuFence completion, Span<ulong> ticks, out double tickPeriodNanoseconds)
        {
            tickPeriodNanoseconds = 0d;
            Recording.RequireLive();
            ArgumentNullException.ThrowIfNull(completion);
            if (!Recording.Ended) throw new InvalidOperationException("End a timestamp batch before resolving it.");
            int count = (int)_state.Count;
            if (ticks.Length < count) throw new ArgumentException("The output cannot hold the timestamp batch.", nameof(ticks));
            if (completion is not VulkanGpuFence fence)
                throw new ArgumentException("Use this Vulkan device's completion fence.", nameof(completion));
            fence.RequireOwner(_timeline);
            if (_resolved is null)
            {
                if (!fence.Signaled) return GpuTimestampStatus.Pending;
                // A semaphore poll can itself latch device loss. Do not query a dead device afterwards.
                Recording.RequireLive();
                Span<ulong> values = _values.AsSpan(0, count);
                Span<ulong> availability = _availability.AsSpan(0, count);
                availability.Clear();
                if (!_api.TryRead(Recording.Handle, _state.Count, values, availability)) return GpuTimestampStatus.Pending;
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
            if (Recording.IsDisposed) return;
            Recording.IsDisposed = true;
            _owner.RetireTerminal(() => _api.DestroyPool(Recording.Handle));
        }
    }
}
