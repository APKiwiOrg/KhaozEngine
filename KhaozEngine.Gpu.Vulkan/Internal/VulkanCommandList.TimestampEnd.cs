using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanCommandList : IGpuCommandListTimestampEnd
    {
        VulkanTimestampPool? _timestampEndPool;
        uint _timestampEndIndex;
        ulong _timestampRecordingId;

        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            if (_timestampEndPool is not null)
                throw new InvalidOperationException("A final timestamp tail is already armed for this recording.");
            native.ValidateTimestampEnd(this, _timestampRecordingId, CurrentBuffer, finalIndex);
            _timestampEndPool = native;
            _timestampEndIndex = finalIndex;
        }

        void RefuseArmedTimestampPool(VulkanTimestampPool pool)
        {
            if (ReferenceEquals(pool, _timestampEndPool))
                throw new InvalidOperationException("The timestamp pool's final marker and batch end are armed for End.");
        }

        void BeginTimestampRecording()
        {
            ClearTimestampEnd();
            // Native command-buffer handles repeat when the ring wraps. Identity includes the recording.
            unchecked { _timestampRecordingId++; }
        }

        void ClearTimestampEnd()
        {
            // Only forget list ownership. Failed or abandoned pools remain quarantined through teardown.
            _timestampEndPool = null;
            _timestampEndIndex = 0;
        }
    }
}
