using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLBlitCommandEncoder</c> handle, with the two copies this backend records: a Shared
    /// <see cref="MTLBuffer"/> into a Private <see cref="MTLTexture"/>, and a Shared buffer into another buffer.
    /// <para>
    /// TWO CALLERS, AND NEITHER OF THEM IS AN ENCODER LIFECYCLE. Decision M-M9 moves the device-level
    /// <c>UpdateTexture</c> off its own queue submit and onto <c>MetalSetupCommands</c>, which opens an encoder,
    /// records the texture copy and ends it in one call. Decision M-M8's record-time bulk
    /// <c>UpdateBuffer</c> encodes the buffer copy into the COMMAND LIST's own blit encoder instead, which is
    /// obtained through <c>MetalEncoderScope</c> and lives until the next kind switch. The one-at-a-time
    /// invariant and every transition between kinds belong to the recording row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), and nothing here tracks any of it: this type is a
    /// handle and two selectors.
    /// </para>
    /// <para>
    /// <b>THE COPY SELECTOR IS A NEW ABI SHAPE, and it is the only one this row adds that row 1's spike does not
    /// already cover</b> (see <see cref="ObjCMsgSend"/> for the prototype and the argument). It takes eleven arguments counting the receiver and the selector, which is three more
    /// than arm64 has general-purpose argument registers, so the last three cross ON THE STACK. Every individual
    /// argument CLASS in it is measured (an object pointer, an <c>NSUInteger</c>, and a 24-byte integer composite
    /// passed indirectly, which is <c>MTLScissorRect</c>'s arm of section 3.1's answer), and what is new is the
    /// spill. The runtime lowers a correct managed signature to the platform ABI itself, so the exposure is a
    /// wrong SIGNATURE rather than a wrong lowering, and <c>MetalResourceGpuTests</c> is what checks the device
    /// accepts the call: an ABI error here presents as a crash or a validation failure rather than as a wrong
    /// pixel, which is the one comforting property of this risk.
    /// </para>
    /// <para>
    /// AN ENCODER ARRIVES AUTORELEASED, like the command buffer that makes it, so nothing here releases one and
    /// every caller is already inside an <see cref="ObjCAutoreleasePool"/> scope (M-N5).
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLBlitCommandEncoder(IntPtr Handle)
    {
        /// <summary>True when the command buffer would not make one, which is a buffer that has already been
        /// committed.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// <c>-copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:</c>.
        /// <para>
        /// <paramref name="sourceBytesPerImage"/> IS ZERO FOR EVERY 2D TEXTURE, which is the incumbent's own
        /// value: <c>MTLCommandList.CopyTextureCore</c> zeroes it for anything that is not a 3D texture, and this
        /// seam has no 3D texture. Passing the depth pitch instead is legal for a single-slice copy and would
        /// diverge from the incumbent for no gain, so the zero is reproduced rather than improved.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CopyFromBufferToTexture(MTLBuffer source, nuint sourceOffset, nuint sourceBytesPerRow,
            nuint sourceBytesPerImage, MTLSize sourceSize, MTLTexture destination, nuint destinationSlice,
            nuint destinationLevel, MTLOrigin destinationOrigin)
            => ObjCMsgSend.SendVoidBufferToTextureCopy(Handle,
                ObjCRuntime.Sel("copyFromBuffer:sourceOffset:sourceBytesPerRow:sourceBytesPerImage:sourceSize:"
                    + "toTexture:destinationSlice:destinationLevel:destinationOrigin:"),
                source.Handle, sourceOffset, sourceBytesPerRow, sourceBytesPerImage, sourceSize,
                destination.Handle, destinationSlice, destinationLevel, destinationOrigin);

        /// <summary>
        /// <c>-copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>, the record-time bulk upload's
        /// copy out of a <see cref="MetalStagingArena"/> block into the destination buffer (M-M8).
        /// <para>
        /// ALL THREE NUMBERS ARE MULTIPLES OF FOUR BY CONSTRUCTION, which is what macOS asks of this selector.
        /// The source offset is the arena's, aligned at the lease
        /// (<see cref="MetalStagingArena.CopyAlignment"/>). The size is the payload rounded up, which is the
        /// size-rounding half section 9.3 reproduces from the incumbent and which
        /// <see cref="MetalBufferPolicy.AllocationBytes"/> makes land inside the destination's allocation. The
        /// destination offset is the CALLER's, and the command list refuses an unaligned one by name rather than
        /// letting the driver reject it.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CopyFromBufferToBuffer(MTLBuffer source, nuint sourceOffset, MTLBuffer destination,
            nuint destinationOffset, nuint size)
            => ObjCMsgSend.SendVoidBufferToBufferCopy(Handle,
                ObjCRuntime.Sel("copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:"),
                source.Handle, sourceOffset, destination.Handle, destinationOffset, size);

        /// <summary>
        /// <c>-copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:</c>,
        /// the READBACK direction of the copy above.
        /// <para>
        /// <b>IT IS HERE FOR ROW 12, AND THE REASON IS THAT A <c>[GpuFact]</c> WHICH ONLY ASSERTS NO-THROW IS HOW
        /// THE ALL-BLACK SPLAT TERRAIN SHIPPED.</b> M-A2's whole claim is that a clear lands on the attachment
        /// the caller named, and M-A4's is that the result is STORED rather than discarded by the descriptor's
        /// own default. Neither is observable from a command buffer that completed: a pass that cleared the wrong
        /// attachment and a pass that threw its result away both complete with a nil error. This selector is what
        /// turns both into a pixel read, in
        /// <c>MetalRenderPassGpuTests</c>.
        /// </para>
        /// <para>
        /// ROW 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580) OWNS THE SEAM MEMBER ABOVE IT.
        /// <c>IGpuCommandList.CopyTexture</c> into a staging texture is the engine's readback path and it is that
        /// row's, so this is the interop half arriving one row early with a caller and a test rather than a
        /// declaration nobody has run.
        /// </para>
        /// <para>
        /// SAME ABI SHAPE AS THE UPLOAD COPY, mirrored: eleven arguments with three on the stack, one 24-byte
        /// integer composite in each of the two positions. Row 1's spike ran this exact prototype against a
        /// device as its by-value round-trip, so the shape is measured rather than assumed.
        /// </para>
        /// <para>
        /// <paramref name="destinationBytesPerImage"/> IS THE DEPTH PITCH RATHER THAN ZERO here, unlike the
        /// upload's, because a readback names where the NEXT slice would start and the engine's staging layout
        /// has an answer for that (<c>MetalStagingLayout.DepthPitch</c>). For the single-slice copies this
        /// backend records the two readings are interchangeable.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void CopyFromTextureToBuffer(MTLTexture source, nuint sourceSlice, nuint sourceLevel,
            MTLOrigin sourceOrigin, MTLSize sourceSize, MTLBuffer destination, nuint destinationOffset,
            nuint destinationBytesPerRow, nuint destinationBytesPerImage)
            => ObjCMsgSend.SendVoidTextureToBufferCopy(Handle,
                ObjCRuntime.Sel("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toBuffer:"
                    + "destinationOffset:destinationBytesPerRow:destinationBytesPerImage:"),
                source.Handle, sourceSlice, sourceLevel, sourceOrigin, sourceSize, destination.Handle,
                destinationOffset, destinationBytesPerRow, destinationBytesPerImage);

        /// <summary><c>-endEncoding</c>. An encoder that is not ended blocks every later encoder on the same
        /// command buffer, and Metal traps on a second encoder while one is open, which is why the setup buffer
        /// ends this one in the same call that opened it.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void EndEncoding() => ObjCMsgSend.SendVoid(Handle, ObjCRuntime.Sel("endEncoding"));
    }
}
