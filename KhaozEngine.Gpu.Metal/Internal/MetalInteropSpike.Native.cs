using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The interop DECLARATIONS half of the spike: the runtime shim, the typed <c>objc_msgSend</c> overload set,
    /// and the by-value structs. Split from the exercise half purely so neither file grows past the size cap
    /// while both stay readable, which is the same answer the design gives for the real interop layer in row 4
    /// (one file per Objective-C class rather than one per API surface).
    /// <para>
    /// EVERY SIGNATURE HERE IS BLITTABLE AND EVERY IMPORT IS <c>[LibraryImport]</c>, so the source generator
    /// emits a direct call with no marshalling stub. That is decision M-P2's mechanism, and under this repo's
    /// warnings-as-errors rule it is also what the SYSLIB1054 analyzer requires. Strings are the one place that
    /// bites: a <c>string</c> parameter would generate a stub, so <c>objc_getClass</c> and
    /// <c>sel_registerName</c> take <c>byte*</c> and the caller encodes ASCII into a stack buffer itself.
    /// </para>
    /// <para>
    /// THE OVERLOAD SET IS THE ARM64 STORY. <c>objc_msgSend</c> must be called through a prototype matching the
    /// real method signature on arm64, because arguments are placed by the caller according to the callee's
    /// declared types. One variadic declaration reused for every selector is the classic way hand-rolled
    /// Objective-C interop corrupts memory, so every distinct shape below is its own named entry point onto the
    /// same symbol. <c>objc_msgSend_stret</c> does not exist on arm64 at all, so no stret path is written here
    /// rather than one being written and disabled.
    /// </para>
    /// </summary>
    internal static unsafe partial class MetalInteropSpike
    {
        const string Objc = "/usr/lib/libobjc.A.dylib";
        const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";
        const string SystemLib = "/usr/lib/libSystem.B.dylib";

        // ---- The runtime shim -------------------------------------------------------------------------------

        [LibraryImport(Objc, EntryPoint = "objc_getClass")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjcGetClass(byte* name);

        [LibraryImport(Objc, EntryPoint = "sel_registerName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SelRegisterName(byte* name);

        [LibraryImport(Objc, EntryPoint = "object_getClassName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjectGetClassName(IntPtr obj);

        [LibraryImport(Objc, EntryPoint = "objc_retain")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjcRetain(IntPtr obj);

        [LibraryImport(Objc, EntryPoint = "objc_release")]
        [SupportedOSPlatform("macos")]
        internal static partial void ObjcRelease(IntPtr obj);

        // The autorelease pool (M-N4's family). Every Objective-C call that returns an autoreleased object needs
        // a pool in scope or the object leaks until the thread's implicit one drains, which under a frame loop
        // is never. Push and pop rather than an NSAutoreleasePool object, because that is the supported spelling
        // and it needs no class lookup.
        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPush")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr AutoreleasePoolPush();

        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPop")]
        [SupportedOSPlatform("macos")]
        internal static partial void AutoreleasePoolPop(IntPtr pool);

        // ---- Metal's own C entry points ---------------------------------------------------------------------

        [LibraryImport(MetalFramework, EntryPoint = "MTLCreateSystemDefaultDevice")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MTLCreateSystemDefaultDevice();

        // The native environment, which is NOT the environment System.Environment mutates on Unix: the CLR keeps
        // its own copy and never writes through. M-G3 asks whether an in-process mutation can reach the Metal
        // validation layer at all, and this is the only call that could possibly make it true.
        [LibraryImport(SystemLib, EntryPoint = "setenv")]
        [SupportedOSPlatform("macos")]
        internal static partial int SetEnv(byte* name, byte* value, int overwrite);

        // ---- The typed objc_msgSend overload set ------------------------------------------------------------

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSend(IntPtr receiver, IntPtr sel);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoid(IntPtr receiver, IntPtr sel);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSendPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        // BOOL is ONE BYTE on arm64, which is why the return type here is byte rather than int or bool. A bool
        // return would also generate a marshalling stub, which SYSLIB1054 rejects under this repo's rules.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolNInt(IntPtr receiver, IntPtr sel, nint a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidBool(IntPtr receiver, IntPtr sel, byte a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBool(IntPtr receiver, IntPtr sel);

        // respondsToSelector: - a SEL argument returning BOOL. The one call that lets a probe ask about a
        // property an older OS does not have, instead of finding out through an unrecognised-selector crash.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolPtr(IntPtr receiver, IntPtr sel, IntPtr a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nuint MsgSendNUInt(IntPtr receiver, IntPtr sel);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial nint MsgSendNInt(IntPtr receiver, IntPtr sel);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidNUInt(IntPtr receiver, IntPtr sel, nuint a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSendPtrNUInt2(IntPtr receiver, IntPtr sel, nuint a, nuint b);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSendPtrNUInt(IntPtr receiver, IntPtr sel, nuint a);

        // CGFloat is a DOUBLE on 64-bit, so this argument rides a SIMD register rather than a general one.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidDouble(IntPtr receiver, IntPtr sel, double a);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial double MsgSendDouble(IntPtr receiver, IntPtr sel);

        // texture2DDescriptorWithPixelFormat:width:height:mipmapped: - three NSUIntegers and a BOOL.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr MsgSendPtrTextureDesc(
            IntPtr receiver, IntPtr sel, nuint pixelFormat, nuint width, nuint height, byte mipmapped);

        // encodeSignalEvent:value: - an object plus a uint64_t (M-F1).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidPtrULong(IntPtr receiver, IntPtr sel, IntPtr a, ulong b);

        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial ulong MsgSendULong(IntPtr receiver, IntPtr sel);

        // waitUntilSignaledValue:timeoutMS: - uint64_t plus uint32_t, returning BOOL (M-F1).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial byte MsgSendBoolULongUInt(IntPtr receiver, IntPtr sel, ulong a, uint b);

        // The ARRAY setters (M-R6): two caller-owned arrays plus an NSRange by value. NSRange is a 16-byte
        // composite of two NSUIntegers, so on arm64 it rides two general registers rather than memory, and
        // getting that wrong shifts every subsequent argument.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidArrayRange(
            IntPtr receiver, IntPtr sel, IntPtr* objects, nuint* offsets, NSRange range);

        // The sibling with no offsets array: setFragmentTextures:withRange: and setFragmentSamplerStates:withRange:.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidObjectsRange(IntPtr receiver, IntPtr sel, IntPtr* objects, NSRange range);

        // setVertexBufferOffset:atIndex: (M-R7), the offsets-only rebind the shadow pass issues thousands of
        // times a frame.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidNUInt2(IntPtr receiver, IntPtr sel, nuint a, nuint b);

        // setViewport: - six doubles. A homogeneous float aggregate of six members, so the whole struct rides
        // SIMD registers on arm64 and never touches the stack.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidViewport(IntPtr receiver, IntPtr sel, MTLViewport viewport);

        // setScissorRect: - four NSUIntegers. NOT a homogeneous float aggregate and larger than 16 bytes, so the
        // platform ABI passes it indirectly. It is here precisely because it takes the OTHER path from the
        // viewport above, and a hand-rolled layer that gets one right can still get the other wrong.
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidScissor(IntPtr receiver, IntPtr sel, MTLScissorRect rect);

        // setClearColor: - four doubles, the third by-value shape (M-A2 folds every clear into one of these).
        [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
        [SupportedOSPlatform("macos")]
        internal static partial void MsgSendVoidClearColor(IntPtr receiver, IntPtr sel, MTLClearColor color);

        // ---- By-value structs -------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct NSRange
        {
            internal nuint Location;
            internal nuint Length;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MTLViewport
        {
            internal double OriginX;
            internal double OriginY;
            internal double Width;
            internal double Height;
            internal double ZNear;
            internal double ZFar;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MTLScissorRect
        {
            internal nuint X;
            internal nuint Y;
            internal nuint Width;
            internal nuint Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MTLClearColor
        {
            internal double Red;
            internal double Green;
            internal double Blue;
            internal double Alpha;
        }

        // An Objective-C block, which is what addCompletedHandler: takes and the only reason this file needs
        // unsafe at all. The layout is the ABI's, not ours: isa, flags, reserved, invoke, descriptor. M-F3 puts
        // an [UnmanagedCallersOnly] static in the invoke slot, so there is no delegate, no
        // Marshal.GetFunctionPointerForDelegate and no GC handle anywhere on the completion path.
        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockLiteral
        {
            internal IntPtr Isa;
            internal int Flags;
            internal int Reserved;
            internal IntPtr Invoke;
            internal IntPtr Descriptor;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BlockDescriptor
        {
            internal nuint Reserved;
            internal nuint Size;
        }
    }
}
