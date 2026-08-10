using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSError</c> handle, with the two reads decision M-G4 needs: the <c>-code</c>, which is an
    /// <c>MTLCommandBufferError</c> when the error came off a command buffer, and the
    /// <c>-localizedDescription</c>, which is the driver's own sentence about what happened.
    /// <para>
    /// THE INCUMBENT READS NEITHER. The vendored fork reads <c>MTLCommandBuffer.status</c> in exactly one place,
    /// to decide whether to wait, and never reads <c>.error</c> at all, so a Metal device loss today is invisible
    /// to the engine and to telemetry. That is what #427 asks for and what this type exists to make possible.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil. Nil is the ORDINARY
    /// answer: a command buffer that completed successfully has a nil error, so this handle being null is the
    /// healthy case rather than a failed read.</param>
    internal readonly record struct NSError(IntPtr Handle)
    {
        /// <summary>True when there is no error, which is what a completed command buffer reports.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>The error's own code. Interpreted against its domain: for an error off a command buffer that
        /// domain is <c>MTLCommandBufferErrorDomain</c> and the code is an
        /// <see cref="MTLCommandBufferError"/>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal nint Code()
            => Handle == IntPtr.Zero ? 0 : ObjCMsgSend.SendNInt(Handle, ObjCRuntime.Sel("code"));

        /// <summary>
        /// The driver's own description, empty when there is no error or it has nothing to say. Localized by
        /// name and by nature, so it is a DIAGNOSTIC string rather than a token: the latch pairs it with the
        /// stable code so a capture can group across sessions and still carry the sentence a human reads.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal string LocalizedDescription()
        {
            if (Handle == IntPtr.Zero) return "";
            var text = new NSString(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("localizedDescription")));
            return text.ToManaged();
        }
    }
}
