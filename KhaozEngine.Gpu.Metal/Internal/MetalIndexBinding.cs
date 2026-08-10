using System;
using System.Globalization;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE BOUND INDEX BUFFER AND THE ONE PIECE OF ARITHMETIC AN INDEXED DRAW DOES, which is the whole of what
    /// <c>SetIndexBuffer</c> leaves behind on this backend. Work-breakdown row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580).
    ///
    /// <para><b>IT IS NOT A BIND RECORD AND IT HAS NO <see cref="MetalEncoderMark"/>, WHICH IS THE ONE CACHE IN
    /// THIS BACKEND AN ENCODER BOUNDARY DOES NOT INVALIDATE.</b> Everything else a draw needs (the argument
    /// tables, the vertex streams, the pipeline-state block, the viewport, the scissor) is ENCODER state and dies
    /// at every boundary under M-R4. An index buffer is not: Metal takes it, its offset and its element width in
    /// <c>-drawIndexedPrimitives:</c> ITSELF, so it never reaches an argument table and there is nothing on the
    /// encoder for a boundary to discard. Section 6.3 records the same fact from the other side, as the reason
    /// this backend has no index-buffer dirty record where both siblings do. What is left is RECORDER state, and
    /// recorder state is reset by a <c>Begin</c> and by nothing else.</para>
    ///
    /// <para><b>THE ARITHMETIC IS THE INCUMBENT'S, MINUS A TERM THIS SEAM CANNOT PRODUCE.</b>
    /// <c>MTLCommandList.DrawIndexedCore</c> computes <c>(indexSize * indexStart) + _ibOffset</c>, where
    /// <c>_ibOffset</c> comes from Veldrid's own three-argument <c>SetIndexBuffer</c> overload. The GPU seam here
    /// declares <c>SetIndexBuffer(buffer, format)</c> with no offset at all, so that term is structurally zero and
    /// is NOT carried as a field that only ever holds one value. <see cref="OffsetFor"/> is the rest of it, and it
    /// is a device-free assertion rather than a line inside a native call because getting it wrong draws a
    /// DIFFERENT mesh out of a shared index buffer, with no error anywhere.</para>
    ///
    /// <para><b>THERE IS NO USAGE CHECK, unlike the Vulkan sibling's.</b> That backend refuses a buffer created
    /// without <c>VK_BUFFER_USAGE_INDEX_BUFFER_BIT</c> because binding one is a validation error there. Metal has
    /// no such bit: an <c>MTLBuffer</c> is bytes, and <c>-drawIndexedPrimitives:</c> reads whichever ones it is
    /// pointed at. Inventing a refusal the API does not have would make a call legal on the incumbent and refused
    /// on its own replacement, which is the divergence class the whole golden gate exists to prevent.</para>
    /// </summary>
    internal struct MetalIndexBinding
    {
        IntPtr _buffer;
        MTLIndexType _indexType;
        bool _bound;

        /// <summary>The <c>MTLBuffer</c> an indexed draw names, or <see cref="IntPtr.Zero"/> when none is bound
        /// or the bound one has since been disposed.</summary>
        internal readonly IntPtr Buffer => _buffer;

        /// <summary>How wide one element is, which travels in the draw call.</summary>
        internal readonly MTLIndexType IndexType => _indexType;

        /// <summary>Whether <c>SetIndexBuffer</c> has been called in this recording. Distinct from
        /// <see cref="Buffer"/> being non-nil, because a bound buffer disposed since answers nil and the two
        /// mistakes deserve different messages.</summary>
        internal readonly bool IsBound => _bound;

        /// <summary>Whether an indexed draw can be issued: something was bound AND its handle is still live.
        /// </summary>
        internal readonly bool IsDrawable => _bound && _buffer != IntPtr.Zero;

        /// <summary>How many bytes one index of <paramref name="type"/> occupies.</summary>
        internal static uint ElementBytes(MTLIndexType type) => type == MTLIndexType.UInt16 ? 2u : 4u;

        /// <summary>The seam's index format as Metal's. Both enums carry exactly the two members, so this is
        /// total and there is no unmappable arm to refuse.</summary>
        internal static MTLIndexType ToIndexType(GpuIndexFormat format)
            => format == GpuIndexFormat.UInt16 ? MTLIndexType.UInt16 : MTLIndexType.UInt32;

        /// <summary>
        /// RECORD ONLY. No native call, because there is no native call to make: the binding is read at the draw.
        /// </summary>
        /// <param name="buffer">The <c>MTLBuffer</c> handle, or <see cref="IntPtr.Zero"/> for a disposed
        /// buffer.</param>
        /// <param name="indexType">The element width.</param>
        internal void Record(IntPtr buffer, MTLIndexType indexType)
        {
            _buffer = buffer;
            _indexType = indexType;
            _bound = true;
        }

        /// <summary>Forget the binding, from <c>MetalCommandList.Begin</c>'s one reset block and from nowhere
        /// else.</summary>
        internal void Reset()
        {
            _buffer = IntPtr.Zero;
            _indexType = default;
            _bound = false;
        }

        /// <summary>
        /// The byte offset an indexed draw starting at element <paramref name="indexStart"/> passes as
        /// <c>indexBufferOffset</c>.
        /// <para>
        /// IT WIDENS BEFORE IT MULTIPLIES. A 32-bit index buffer with more than 2^30 elements would overflow a
        /// <see cref="uint"/> product on the way to an <c>NSUInteger</c> argument that has room for it, and an
        /// overflowed offset points somewhere inside the buffer rather than past it, so the draw succeeds and
        /// reads the wrong indices. No shipped mesh is near that, which is exactly why it would go unnoticed.
        /// </para>
        /// </summary>
        internal readonly nuint OffsetFor(uint indexStart) => (nuint)((ulong)ElementBytes(_indexType) * indexStart);

        /// <summary>
        /// The refusal an indexed draw owes before it reaches the encoder, or null when there is none.
        /// <para>
        /// TWO STATES REACH IT AND THEY ARE DIFFERENT MISTAKES. Nothing bound is a recording that called
        /// <c>DrawIndexed</c> without <c>SetIndexBuffer</c>. A nil handle is a buffer that WAS bound and has been
        /// disposed since, which <c>MetalBuffer.Handle</c> answers nil for deliberately. Both would reach
        /// <c>-drawIndexedPrimitives:</c> with a nil <c>indexBuffer</c>, which is a driver-side assertion that
        /// aborts the process rather than anything this backend could report.
        /// </para>
        /// </summary>
        internal readonly string? DrawRefusal()
        {
            if (IsDrawable) return null;

            if (!_bound)
            {
                return "An indexed draw was recorded on a native Metal command list with no index buffer bound. "
                    + "Call SetIndexBuffer first. Metal takes the index buffer IN the draw call rather than "
                    + "binding it beforehand, so there is no argument table holding a stale one and nothing to "
                    + "fall back to: the call would name a nil MTLBuffer, which is a driver-side assertion that "
                    + "aborts the process rather than an error this backend can report.";
            }

            return "An indexed draw was recorded on a native Metal command list whose bound index buffer has "
                + "been disposed. Its MTLBuffer has been released, so the handle this recording holds is nil and "
                + "the draw would name a nil index buffer, which is a driver-side assertion rather than an error "
                + "this backend can report. Bind a live buffer, or keep the one this draw uses alive until the "
                + "recording is submitted.";
        }

        /// <summary>A short description for a message, so a refusal can say what was bound without holding the
        /// buffer.</summary>
        internal readonly string Describe()
            => _bound
                ? "a " + (ElementBytes(_indexType) * 8).ToString(CultureInfo.InvariantCulture)
                    + "-bit index buffer"
                : "no index buffer";
    }
}
