using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal sealed partial class D3D11CommandRecorder<TEmitter>
    {
        /// <summary>Seal only after all emission succeeds. A failed End abandons the recording.</summary>
        public void End()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_recording)
                throw new InvalidOperationException(
                    "End was called on a Direct3D 11 command list that is not recording. Every recording opens "
                    + "with Begin, which is what resets the stream.");

            try
            {
                EmitTimestampEnd();
                _emitter.End();
                IsSealed = true;
            }
            finally
            {
                _recording = false;
                ClearTimestampEnd();
                if (!IsSealed)
                {
                    _boundSwapchain = false;
                    // The abandoned deferred program must neither replay nor retain its resource arguments.
                    if (_emitter is D3D11StreamEmitter stream) stream.Stream.Reset();
                }
            }
        }
    }
}
