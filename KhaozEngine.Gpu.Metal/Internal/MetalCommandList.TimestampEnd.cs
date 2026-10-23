using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal sealed partial class MetalCommandList : IGpuCommandListTimestampEnd
    {
        MetalTimestampPool? _timestampEndPool;
        uint _timestampEndIndex;
        ulong _timestampRecordingId;

        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            MetalTimestampPool native = RequireTimestampPool(pool);
            if (_timestampEndPool is not null)
                throw new InvalidOperationException("A final timestamp tail is already armed for this recording.");
            native.ValidateTimestampEnd(this, _timestampRecordingId, _commandBuffer, finalIndex);
            _timestampEndPool = native;
            _timestampEndIndex = finalIndex;
        }

        void RefuseArmedTimestampPool(MetalTimestampPool pool)
        {
            if (ReferenceEquals(pool, _timestampEndPool))
                throw new InvalidOperationException("The timestamp pool's final marker and batch end are armed for End.");
        }

        void EmitTimestampEnd()
        {
            if (_timestampEndPool is not { } pool) return;
            // These are the ordinary Part A writes. Their dedicated blits close before returning, so no
            // encoder opened by the tail can survive the seal. End already holds the member's autorelease pool.
            WriteTimestampCore(pool, _timestampEndIndex);
            EndTimestampsCore(pool);
        }

        void BeginTimestampRecording()
        {
            ClearTimestampEnd();
            // Native handles can repeat even though Metal command buffers are single-use.
            unchecked { _timestampRecordingId++; }
        }

        void ClearTimestampEnd()
        {
            // Forget list ownership only. Unresolved pools remain quarantined through teardown.
            _timestampEndPool = null;
            _timestampEndIndex = 0;
        }
    }
}
