using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE BLIT-ENCODER-SCOPED TRANSFERS, behind an interface so the ROUTING and the ARITHMETIC above them are
    /// device-free: the buffer copy a record-time <c>UpdateBuffer</c> on a NON-uniform buffer emits (M-M8), the
    /// three copy selectors the seam's texture copies fan out to, and mip generation.
    ///
    /// <para><b>ROW 8 DECLARED ONE MEMBER AND ROW 14 ADDED FOUR</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580), which is the same line drawn wider rather than a
    /// different line: a texture copy's arithmetic (which of the four staging cases it is, which subresource each
    /// side names, and the byte offsets and pitches the staging side supplies) is the highest-risk parity surface
    /// in this backend, since a wrong pitch garbles every golden readback at once with no error anywhere. Behind
    /// this interface all of it runs under a plain <c>[Fact]</c> on a machine with no Metal at all. See
    /// <see cref="MetalTransferPlan"/>.</para>
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

        /// <summary>
        /// <c>-copyFromTexture:...toTexture:...</c>, the arm a copy between two non-staging textures takes.
        /// </summary>
        /// <param name="encoder">The open blit encoder.</param>
        /// <param name="source">The source <c>MTLTexture</c>.</param>
        /// <param name="destination">The destination <c>MTLTexture</c>.</param>
        /// <param name="region">Which subresource on each side, and how many texels.</param>
        void CopyTextureToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalTextureRegion region);

        /// <summary>
        /// <c>-copyFromTexture:...toBuffer:...</c>, the READBACK arm, which is what every golden in the suite
        /// goes through.
        /// </summary>
        /// <param name="encoder">The open blit encoder.</param>
        /// <param name="source">The source <c>MTLTexture</c>.</param>
        /// <param name="destination">The staging texture's backing <c>MTLBuffer</c>.</param>
        /// <param name="region">The buffer terms and the texture's subresource.</param>
        void CopyTextureToBuffer(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region);

        /// <summary>
        /// <c>-copyFromBuffer:...toTexture:...</c>, the upload arm, which is a record-time copy out of a staging
        /// texture rather than row 6's device-level one.
        /// </summary>
        /// <param name="encoder">The open blit encoder.</param>
        /// <param name="source">The staging texture's backing <c>MTLBuffer</c>.</param>
        /// <param name="destination">The destination <c>MTLTexture</c>.</param>
        /// <param name="region">The buffer terms and the texture's subresource.</param>
        void CopyBufferToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region);

        /// <summary>
        /// <c>-generateMipmapsForTexture:</c>, the WHOLE chain in one call.
        /// <para>
        /// THERE IS NO REGION AND NO FILTER ARGUMENT, and the absence is Metal being shorter than both siblings
        /// rather than this seam hiding something: Vulkan generates a chain as a loop of <c>vkCmdBlitImage</c>
        /// with a layout transition per level and a filter to choose, and the blit encoder does the whole thing
        /// itself. What is left for the caller is deciding whether the texture is a legal argument at all.
        /// </para>
        /// </summary>
        /// <param name="encoder">The open blit encoder.</param>
        /// <param name="texture">The <c>MTLTexture</c> whose chain is generated from its base level.</param>
        void GenerateMipmaps(IntPtr encoder, IntPtr texture);
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

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CopyTextureToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalTextureRegion region)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLBlitCommandEncoder(encoder).CopyFromTextureToTexture(
                new MTLTexture(source), region.SourceLayer, region.SourceLevel, Origin,
                new MTLSize(region.Width, region.Height, 1),
                new MTLTexture(destination), region.DestinationLayer, region.DestinationLevel, Origin);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CopyTextureToBuffer(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLBlitCommandEncoder(encoder).CopyFromTextureToBuffer(
                new MTLTexture(source), region.Layer, region.Level, Origin,
                new MTLSize(region.Width, region.Height, 1),
                new MTLBuffer(destination), (nuint)region.BufferOffset, (nuint)region.BytesPerRow,
                (nuint)region.BytesPerImage);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void CopyBufferToTexture(IntPtr encoder, IntPtr source, IntPtr destination,
            in MetalBufferImageRegion region)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLBlitCommandEncoder(encoder).CopyFromBufferToTexture(
                new MTLBuffer(source), (nuint)region.BufferOffset, (nuint)region.BytesPerRow,
                (nuint)region.BytesPerImage, new MTLSize(region.Width, region.Height, 1),
                new MTLTexture(destination), region.Layer, region.Level, Origin);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void GenerateMipmaps(IntPtr encoder, IntPtr texture)
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();

            new MTLBlitCommandEncoder(encoder).GenerateMipmapsForTexture(new MTLTexture(texture));
        }

        // THE ONLY ORIGIN ANY OF THESE EVER PASSES. The seam copies whole subresources from their top-left
        // corner, on both sides, so a parameter for it would be a parameter that only ever holds one value. See
        // MetalTextureRegion for the same note one level up.
        static MTLOrigin Origin => new(0, 0, 0);
    }
}
