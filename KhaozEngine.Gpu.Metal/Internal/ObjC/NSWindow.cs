using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSWindow</c> handle, TWO selectors wide. <c>GpuWindowHandle</c> with
    /// <c>GpuWindowKind.Cocoa</c> carries the <c>NSWindow</c> the windowing package built, and what this backend
    /// wants from it is its content view and the scale that turns that view's points into pixels.
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

        /// <summary>
        /// <c>-backingScaleFactor</c>: how many device pixels one point covers on the display this window is
        /// currently on. 1.0 on a non-Retina display, 2.0 on a Retina one, and it CHANGES when the window is
        /// dragged between displays, which is why it is read here rather than assumed anywhere.
        /// <para>
        /// IT IS READ OFF THE WINDOW RATHER THAN THE VIEW, and the choice is deliberate. AppKit offers
        /// <c>-[NSView convertRectToBacking:]</c>, which would do the multiply inside Cocoa and hand back pixels,
        /// and that is the wrong shape for this backend: it would move the one piece of arithmetic that CAN be
        /// asserted on every leg into a selector that can be asserted on none. Reading the scalar keeps
        /// points-times-scale in <see cref="MetalSwapchainPolicy"/>, where it is device-free and tested, and
        /// leaves exactly one number crossing the interop boundary.
        /// </para>
        /// <para>
        /// A NIL RECEIVER ANSWERS 0, because <c>objc_msgSend</c> to nil returns zero. That is the only zero this
        /// member can produce: a non-nil handle that is not an <c>NSWindow</c> raises
        /// <c>doesNotRecognizeSelector</c> rather than answering, and the one call site refuses a zero handle and
        /// a nil content view before reading the scale, so the nil arm is defensive. The policy hardens a zero
        /// into the non-Retina identity anyway, so the failure direction of any future caller is a
        /// correctly-sized window rather than a zero-sized drawable.
        /// </para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal double BackingScaleFactor()
            => ObjCMsgSend.SendDouble(Handle, ObjCRuntime.Sel("backingScaleFactor"));
    }
}
