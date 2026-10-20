using System;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Shared timestamp batch validation. Increasing indices need only a fixed cursor, not per-frame storage.
    /// Backends call Resolved when consuming Ready or Invalid, and retain that result until Begin.
    /// </summary>
    internal sealed class GpuTimestampPoolState
    {
        readonly uint _capacity;
        uint _nextIndex;
        Phase _phase;

        internal GpuTimestampPoolState(uint capacity)
        {
            if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
        }

        internal uint Count { get; private set; }

        internal void Begin(uint count)
        {
            if (count == 0 || count > _capacity) throw new ArgumentOutOfRangeException(nameof(count));
            if (_phase is Phase.Recording or Phase.Pending)
                throw new InvalidOperationException("The timestamp pool already has an unresolved batch.");
            Count = count;
            _nextIndex = 0;
            _phase = Phase.Recording;
        }

        internal void Write(uint index)
        {
            if (_phase != Phase.Recording)
                throw new InvalidOperationException("Begin a timestamp batch before writing queries.");
            if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index != _nextIndex)
                throw new InvalidOperationException("Timestamp indices must be written exactly once in increasing order.");
            _nextIndex++;
        }

        internal void ValidateTimestampEnd(uint finalIndex)
        {
            if (_phase != Phase.Recording)
                throw new InvalidOperationException("Begin a timestamp batch before arming its final timestamp.");
            if (finalIndex != Count - 1)
                throw new ArgumentOutOfRangeException(nameof(finalIndex), "The final timestamp must use the batch's last index.");
            if (_nextIndex != finalIndex)
                throw new InvalidOperationException("Write every earlier timestamp index, leaving the final index unwritten, before arming the tail.");
        }

        internal void End()
        {
            if (_phase != Phase.Recording || _nextIndex != Count)
                throw new InvalidOperationException("Write every timestamp index before ending the batch.");
            _phase = Phase.Pending;
        }

        internal void Resolved()
        {
            if (_phase is not (Phase.Pending or Phase.Resolved))
                throw new InvalidOperationException("End a timestamp batch before resolving it.");
            _phase = Phase.Resolved;
        }

        enum Phase { Idle, Recording, Pending, Resolved }
    }
}
