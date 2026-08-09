using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The interop DECLARATIONS the timeline subsystem needs, and nothing else: the runtime shim, the
    /// autorelease-pool pair, the typed <c>objc_msgSend</c> overloads for <c>MTLSharedEvent</c>'s four members
    /// (M-F1), <c>addCompletedHandler:</c> and the two reads M-G4 takes at completion, plus the Objective-C block
    /// layout M-F3's <c>[UnmanagedCallersOnly]</c> handler is installed into.
    /// <para>
    /// THIS IS NOT ROW 4'S INTEROP LAYER, and it is shaped so it cannot quietly become one, which is the same
    /// call row 2's probe made and for the same reason. That layer is <c>Internal/ObjC/</c>, one file per
    /// Objective-C class, and it lands with the device and the queue
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/570). Row 5 is pulled ahead of it because row 8's ring
    /// reads a completion value and a ring built before the timeline exists is a silent corruption, so the
    /// timeline needs a handful of selectors before the real layer exists. The handoff is recorded on #570
    /// itself: row 4 absorbs these declarations into the real layer and DELETES this file. Nothing here is
    /// public, nothing here is reachable from outside the assembly, and the whole set is small enough to delete
    /// rather than migrate.
    /// </para>
    /// <para>
    /// EVERY IMPORT IS <c>[LibraryImport]</c> WITH BLITTABLE-ONLY SIGNATURES, which is decision M-P2's mechanism
    /// and, under this repo's warnings-as-errors rule, also what the SYSLIB1054 analyzer requires: the source
    /// generator emits a direct call with no marshalling stub. Strings are where that bites, so
    /// <c>sel_registerName</c> takes a <c>byte*</c> and the caller encodes ASCII itself.
    /// </para>
    /// <para>
    /// THE OVERLOAD SET IS THE ARM64 STORY. <c>objc_msgSend</c> is called through a prototype matching the real
    /// method signature, because arguments are placed by the caller according to the callee's declared types, so
    /// one variadic declaration reused for every selector is the classic way hand-rolled Objective-C interop
    /// corrupts memory. <c>BOOL</c> is one byte. Every shape here is a scalar or a handle, so none of row 1's
    /// by-value struct paths is reached from the timeline at all.
    /// </para>
    /// </summary>
    internal static unsafe partial class MetalTimelineNative
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";
        const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";

        // ---- The runtime shim -----------------------------------------------------------------------------

        [LibraryImport(Objc, EntryPoint = "sel_registerName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SelRegisterName(byte* name);

        [LibraryImport(Objc, EntryPoint = "objc_release")]
        [SupportedOSPlatform("macos")]
        internal static partial void ObjcRelease(IntPtr obj);

        // The pool pair (M-N5). The completion handler reads -error and -localizedDescription, both of which
        // hand back autoreleased objects, and it runs on a driver thread whose implicit pool drains at a moment
        // nobody here controls. The rule is that every entry point which can create an autoreleased object wraps
        // its body, and a completion callback under a frame loop is the exact shape that rule exists for.
        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPush")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr AutoreleasePoolPush();

        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPop")]
        [SupportedOSPlatform("macos")]
        internal static partial void AutoreleasePoolPop(IntPtr pool);

        // Only the timeline probe calls this. The backend's real device creation, with KE_METAL_DEVICE
        // selection, is row 4's (M-N1).
        [LibraryImport(MetalFramework, EntryPoint = "MTLCreateSystemDefaultDevice")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MTLCreateSystemDefaultDevice();

        // ---- The typed objc_msgSend overload set ----------------------------------------------------------

        // -newSharedEvent, -newCommandQueue, -commandBuffer, -error, -device and -UTF8String: a bare
        // object-returning message with no arguments.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel);

        // -commit, and nothing else here.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel);

        // -addCompletedHandler:, whose argument is the block below.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        // -signaledValue, a uint64_t property read. This is the one on the polling path, which is why the
        // selector behind it is cached at construction rather than registered per call.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial ulong MsgSendULong(IntPtr receiver, IntPtr sel);

        // -encodeSignalEvent:value:, an object plus a uint64_t. The receiver is the COMMAND BUFFER and the
        // object is the event, which is why the timeline's encode takes a command buffer (M-F1).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidPtrULong(IntPtr receiver, IntPtr sel, IntPtr a, ulong b);

        // -waitUntilSignaledValue:timeoutMS:, a uint64_t plus a uint32_t returning BOOL. BOOL is ONE BYTE on
        // arm64, which is why this returns byte rather than bool: a bool return would also generate a
        // marshalling stub, which SYSLIB1054 rejects under this repo's rules.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolULongUInt(IntPtr receiver, IntPtr sel, ulong a, uint b);

        // -status on MTLCommandBuffer and -code on NSError, both NSInteger (M-G4).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nint MsgSendNInt(IntPtr receiver, IntPtr sel);

        // ---- The block layout ------------------------------------------------------------------------------

        /// <summary>
        /// An Objective-C block, which is what <c>addCompletedHandler:</c> takes. The layout is the ABI's rather
        /// than ours: isa, flags, reserved, invoke, descriptor. M-F3 puts an <c>[UnmanagedCallersOnly]</c> static
        /// in the invoke slot, so there is no delegate, no <c>Marshal.GetFunctionPointerForDelegate</c> and no GC
        /// handle anywhere on the completion path.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockLiteral
        {
            internal IntPtr Isa;
            internal int Flags;
            internal int Reserved;
            internal IntPtr Invoke;
            internal IntPtr Descriptor;
        }

        /// <summary>A block's descriptor. A block with no captures needs no copy helper and no dispose helper,
        /// so the two-field form is the whole of it.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockDescriptor
        {
            internal nuint Reserved;
            internal nuint Size;
        }

        // ---- Small helpers ---------------------------------------------------------------------------------

        /// <summary>The selector for <paramref name="name"/>, registered with the Objective-C runtime.</summary>
        [SupportedOSPlatform("macos")]
        internal static IntPtr Sel(string name)
        {
            fixed (byte* p = Ascii(name)) return SelRegisterName(p);
        }

        /// <summary>An <c>NSString</c> as a managed string, empty for nil.</summary>
        [SupportedOSPlatform("macos")]
        internal static string NSStringToManaged(IntPtr nsString)
        {
            if (nsString == IntPtr.Zero) return "";
            IntPtr utf8 = MsgSend(nsString, Sel("UTF8String"));
            return utf8 == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(utf8) ?? "";
        }

        // ASCII into a heap array rather than a stackalloc, so the fixed statement at the call site owns the
        // lifetime and nothing here depends on an inlining decision. Every name passed in is a compile-time
        // constant, and the two hot paths (the signalled-value poll and the encode) cache their selectors at
        // construction, so this runs on cold paths only.
        static byte[] Ascii(string text)
        {
            var bytes = new byte[Encoding.ASCII.GetByteCount(text) + 1];
            Encoding.ASCII.GetBytes(text, bytes);
            return bytes;
        }
    }
}
