using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// An <c>NSView</c> handle, and the ONLY Cocoa class this backend sends messages to beyond
    /// <see cref="NSWindow"/>. Four selectors, all of them part of the incumbent's adopt-or-create dance (M-W1):
    /// read the frame for the size, read the layer, ask whether it is already a <c>CAMetalLayer</c>, and if it is
    /// not, set <c>wantsLayer</c> and attach a fresh one.
    ///
    /// <para><b>THE FRAME IS IN POINTS AND THE DRAWABLE SIZE IS IN PIXELS, and the incumbent's NSView arm writes
    /// one straight into the other.</b> That half is NOT reproduced
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/605">#605</see>): the scale comes off
    /// <see cref="NSWindow.BackingScaleFactor"/> and the multiply happens in <c>MetalSwapchainPolicy</c>, which is
    /// where the decision and its whole argument live. The short version is that the incumbent's own UIView arm
    /// already multiplies, so the two arms of one constructor disagree and only one of them can be right.</para>
    ///
    /// <para><b>NOTHING HERE IS OWNED.</b> A content view belongs to its window and a layer read off a view
    /// belongs to the view, so this type retains nothing and releases nothing. The one reference the swapchain
    /// does own is its layer, taken explicitly in <c>MetalSwapchainApi</c>.</para>
    /// </summary>
    /// <param name="Handle">The Objective-C object, or <see cref="IntPtr.Zero"/> for nil.</param>
    internal readonly record struct NSView(IntPtr Handle)
    {
        /// <summary>True when there is no view, which is what a window handle that is not an <c>NSWindow</c>
        /// degrades to rather than a crash.</summary>
        internal bool IsNull => Handle == IntPtr.Zero;

        /// <summary><c>-frame</c>. Its SIZE is the only half read, in points. See the type remarks.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal CGRect Frame() => ObjCMsgSend.SendCGRect(Handle, ObjCRuntime.Sel("frame"));

        /// <summary><c>-layer</c>, borrowed from the view. Nil on a view that is not layer-backed, which is what
        /// <see cref="SetWantsLayer"/> exists to change.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal IntPtr Layer() => ObjCMsgSend.Send(Handle, ObjCRuntime.Sel("layer"));

        /// <summary><c>-setWantsLayer:</c>. Set true before attaching a layer, which is the order the incumbent
        /// uses and the order AppKit documents: a view told to want a layer after one was assigned makes its own
        /// and discards the assignment.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetWantsLayer(bool value)
            => ObjCMsgSend.SendVoidBool(Handle, ObjCRuntime.Sel("setWantsLayer:"), value ? (byte)1 : (byte)0);

        /// <summary><c>-setLayer:</c>. The view retains what it is given, which is why the swapchain's own
        /// reference to a layer it CREATED is the +1 out of <c>alloc</c>/<c>init</c> and nothing more.</summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal void SetLayer(IntPtr layer)
            => ObjCMsgSend.SendVoidPtr(Handle, ObjCRuntime.Sel("setLayer:"), layer);
    }
}
