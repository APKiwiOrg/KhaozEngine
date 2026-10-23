using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanCommandList : IGpuCommandListTimestampEnd
    {
        VulkanTimestampRecording? _timestampEndRecording;
        uint _timestampEndIndex;
        ulong _timestampRecordingId;

        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            VulkanTimestampPool native = RequireTimestampPool(pool);
            if (_timestampEndRecording is not null)
                throw new InvalidOperationException("A final timestamp tail is already armed for this recording.");
            native.ValidateTimestampEnd(this, _timestampRecordingId, CurrentBuffer, finalIndex);
            _timestampEndRecording = native.Recording;
            _timestampEndIndex = finalIndex;
        }

        void RefuseArmedTimestampPool(VulkanTimestampPool pool)
        {
            if (ReferenceEquals(pool.Recording, _timestampEndRecording))
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
            _timestampEndRecording = null;
            _timestampEndIndex = 0;
        }
    }
}
