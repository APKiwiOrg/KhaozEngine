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
                throw new GpuTimestampStateException(GpuTimestampRefusal.UnresolvedBatch, "The timestamp pool already has an unresolved batch.") { Count = Count };
            Count = count;
            _nextIndex = 0;
            _phase = Phase.Recording;
        }

        internal void Write(uint index)
        {
            if (_phase != Phase.Recording)
                throw new GpuTimestampStateException(GpuTimestampRefusal.BatchNotRecording, "Begin a timestamp batch before writing queries.") { Command = nameof(Write) };
            if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
            if (index != _nextIndex)
                throw new GpuTimestampStateException(GpuTimestampRefusal.IndexOutOfSequence, "Timestamp indices must be written exactly once in increasing order.") { Index = index, ExpectedIndex = _nextIndex, Count = Count };
            _nextIndex++;
        }

        internal void ValidateTimestampEnd(uint finalIndex)
        {
            if (_phase != Phase.Recording)
                throw new GpuTimestampStateException(GpuTimestampRefusal.BatchNotRecording, "Begin a timestamp batch before arming its final timestamp.") { Command = nameof(ValidateTimestampEnd) };
            if (finalIndex != Count - 1)
                throw new GpuTimestampRangeException(GpuTimestampRefusal.FinalIndexRequired, nameof(finalIndex), null, "The final timestamp must use the batch's last index.") { FinalIndex = Count - 1, Count = Count };
            if (_nextIndex != finalIndex)
                throw new GpuTimestampStateException(GpuTimestampRefusal.TailPrefixIncomplete, "Write every earlier timestamp index, leaving the final index unwritten, before arming the tail.") { ExpectedIndex = _nextIndex, FinalIndex = finalIndex, Count = Count };
        }

        internal void End()
        {
            if (_phase != Phase.Recording || _nextIndex != Count)
                throw new GpuTimestampStateException(GpuTimestampRefusal.IncompleteBatch, "Write every timestamp index before ending the batch.") { WrittenCount = _nextIndex, Count = Count };
            _phase = Phase.Pending;
        }

        internal void Resolved()
        {
            if (_phase is not (Phase.Pending or Phase.Resolved))
                throw new GpuTimestampStateException(GpuTimestampRefusal.BatchNotEnded, "End a timestamp batch before resolving it.") { Command = nameof(Resolved), Count = Count };
            _phase = Phase.Resolved;
        }

        enum Phase { Idle, Recording, Pending, Resolved }
    }
}
