using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE BLIT-ENCODER-SCOPED COPY, behind an interface so the ROUTING above it is device-free: one
    /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>, which is what a record-time
    /// <c>UpdateBuffer</c> on a NON-uniform buffer emits (M-M8).
    ///
    /// <para><b>THIS IS NOT <see cref="IMetalEncoderSink"/> AND MUST NOT BECOME IT</b>, which is the same
    /// sentence <see cref="IMetalRenderApi"/> carries and the same reason. That seam exists to be COUNTED: M-T2
    /// names three call classes and freezes a budget over them, and a fourth member would quietly change what the
    /// budget means. The class that matters about this copy IS already counted, and it is counted there: the
    /// ENCODER BOUNDARY the copy forces is emitted through <see cref="IMetalEncoderSink.BeginBlitEncoder"/>, so a
    /// recorder that split the encoder a thousand times a frame is visible in the budget without the copy itself
    /// being in it.</para>
    ///
    /// <para><b>IT IS A LINE AT ALL BECAUSE ROW 8 OWES A DEVICE-FREE TEST, and the test is about a NEGATIVE.</b>
    /// The whole claim of the uniform ring is that a record-time uniform write opens NO ENCODER and emits NO
    /// COPY, where every other record-time upload does both. A backend that routed a uniform write down the
    /// staging path would still produce correct pixels and would cost a full graphics-state re-activation per
    /// write, which is exactly the shipped incumbent's behaviour and exactly what no golden can see. The
    /// interop layer's calls are static P/Invoke, so an emission is observable only where there is a line to
    /// interpose on.</para>
    ///
    /// <para><b>HANDLES ARE <see cref="IntPtr"/> AND NOTHING HERE NAMES AN OBJECTIVE-C TYPE</b>, so a fake
    /// invents plain numbers and the routing tests run on the Linux and Windows legs.</para>
    /// </summary>
    internal interface IMetalBlitApi
    {
        /// <summary>
        /// <c>-[MTLBlitCommandEncoder copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:]</c>.
        /// </summary>
        /// <param name="encoder">The open blit encoder.</param>
        /// <param name="source">The staging arena block the payload was written into.</param>
        /// <param name="sourceOffsetBytes">Where in that block the lease started. A multiple of four by
        /// construction (<see cref="MetalStagingArena.CopyAlignment"/>).</param>
        /// <param name="destination">The buffer being written.</param>
        /// <param name="destinationOffsetBytes">The caller's own offset. A multiple of four, refused by name at
        /// the command list when it is not.</param>
        /// <param name="sizeBytes">The payload rounded up to four, which is section 9.3's reproduction of the
        /// incumbent's own size pad.</param>
        void CopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffsetBytes, IntPtr destination,
            ulong destinationOffsetBytes, ulong sizeBytes);
    }

    /// <summary>
    /// The real one: a single message send, in the shape <see cref="MetalEncoderSink"/> established. A readonly
    /// struct with no state, so it can be held as an interface field on the command list without the sink rule's
    /// two-copies hazard applying to it.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal readonly struct MetalBlitApi : IMetalBlitApi
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CopyBufferToBuffer(IntPtr encoder, IntPtr source, ulong sourceOffsetBytes, IntPtr destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLBlitCommandEncoder(encoder).CopyFromBufferToBuffer(
                new MTLBuffer(source), (nuint)sourceOffsetBytes,
                new MTLBuffer(destination), (nuint)destinationOffsetBytes, (nuint)sizeBytes);
        }
    }
}
