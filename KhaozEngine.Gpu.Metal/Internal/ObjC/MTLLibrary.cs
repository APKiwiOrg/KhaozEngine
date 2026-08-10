using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLFunction</c> handle: one entry point inside a library, and what a pipeline descriptor's
    /// <c>vertexFunction</c>, <c>fragmentFunction</c> and <c>computeFunction</c> are set to.
    /// <para>
    /// A DISTINCT TYPE FROM A LIBRARY for the reason the whole handle family exists (M-P2): everything on the
    /// Objective-C side is <c>id</c>, so a bare <see cref="IntPtr"/> would let a library be passed where a
    /// function belongs and the failure would be an unrecognised selector at runtime.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLFunction(IntPtr Handle)
    {
        /// <summary>True when there is no function, which is what a library answers for a name it does not
        /// carry.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this function. Only ever called on a handle that arrived at +1 from
        /// <c>-newFunctionWithName:</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }
    }

    /// <summary>
    /// An <c>MTLLibrary</c> handle: one compiled translation unit, and the only thing a function can be got out
    /// of. One per STAGE on this backend, because SPIRV-Cross emits each stage as its own translation unit and
    /// names both entry points <c>main0</c>, which is a duplicate-symbol error if the two texts are compiled
    /// together.
    ///
    /// <para>
    /// OWNERSHIP IS PART OF THE SIGNATURE, because Objective-C's is a naming convention rather than a type. Both
    /// members here follow the <c>new</c> rule and hand back a +1 object the caller <see cref="Release"/>s.
    /// </para>
    /// <para>
    /// <b>THERE IS NO SERIALIZE MEMBER, AND THAT IS A MEASURED ABSENCE RATHER THAN AN OMISSION.</b> Decision M-S7
    /// specified a per-program <c>.metallib</c> written to disk and reloaded through <c>newLibraryWithData:</c>,
    /// and no public API can produce those bytes for a library compiled from source. The private
    /// <c>_MTLLibrary</c> selectors <c>-libraryDataContents</c> and <c>-serializeToURL:error:</c> exist on the
    /// concrete class and are in no SDK header. The public route, compiling with
    /// <c>MTLLibraryTypeDynamic</c> and serializing an <c>MTLDynamicLibrary</c>, produces a library whose
    /// functions are UNQUALIFIED by Apple's own documentation, and asking the reloaded library for one aborts the
    /// process on a <c>validateMTLFunctionType</c> assertion. Section 12.5 carries the measurement and what
    /// replaced the cache. Do not add a serialize member here without re-reading it.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLLibrary(IntPtr Handle)
    {
        /// <summary>True when there is no library, which is what a failed compile answers alongside its
        /// error.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>Release this library. Only ever called on a handle that arrived at +1.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary>
        /// <c>-[MTLDevice newLibraryWithSource:options:error:]</c>: a +1 library the caller releases, or nil with
        /// <paramref name="error"/> describing why.
        /// <para>
        /// THE ERROR IS THE WHOLE DIAGNOSTIC VALUE OF A FAILED COMPILE. Metal reports a syntax error by returning
        /// nil and writing an autoreleased <c>NSError</c> whose localized description carries the source line and
        /// the message, so a caller that ignored it would turn a one-line shader typo into an unexplained nil.
        /// The <c>NSError</c> is valid only until the enclosing pool drains, which is why it comes back as a
        /// handle to be read inside that pool rather than kept.
        /// </para>
        /// </summary>
        /// <param name="device">The device to compile on.</param>
        /// <param name="source">The MSL source.</param>
        /// <param name="options">The pinned compile options.</param>
        /// <param name="error">The <c>NSError</c> on failure, nil on success.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLLibrary NewWithSource(MTLDevice device, NSString source, MTLCompileOptions options,
            out NSError error)
        {
            IntPtr library = ObjCMsgSend.SendPtrPtrPtrOutPtr(
                device.Handle, ObjCRuntime.Sel("newLibraryWithSource:options:error:"),
                source.Handle, options.Handle, out IntPtr raw);

            error = new NSError(raw);
            return new MTLLibrary(library);
        }

        /// <summary>
        /// <c>-newFunctionWithName:</c>: a +1 function the caller releases, or nil when this library carries no
        /// entry point of that name.
        /// <para>
        /// THE NAME IS THE ONE READ OUT OF THE EMISSION (M-S5), never a hardcoded <c>main0</c>. SPIRV-Cross
        /// renames the GLSL <c>main</c> because <c>main</c> is reserved in MSL, and the incumbent gets the name
        /// from a Veldrid layer this backend does not have.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal MTLFunction NewFunction(NSString name)
            => new(ObjCMsgSend.SendPtr(Handle, ObjCRuntime.Sel("newFunctionWithName:"), name.Handle));
    }
}
