using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLBlitCommandEncoder</c> handle, with the ONE copy this row records: a Shared
    /// <see cref="MTLBuffer"/> into a Private <see cref="MTLTexture"/>.
    /// <para>
    /// THIS IS THE DEVICE-OWNED SETUP BUFFER'S ENCODER AND NOT THE COMMAND LIST'S. Decision M-M9 moves the
    /// device-level <c>UpdateTexture</c> off its own queue submit and onto <c>MetalSetupCommands</c>, which is
    /// what encodes through this type. The command list's own three encoders, their one-at-a-time invariant and
    /// every transition between them belong to the recording row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573), which owns the encoder LIFECYCLE. Nothing here
    /// tracks a lifecycle: the setup buffer opens an encoder, records, and ends it in one call.
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

        /// <summary><c>-endEncoding</c>. An encoder that is not ended blocks every later encoder on the same
        /// command buffer, and Metal traps on a second encoder while one is open, which is why the setup buffer
        /// ends this one in the same call that opened it.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void EndEncoding() => ObjCMsgSend.SendVoid(Handle, ObjCRuntime.Sel("endEncoding"));
    }
}
