using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>MTLCompileOptions</c> handle, and the three properties decision M-S6 pins.
    /// <para>
    /// OWNED, NOT AUTORELEASED. <see cref="New"/> goes through <c>+alloc</c> and <c>-init</c>, so it hands back a
    /// +1 object the caller <see cref="Release"/>s. That is the create rule rather than a preference: the
    /// alternative spelling <c>+[MTLCompileOptions new]</c> is also +1, and going through alloc/init keeps this
    /// file's ownership readable from the selectors alone.
    /// </para>
    /// <para>
    /// THE INCUMBENT PASSES A DEFAULT-CONSTRUCTED ONE OF THESE, which is what makes every property here a fact
    /// about the runner rather than a choice, and what <c>MslCompilePin</c> exists to end.
    /// <c>fastMathEnabled</c> moves every pixel and <c>languageVersion</c> drifts with the OS image.
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct MTLCompileOptions(IntPtr Handle)
    {
        /// <summary>True when there is no options object, which means this runtime has no
        /// <c>MTLCompileOptions</c> class at all.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary>A fresh options object at +1, or nil when the class is absent.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MTLCompileOptions New()
        {
            IntPtr cls = ObjCRuntime.ClassNamed("MTLCompileOptions");
            if (cls == IntPtr.Zero) return new MTLCompileOptions(IntPtr.Zero);

            IntPtr allocated = ObjCMsgSend.Send(cls, ObjCRuntime.Sel("alloc"));
            return new MTLCompileOptions(ObjCMsgSend.Send(allocated, ObjCRuntime.Sel("init")));
        }

        /// <summary>Release these options. Only ever called on a handle that arrived at +1.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void Release()
        {
            if (Handle != IntPtr.Zero) ObjCRuntime.ObjcRelease(Handle);
        }

        /// <summary><c>-setLanguageVersion:</c>. An <c>MTLLanguageVersion</c> is an <c>NSUInteger</c> carrying
        /// the packed <c>(major &lt;&lt; 16) | minor</c>.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetLanguageVersion(uint packed)
            => ObjCMsgSend.SendVoidNUInt(Handle, ObjCRuntime.Sel("setLanguageVersion:"), packed);

        /// <summary><c>-setFastMathEnabled:</c>. The newer spelling is <c>-setMathMode:</c>, and row 1 measured
        /// that the two AGREE on this OS, which is what keeps this a pin on the real setting rather than on a
        /// shim nothing reads.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetFastMathEnabled(bool enabled)
            => ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setFastMathEnabled:"), enabled ? (byte)1 : (byte)0);

        /// <summary><c>-setPreserveInvariance:</c>. Written even though the pinned value equals the default,
        /// because a property set explicitly cannot be moved by an OS whose default changes.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetPreserveInvariance(bool preserve)
            => ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setPreserveInvariance:"),
                preserve ? (byte)1 : (byte)0);
    }
}
