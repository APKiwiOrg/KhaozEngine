using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The interop DECLARATIONS the machine probe needs, and nothing else: eight typed
    /// <c>objc_msgSend</c> overloads, the two runtime lookups, the autorelease pool pair and
    /// <c>MTLCreateSystemDefaultDevice</c>. Split from <see cref="MetalSupportProbe"/> the way row 1's spike
    /// splits its own two halves, so the declarations read as a list and the reads read as a sequence.
    /// <para>
    /// THIS IS NOT ROW 4'S INTEROP LAYER, and it is deliberately shaped so it cannot quietly become one. That
    /// layer is <c>Internal/ObjC/</c>, one file per Objective-C class, and it lands with the device and the queue
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570). Row 2 needs a device handle, four reads and a
    /// release, before any of that exists, and the handoff is recorded on #570 itself the way phase 3's #514
    /// recorded sharing #512's enumeration: row 4 absorbs these declarations into the real layer and deletes
    /// this file, and it re-points the probe at the KE_METAL_DEVICE-selected device when M-N1 lands. So when
    /// row 4 lands, <see cref="MetalSupportProbe"/> reads through the real layer. Nothing here is public,
    /// nothing here is reachable from another type, and the whole set is small enough to delete rather than
    /// migrate.
    /// </para>
    /// <para>
    /// EVERY IMPORT IS <c>[LibraryImport]</c> WITH BLITTABLE-ONLY SIGNATURES, which is decision M-P2's mechanism
    /// and, under this repo's warnings-as-errors rule, also what the SYSLIB1054 analyzer requires: the source
    /// generator emits a direct call with no marshalling stub. Strings are where that bites, so
    /// <c>sel_registerName</c> takes a <c>byte*</c> and the caller encodes ASCII itself.
    /// </para>
    /// <para>
    /// THE OVERLOAD SET IS THE ARM64 STORY, restated because it is the thing that kills hand-rolled Objective-C
    /// interop rather than a detail of it. <c>objc_msgSend</c> is called through a prototype matching the real
    /// method signature, because arguments are placed by the caller according to the callee's declared types, so
    /// one variadic declaration reused for every selector is the classic corruption. <c>BOOL</c> is one byte.
    /// Every shape below is a scalar or a handle, so none of row 1's by-value struct paths is reached from the
    /// probe at all, which is the other reason this set is allowed to be its own small thing.
    /// </para>
    /// </summary>
    internal static unsafe partial class MetalSupportProbeNative
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";
        const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";

        [LibraryImport(Objc, EntryPoint = "sel_registerName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SelRegisterName(byte* name);

        [LibraryImport(Objc, EntryPoint = "objc_release")]
        [SupportedOSPlatform("macos")]
        internal static partial void ObjcRelease(IntPtr obj);

        // The pool pair (M-N5). MTLCreateSystemDefaultDevice hands back a +1 device, which the probe releases by
        // hand, but -name and the rest return autoreleased objects, so the probe's body sits inside a pool
        // instead of leaving them to a thread pool thread's implicit one. The rule is that every public entry
        // point which can create an autoreleased object wraps its body, and a probe that leaked because it was
        // "only four reads" is exactly the habit M-N5 exists to replace with a rule.
        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPush")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr AutoreleasePoolPush();

        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPop")]
        [SupportedOSPlatform("macos")]
        internal static partial void AutoreleasePoolPop(IntPtr pool);

        [LibraryImport(MetalFramework, EntryPoint = "MTLCreateSystemDefaultDevice")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MTLCreateSystemDefaultDevice();

        // -name, and -UTF8String on the NSString it returns.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel);

        // -respondsToSelector:, the one call that lets the probe ask about a property this OS may not have
        // instead of finding out through an unrecognised-selector crash.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        // -supportsFamily:, whose argument is an MTLGPUFamily (NSInteger).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolNInt(IntPtr receiver, IntPtr sel, nint a);

        // -supportsTextureSampleCount:, whose argument is an NSUInteger.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolNUInt(IntPtr receiver, IntPtr sel, nuint a);

        // -minimumLinearTextureAlignmentForPixelFormat:, an NSUInteger in and an NSUInteger out.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nuint MsgSendNUIntNUInt(IntPtr receiver, IntPtr sel, nuint a);

        // A bare NSUInteger property read, for the constant-buffer alignment query a future OS may add.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nuint MsgSendNUInt(IntPtr receiver, IntPtr sel);

        /// <summary>The selector for <paramref name="name"/>, registered with the Objective-C runtime.</summary>
        [SupportedOSPlatform("macos")]
        internal static IntPtr Sel(string name)
        {
            fixed (byte* p = Ascii(name)) return SelRegisterName(p);
        }

        /// <summary>An autoreleased <c>NSString</c> as a managed string, empty for nil.</summary>
        [SupportedOSPlatform("macos")]
        internal static string NSStringToManaged(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return "";
            IntPtr utf8 = MsgSend(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }

        // ASCII into a heap array rather than a stackalloc, so the fixed statement at the call site owns the
        // lifetime and nothing here depends on an inlining decision. Every name passed in is a compile-time
        // constant on a cold path, so the allocation is bounded and happens a handful of times per probe.
        static byte[] Ascii(string text)
        {
            var bytes = new byte[Encoding.ASCII.GetByteCount(text) + 1];
            Encoding.ASCII.GetBytes(text, bytes);
            return bytes;
        }
    }
}
