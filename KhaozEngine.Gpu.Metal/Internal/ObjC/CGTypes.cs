using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>CGSize</c>: two <c>CGFloat</c>s, which are DOUBLES on 64-bit. The type <c>CAMetalLayer.drawableSize</c>
    /// is written and read in.
    /// <para>
    /// TWO DOUBLES IS AN arm64 HOMOGENEOUS FLOATING-POINT AGGREGATE, well inside the four-member limit, so it
    /// rides <c>d0</c> and <c>d1</c> in both directions and never touches the stack. That is the same register
    /// file and the same rule <see cref="MTLClearColor"/>'s four doubles ride under, and row 1's spike checked
    /// that arm by VALUE rather than by acceptance (section 3.1), so this shape inherits a measured answer rather
    /// than needing one of its own. The spike separately round-tripped a single <c>CGFloat</c> through
    /// <c>-setContentsScale:</c> and read <c>2.0</c> back, which is the width half of the same question.
    /// </para>
    /// </summary>
    /// <param name="Width">Width, in pixels for a drawable size and in points for a view frame.</param>
    /// <param name="Height">Height, in the same units as <paramref name="Width"/>.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct CGSize(double Width, double Height);

    /// <summary>
    /// <c>CGPoint</c>: two <c>CGFloat</c>s. Present only because it is the first half of <see cref="CGRect"/>,
    /// whose layout has to be exact for the size in the second half to land where the caller reads it.
    /// </summary>
    /// <param name="X">Left edge, in the containing coordinate space.</param>
    /// <param name="Y">Bottom edge, in Cocoa's own bottom-left origin.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct CGPoint(double X, double Y);

    /// <summary>
    /// <c>CGRect</c>: an origin and a size, four <c>CGFloat</c>s in all, which is what <c>-[NSView frame]</c>
    /// returns and the only thing this backend reads one for.
    /// <para>
    /// FOUR DOUBLES IS EXACTLY THE arm64 HFA LIMIT, so it comes back in <c>d0</c> to <c>d3</c> and there is no
    /// <c>objc_msgSend_stret</c> path to write. That matters because the incumbent's own binding DOES branch on
    /// the architecture here (<c>Veldrid.MetalBindings.NSView.frame</c> picks <c>objc_msgSend_stret</c> off
    /// arm64), and reproducing that branch would be reproducing a fork of a fork: <c>objc_msgSend_stret</c> does
    /// not exist on arm64 at all, which <see cref="ObjCMsgSend"/>'s own header records as the reason no stret
    /// path is written anywhere in this folder.
    /// </para>
    /// <para>
    /// THE ORIGIN IS READ AND DISCARDED, deliberately. A content view's frame origin is its position inside the
    /// window, and the only number the swapchain wants out of it is the SIZE. It is carried because dropping it
    /// from the struct would change where the size lands in the return registers.
    /// </para>
    /// </summary>
    /// <param name="Origin">The rectangle's corner.</param>
    /// <param name="Size">The rectangle's extent, which is the half this backend reads.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct CGRect(CGPoint Origin, CGSize Size);
}
