using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    internal sealed partial class D3D11GpuDevice
    {
        // Built once at creation beside the staging map path, so the read allocates nothing per call.
        readonly D3D11CompletedReads _completedReads;

        /// <summary>
        /// The seam's non-blocking staging read, refused on a device past teardown and otherwise
        /// <see cref="D3D11CompletedReads.TryReadBuffer"/>: a refusal pass, a TRIED immediate-context lock, a fence
        /// poll inside it and a <c>DO_NOT_WAIT</c> map, copy and unmap. Nothing on this path waits, drains,
        /// flushes or submits.
        /// </summary>
        public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        {
            // The liveness token flips LAST in teardown (decision X3), so a read racing the releases is refused on
            // the teardown flag rather than reaching a context that is being dismantled.
            ObjectDisposedException.ThrowIf(_tornDown, this);

            return _completedReads.TryReadBuffer(staging, completion, offsetBytes, destination);
        }
    }
}
