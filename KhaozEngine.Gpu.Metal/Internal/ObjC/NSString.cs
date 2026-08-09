using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSString</c> handle, and the one direction this backend needs: native to managed. A device name, an
    /// <c>NSError</c>'s localized description and a class name all arrive as one of these and all three end up in
    /// a log line or a capture header.
    /// <para>
    /// THE OTHER DIRECTION IS NOT HERE AND THAT IS DELIBERATE. Nothing this backend does needs to CREATE an
    /// <c>NSString</c>: labels on Metal objects would be the first consumer, and naming objects for a GPU capture
    /// belongs with the capture path (M-G5, row 16) rather than with the device. Adding
    /// <c>+stringWithUTF8String:</c> here for symmetry would add a <c>byte*</c> argument shape to
    /// <see cref="ObjCMsgSend"/> that no caller exercises, and an unexercised interop prototype is the one kind
    /// of dead code in this package that can corrupt memory the day somebody uses it.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct NSString(IntPtr Handle)
    {
        /// <summary>True when this is nil, which is an ordinary answer from Objective-C rather than an error.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>
        /// This string as a managed copy, EMPTY for nil. A copy rather than a view, because the receiver is very
        /// often autoreleased and the caller's pool drains before the value is used.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal string ToManaged()
        {
            if (Handle == IntPtr.Zero) return "";

            // -UTF8String hands back a pointer into an autoreleased buffer, so it is only valid until the
            // enclosing pool drains. Marshal.PtrToStringUTF8 copies immediately, which is what makes the return
            // value safe to hold.
            IntPtr utf8 = ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }
    }
}
