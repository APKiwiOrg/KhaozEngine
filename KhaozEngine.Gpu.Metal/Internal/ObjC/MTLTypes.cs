using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLSize</c>: three <c>NSUInteger</c>s, the extent of a copy region.
    /// <para>
    /// TWENTY-FOUR BYTES OF INTEGERS, WHICH DECIDES HOW IT CROSSES. The arm64 rule row 1's spike measured is that
    /// a homogeneous FLOATING-POINT aggregate of at most four members rides the vector registers and everything
    /// else over sixteen bytes is passed INDIRECTLY, as a pointer the caller supplies. This is not floating point
    /// at all, so it takes the indirect path for the same reason <c>MTLScissorRect</c> does, which is the shape
    /// the spike measured under that arm (section 3.1).
    /// </para>
    /// </summary>
    /// <param name="Width">Extent on X, in texels for a texture copy.</param>
    /// <param name="Height">Extent on Y.</param>
    /// <param name="Depth">Extent on Z, which is 1 for every texture the GPU seam can express.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLSize(nuint Width, nuint Height, nuint Depth);

    /// <summary>
    /// <c>MTLOrigin</c>: three <c>NSUInteger</c>s, the corner a copy region starts at. Same size, same layout and
    /// therefore the same ABI class as <see cref="MTLSize"/>.
    /// </summary>
    /// <param name="X">Left edge.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Z">Depth slice, which is 0 for every texture the GPU seam can express.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLOrigin(nuint X, nuint Y, nuint Z);

    /// <summary>
    /// <c>MTLClearColor</c>: four doubles, the value an attachment whose <c>loadAction</c> is
    /// <see cref="MTLLoadAction.Clear"/> is filled with (M-A2).
    /// <para>
    /// THE ONE SHAPE IN THIS FOLDER THAT RIDES THE REGISTERS, and the only one whose VALUE row 1's spike checked
    /// rather than only its acceptance. Four is the arm64 limit for a homogeneous floating-point aggregate, so
    /// this lands in <c>d0</c> to <c>d3</c> and never touches the stack, where <see cref="MTLSize"/> and
    /// <c>MTLScissorRect</c> both go indirectly. The spike cleared a 64x64 target to
    /// <c>(0.25, 0.5, 0.75, 1.0)</c> and read <c>(191, 128, 64, 255)</c> back through a blit, which is what
    /// separates a correctly passed struct from one whose members landed in the wrong registers and did not
    /// happen to fault (section 3.1).
    /// </para>
    /// <para>
    /// THE CHANNELS ARE ZERO TO ONE AND UNCLAMPED BY THIS TYPE, which is Metal's own contract: the runtime
    /// converts to the attachment's pixel format at the clear. The engine's <c>Color</c> is already four floats
    /// in that range, so <c>MetalRenderApi</c> widens each to a double and nothing rescales.
    /// </para>
    /// </summary>
    /// <param name="Red">Red channel, 0 to 1.</param>
    /// <param name="Green">Green channel.</param>
    /// <param name="Blue">Blue channel.</param>
    /// <param name="Alpha">Alpha channel.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLClearColor(double Red, double Green, double Blue, double Alpha);

    /// <summary>
    /// <c>MTLViewport</c>: six doubles, the rectangle and depth range a render encoder rasterises into.
    /// <para>
    /// SIX DOUBLES IS ONE TOO MANY TO BE AN HFA, however homogeneous it looks, so this is an ordinary composite
    /// over sixteen bytes. That would make it an INDIRECT by-value argument, and this backend never passes it
    /// that way at all: M-A7 takes <c>setViewports:count:</c>, which crosses a POINTER to an array of these plus
    /// a count, so the composite never rides a register file and the by-value question does not arise. The
    /// layout still has to be exact, because the driver reads the array through that pointer.
    /// </para>
    /// <para>
    /// <see cref="Height"/> IS POSITIVE, unlike the Vulkan sibling's. Metal's clip space already matches the
    /// engine's, so <c>GpuCapabilities.ClipSpaceYInverted</c> is false, <c>GpuClip.Correct</c> is the identity,
    /// and there is no negative-height trick to reproduce. A reader arriving from phase 3 will look for one.
    /// </para>
    /// </summary>
    /// <param name="OriginX">Left edge in pixels.</param>
    /// <param name="OriginY">Top edge in pixels.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels, POSITIVE.</param>
    /// <param name="ZNear">Near plane, 0 for every shipped pass.</param>
    /// <param name="ZFar">Far plane, 1 for every shipped pass.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLViewport(
        double OriginX, double OriginY, double Width, double Height, double ZNear, double ZFar);

    /// <summary>
    /// <c>MTLScissorRect</c>: four <c>NSUInteger</c>s, the rectangle outside which a render encoder discards
    /// fragments.
    /// <para>
    /// NOT FLOATING POINT AT ALL, so it could never be an HFA and would go indirectly by value. Like
    /// <see cref="MTLViewport"/> it never crosses that way here, because <c>setScissorRects:count:</c> takes a
    /// pointer and a count (M-A7).
    /// </para>
    /// <para>
    /// METAL HAS NO SCISSOR-TEST ENABLE. The rectangle is always live and defaults to the whole attachment, so
    /// whether one is emitted at all is decided by the SEAM's <c>ScissorTestEnabled</c> rasterizer state rather
    /// than by anything in this struct. <c>MetalRenderPassSchedule</c> is where that gate lives.
    /// </para>
    /// </summary>
    /// <param name="X">Left edge in pixels.</param>
    /// <param name="Y">Top edge in pixels. NOT flipped: a scissor is a framebuffer-space rectangle with no clip
    /// space to correct for.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct MTLScissorRect(nuint X, nuint Y, nuint Width, nuint Height);

    /// <summary>
    /// <c>NSRange</c>: two <c>NSUInteger</c>s, the CONTIGUOUS run of argument-table indices an array setter
    /// writes (M-R6). Every one of the six array setters ends <c>withRange:</c> and takes one of these BY VALUE,
    /// so this is the type the whole bind flush is expressed through.
    /// <para>
    /// SIXTEEN BYTES OF INTEGERS, WHICH IS THE ONE SIZE THAT DOES NOT GO INDIRECTLY. The arm64 rule the other
    /// composites in this file are governed by is that anything over sixteen bytes and not a homogeneous
    /// floating-point aggregate is passed as a pointer the caller supplies. This is exactly sixteen, so it rides
    /// TWO general-purpose registers instead, which is a third class from either
    /// <see cref="MTLClearColor"/>'s register file or <see cref="MTLSize"/>'s indirect path. Getting it wrong
    /// does not fault: it shifts every argument after it, and on
    /// <c>setVertexBuffers:offsets:withRange:</c> there is nothing after it to notice.
    /// </para>
    /// <para>
    /// MEASURED RATHER THAN REASONED. Row 1's interop spike sent
    /// <c>setVertexBuffers:offsets:withRange:</c>, <c>setFragmentBuffers:offsets:withRange:</c>,
    /// <c>setFragmentTextures:withRange:</c>, <c>setFragmentSamplerStates:withRange:</c> and the compute
    /// <c>setBuffers:offsets:withRange:</c> against a real device with this layout, in one command buffer that
    /// completed with a nil error (section 3.1). <c>MetalInteropSpike.Native.cs</c> keeps its own copy of the
    /// declaration deliberately, as the measurement, and this is the shipped one.
    /// </para>
    /// </summary>
    /// <param name="Location">The first index the setter writes.</param>
    /// <param name="Length">How many consecutive indices it writes, which is the length of the arrays handed
    /// alongside it. Metal reads exactly this many entries through those pointers.</param>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct NSRange(nuint Location, nuint Length);
}
