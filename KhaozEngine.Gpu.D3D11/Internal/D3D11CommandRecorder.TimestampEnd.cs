using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal sealed partial class D3D11CommandRecorder<TEmitter> : IGpuCommandListTimestampEnd
    {
        D3D11TimestampPool? _timestampEndPool;
        uint _timestampEndIndex;
        ulong _timestampRecordingId;

        public void FinishTimestampsOnEnd(IGpuTimestampPool pool, uint finalIndex)
        {
            D3D11TimestampPool native = RequireTimestampPool(pool);
            if (_timestampEndPool is not null)
                throw new GpuTimestampStateException(GpuTimestampRefusal.TailAlreadyArmed, "A final timestamp tail is already armed for this recording.") { Backend = GpuBackendKind.Direct3D11Native };
            native.ValidateTimestampEnd(this, _timestampRecordingId, finalIndex);
            _timestampEndPool = native;
            _timestampEndIndex = finalIndex;
        }

        void RefuseArmedTimestampPool(D3D11TimestampPool pool, [System.Runtime.CompilerServices.CallerMemberName] string member = "")
        {
            if (ReferenceEquals(pool, _timestampEndPool))
                throw new GpuTimestampStateException(GpuTimestampRefusal.ManualFinalizationWhileArmed, "The timestamp pool's final marker and batch end are armed for End.") { Backend = GpuBackendKind.Direct3D11Native, Command = member };
        }

        void EmitTimestampEnd()
        {
            if (_timestampEndPool is not { } pool) return;
            pool.WriteRecording(this, _timestampEndIndex);
            _emitter.WriteTimestamp(pool, _timestampEndIndex);
            pool.EndRecording(this);
            _emitter.EndTimestamps(pool);
        }

        void BeginTimestampRecording()
        {
            ClearTimestampEnd();
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
