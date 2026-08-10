using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// THE RUNTIME SHIM, and the bottom of decision M-P2's interop layer: <c>objc_getClass</c>,
    /// <c>sel_registerName</c>, <c>objc_retain</c>, <c>objc_release</c> and the autorelease-pool pair. Everything
    /// else in this folder is a handle type over <see cref="IntPtr"/> that sends messages through
    /// <see cref="ObjCMsgSend"/>, and every one of those needs a selector from here first.
    /// <para>
    /// THE FOLDER'S RULE IS ONE FILE PER OBJECTIVE-C CLASS, which is what section 3.1 decides and what the
    /// vendored fork this design reads as its reference already does. A file carries its class's handle type,
    /// that class's selectors, and the enums those selectors take, because an enum with no class to belong to is
    /// how a second copy of one starts. THREE files are exceptions, and each is a file with no class to be about.
    /// This one is the C runtime. <see cref="ObjCMsgSend"/> is the one dispatch function every class goes
    /// through. And <c>MTLTypes.cs</c> carries the plain C value structs (<see cref="MTLSize"/> and
    /// <see cref="MTLOrigin"/>) that a class takes by value and no class owns.
    /// </para>
    /// <para>
    /// EVERY IMPORT IS <c>[LibraryImport]</c> WITH BLITTABLE-ONLY SIGNATURES, so the source generator emits a
    /// direct call with no marshalling stub. That is M-P2's mechanism and, under this repo's warnings-as-errors
    /// rule, also what the SYSLIB1054 analyzer requires. Strings are where it bites: a <c>string</c> parameter
    /// would generate a stub, so <c>sel_registerName</c> and <c>objc_getClass</c> take <c>byte*</c> and the
    /// caller encodes ASCII itself.
    /// </para>
    /// <para>
    /// NOTHING HERE RUNS OFF macOS, and nothing here runs at type load either. There is deliberately no
    /// <c>static readonly</c> selector field anywhere in this folder: a static initializer would P/Invoke into
    /// libobjc the moment the type was touched, including on the Linux and Windows legs where this assembly is
    /// referenced and its device-free tests run. Selectors are fetched through <see cref="Sel"/> instead, which
    /// is only ever reached from a body already behind the platform guard.
    /// </para>
    /// </summary>
    internal static unsafe partial class ObjCRuntime
    {
        /// <summary>The Objective-C runtime itself. An absolute path rather than a name, because that is what the
        /// existing <c>MetalFrameCapture</c> in <c>KhaozEngine.Gpu</c> uses and because it removes the loader
        /// search entirely.</summary>
        internal const string Objc = "/usr/lib/libobjc.A.dylib";

        /// <summary>The Metal framework binary, for its handful of C entry points (<c>MTLCreateSystemDefaultDevice</c>
        /// and <c>MTLCopyAllDevices</c>, both declared on <see cref="MTLDevice"/> because both produce one).</summary>
        internal const string MetalFramework = "/System/Library/Frameworks/Metal.framework/Metal";

        // THE SELECTOR CACHE. sel_registerName is already a hash lookup inside libobjc, so this is not about
        // saving the call: it is about what the call sites are allowed to look like. Later rows send messages on
        // a frame path, and a managed dictionary hit is cheaper than a P/Invoke transition plus a strlen, so
        // Sel("setVertexBuffers:offsets:withRange:") at a bind site stays honest rather than needing a hand-hoisted
        // field. A field would be the obvious alternative and it is the one thing this file may not have: a
        // static initializer would call into libobjc at type load, on every platform.
        static readonly ConcurrentDictionary<string, IntPtr> _selectors = new(StringComparer.Ordinal);

        [LibraryImport(Objc, EntryPoint = "sel_registerName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr SelRegisterName(byte* name);

        [LibraryImport(Objc, EntryPoint = "objc_getClass")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjcGetClass(byte* name);

        /// <summary>The class name of a live object, as the runtime knows it. Read for diagnostics only: it is
        /// what tells <c>AGXG14CDevice</c> from <c>MTLDebugDevice</c>, which is the control behind M-G3's
        /// measured answer.</summary>
        [LibraryImport(Objc, EntryPoint = "object_getClassName")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjectGetClassName(IntPtr obj);

        [LibraryImport(Objc, EntryPoint = "objc_retain")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr ObjcRetain(IntPtr obj);

        [LibraryImport(Objc, EntryPoint = "objc_release")]
        [SupportedOSPlatform("macos")]
        internal static partial void ObjcRelease(IntPtr obj);

        // The pool pair (M-N5). Push and pop rather than an NSAutoreleasePool object, because that is the
        // supported spelling and it needs no class lookup. Wrapped by ObjCAutoreleasePool, which is what call
        // sites use and what the architecture test looks for.
        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPush")]
        [SupportedOSPlatform("macos")]
        internal static partial IntPtr AutoreleasePoolPush();

        [LibraryImport(Objc, EntryPoint = "objc_autoreleasePoolPop")]
        [SupportedOSPlatform("macos")]
        internal static partial void AutoreleasePoolPop(IntPtr pool);

        /// <summary>The selector for <paramref name="name"/>, registered once per process and cached. Selectors
        /// never expire, so caching one is safe for the life of the process by the runtime's own contract.</summary>
        [SupportedOSPlatform("macos")]
        internal static IntPtr Sel(string name)
        {
            if (_selectors.TryGetValue(name, out IntPtr cached)) return cached;

            IntPtr registered = Register(name);
            // GetOrAdd would be the shorter spelling and would still be correct, because sel_registerName is
            // idempotent and two threads racing here get the same pointer back. TryAdd is used instead so the
            // common path above is a plain read with no delegate allocation on it.
            _selectors.TryAdd(name, registered);
            return registered;
        }

        /// <summary>The class object for <paramref name="name"/>, or <see cref="IntPtr.Zero"/> when this runtime
        /// has no such class. Zero is a real answer rather than a failure: a class that ships on a later macOS is
        /// absent here, which is exactly what a caller checks for before sending it a message.</summary>
        [SupportedOSPlatform("macos")]
        internal static IntPtr ClassNamed(string name)
        {
            fixed (byte* p = Ascii(name)) return ObjcGetClass(p);
        }

        /// <summary>The runtime's own class name for <paramref name="obj"/>, empty for nil.</summary>
        [SupportedOSPlatform("macos")]
        internal static string ClassNameOf(IntPtr obj)
        {
            if (obj == IntPtr.Zero) return "";
            IntPtr name = ObjectGetClassName(obj);
            return name == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(name) ?? "";
        }

        [SupportedOSPlatform("macos")]
        static IntPtr Register(string name)
        {
            fixed (byte* p = Ascii(name)) return SelRegisterName(p);
        }

        // ASCII into a heap array rather than a stackalloc, so the fixed statement at the call site owns the
        // lifetime and nothing here depends on an inlining decision. Every name passed in is a compile-time
        // constant and the cache above means each one is encoded once per process.
        static byte[] Ascii(string text)
        {
            var bytes = new byte[Encoding.ASCII.GetByteCount(text) + 1];
            Encoding.ASCII.GetBytes(text, bytes);
            return bytes;
        }
    }
}
