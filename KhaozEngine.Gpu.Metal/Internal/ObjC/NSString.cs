using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSString</c> handle, and both directions this backend needs.
    /// <para>
    /// NATIVE TO MANAGED came first and is the common one: a device name, an <c>NSError</c>'s localized
    /// description and a class name all arrive as one of these and all three end up in a log line or a capture
    /// header.
    /// </para>
    /// <para>
    /// MANAGED TO NATIVE ARRIVED WITH THE SHADER ROW, and this file previously said it was deliberately absent
    /// because no caller exercised it. That reasoning was right and it has expired:
    /// <c>-[MTLDevice newLibraryWithSource:options:error:]</c> takes the MSL as an <c>NSString</c>, and
    /// <c>-[MTLLibrary newFunctionWithName:]</c> takes the entry-point name as one, so the shader path is the
    /// first real consumer rather than a symmetry argument. The old note's actual point stands: an interop
    /// prototype nothing calls is the one kind of dead code here that can corrupt memory the day somebody uses
    /// it, so this direction lands WITH its callers and with <c>MetalShaderGpuTests</c> running it on a device.
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

        /// <summary>
        /// An AUTORELEASED <c>NSString</c> carrying <paramref name="value"/>, through
        /// <c>+[NSString stringWithUTF8String:]</c>.
        /// <para>
        /// AUTORELEASED RATHER THAN OWNED, which is the whole of its lifetime contract: the <c>stringWith</c>
        /// prefix is neither <c>alloc</c> nor <c>new</c> nor <c>copy</c>, so this comes back at +0 and the caller
        /// must NOT release it. It lives until the enclosing <see cref="ObjCAutoreleasePool"/> drains, which every
        /// caller on this path opens, and Metal copies the source text out of it during the compile rather than
        /// retaining it.
        /// </para>
        /// <para>
        /// THE UTF-8 BYTES ARE PINNED FOR THE DURATION OF THE CALL and nothing keeps them afterwards.
        /// <c>+stringWithUTF8String:</c> copies, so a stack buffer would be legal too: the array is here because
        /// an MSL source is tens of kilobytes and a stackalloc of that size is not.
        /// </para>
        /// </summary>
        /// <param name="value">The managed string. A null is not accepted: a nil source or a nil function name is
        /// a caller bug that would surface as a Metal error message about something unrelated.</param>
        /// <exception cref="InvalidOperationException">The runtime has no <c>NSString</c> class, which is
        /// <c>MTLCompileOptions.New</c>'s case rather than a new one: a message to a nil class answers nil, so
        /// without this the caller would compile a nil source and read the failure as a broken shader.</exception>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static unsafe NSString FromManaged(string value)
        {
            ArgumentNullException.ThrowIfNull(value);

            // The nil-class check MTLCompileOptions.New's caller makes, made here instead of at the two call
            // sites, because there is nothing either of them could do differently. objc_msgSend to a nil class
            // answers nil, so skipping it would hand newLibraryWithSource: a nil source and hand the caller back
            // a nil library with no NSError, which reads as "this shader failed to compile" and is not.
            IntPtr cls = ObjCRuntime.ClassNamed("NSString");
            if (cls == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The Objective-C runtime has no NSString class, which means Foundation did not load. Nothing "
                    + "about this string or this shader caused it.");
            }

            int byteCount = Encoding.UTF8.GetByteCount(value);
            var utf8 = new byte[byteCount + 1];
            Encoding.UTF8.GetBytes(value, utf8);
            utf8[byteCount] = 0;

            fixed (byte* p = utf8)
            {
                return new NSString(ObjCMsgSend.SendPtrBytes(
                    cls, ObjCRuntime.Sel("stringWithUTF8String:"), p));
            }
        }
    }
}
