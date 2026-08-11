using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSWindow</c> handle, ONE selector wide. <c>GpuWindowHandle</c> with
    /// <c>GpuWindowKind.Cocoa</c> carries the <c>NSWindow</c> the windowing package built, and the whole of what
    /// this backend wants from it is its content view.
    /// <para>
    /// THE INCUMBENT ALSO ACCEPTS AN <c>NSView</c> AND A <c>UIView</c> SOURCE, and neither is reproduced.
    /// <c>GpuWindowHandle</c> has no way to express either: <c>Cocoa</c> means an <c>NSWindow</c> at the one site
    /// that builds one, and there is no iOS head in this fleet at all, so a <c>UIView</c> arm would be a code path
    /// with no caller and no test on the one surface that already has no coverage (MM7).
    /// </para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct NSWindow(IntPtr Handle)
    {
        /// <summary><c>-contentView</c>, borrowed from the window.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal NSView ContentView() => new(ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("contentView")));
    }
}
