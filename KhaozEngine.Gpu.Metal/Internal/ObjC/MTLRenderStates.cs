// THE RENDER-ENCODER STATE ENUMS, IN ONE FILE, WHICH IS THE SAME CARVE-OUT MTLTypes.cs ALREADY TAKES. This
// folder's rule is one file per Objective-C CLASS, so that a class's selectors and its own enums have one home.
// None of these five is a class: they are the argument VALUES of the render command encoder's state setters, and
// giving each its own file would be five headers around five integer lists with nothing to say in them.
//
// AND THEY LAND HERE, WITH THE PIPELINE, BECAUSE THE PIPELINE IS WHERE THEY ARE RESOLVED. A pipeline turns the
// seam's rasterizer and depth state into these values once at creation. The SETTERS that consume them
// (-setCullMode:, -setFrontFacing:, -setTriangleFillMode:, -setDepthClipMode:) are deliberately NOT added to
// MTLRenderCommandEncoder by this row, because their caller is the pre-draw state block, which lands with the
// draw row (https://github.com/APKiwiOrg/KhaozEngine/issues/580). A native prototype with no caller is an
// Objective-C declaration nobody has ever executed, and a wrong ABI assumption there is a memory corruption
// rather than a compile error, which is the rule MTLRenderCommandEncoder's own header states.

namespace KhaozEngine.Gpu.Metal.Internal.ObjC
{
    /// <summary>
    /// <c>MTLCullMode</c>, an <c>NSUInteger</c>. The full set, because the map in <c>MetalFormats</c> is total over
    /// <see cref="GpuFaceCull"/> and reads better against a complete table than against a subset.
    /// </summary>
    internal enum MTLCullMode : ulong
    {
        /// <summary>Cull nothing.</summary>
        None = 0,

        /// <summary>Cull front faces.</summary>
        Front = 1,

        /// <summary>Cull back faces, which is what the 3D model pass asks for.</summary>
        Back = 2,
    }

    /// <summary><c>MTLWinding</c>, an <c>NSUInteger</c>. Which winding order the rasterizer treats as front.</summary>
    internal enum MTLWinding : ulong
    {
        /// <summary>Clockwise is front, which is what every shipped renderer declares.</summary>
        Clockwise = 0,

        /// <summary>Counter-clockwise is front.</summary>
        CounterClockwise = 1,
    }

    /// <summary><c>MTLTriangleFillMode</c>, an <c>NSUInteger</c>. Two members and no third.</summary>
    internal enum MTLTriangleFillMode : ulong
    {
        /// <summary>Solid triangles.</summary>
        Fill = 0,

        /// <summary>Wireframe, which the seam spells <see cref="GpuPolygonFill.Wireframe"/>.</summary>
        Lines = 1,
    }

    /// <summary>
    /// <c>MTLDepthClipMode</c>, an <c>NSUInteger</c>. Whether geometry outside the near and far planes is clipped
    /// or clamped against them.
    /// <para>
    /// THE SEAM HAS NO MEMBER FOR IT AND THE INCUMBENT DERIVES IT FROM THE DEPTH TEST, which is worth naming
    /// because the derivation looks arbitrary until you see where it comes from.
    /// <c>Veldrid.MTL.MTLPipeline</c> ends its graphics constructor with
    /// <c>DepthClipMode = description.DepthStencilState.DepthTestEnabled ? Clip : Clamp</c>, so a pass with the
    /// depth test off gets clamping. <c>MetalPipelineState</c> reproduces exactly that, because the committed
    /// <c>metal</c> goldens were baked through it.
    /// </para>
    /// </summary>
    internal enum MTLDepthClipMode : ulong
    {
        /// <summary>Clip against the near and far planes.</summary>
        Clip = 0,

        /// <summary>Clamp depth to the near and far planes instead of clipping.</summary>
        Clamp = 1,
    }

    /// <summary>
    /// <c>MTLPrimitiveType</c>, an <c>NSUInteger</c>. The full set, and the seam can express all five.
    /// <para>
    /// IT IS A DRAW ARGUMENT ON THIS API RATHER THAN PIPELINE STATE, which is the one place Metal differs from
    /// both siblings here: Direct3D 11 sets a topology on the input assembler and Vulkan bakes it into the
    /// pipeline, while <c>-drawPrimitives:</c> takes it per call. So the pipeline RESOLVES it once at creation
    /// (a pure map over an enum, done once instead of once per draw) and row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) passes the resolved value to every draw.
    /// </para>
    /// </summary>
    internal enum MTLPrimitiveType : ulong
    {
        /// <summary>Independent points.</summary>
        Point = 0,

        /// <summary>Independent line segments.</summary>
        Line = 1,

        /// <summary>Connected line strip.</summary>
        LineStrip = 2,

        /// <summary>Independent triangles, which is what almost every shipped pipeline draws.</summary>
        Triangle = 3,

        /// <summary>Connected triangle strip.</summary>
        TriangleStrip = 4,
    }

    /// <summary>
    /// <c>MTLIndexType</c>, an <c>NSUInteger</c>: how wide one element of the index buffer is.
    /// <para>
    /// IT IS A DRAW ARGUMENT TOO, and for the same reason the primitive type is: Metal takes the index buffer,
    /// its offset and its element width in <c>-drawIndexedPrimitives:</c> itself rather than binding any of them
    /// beforehand. That is why this backend has no index-buffer argument-table entry and therefore no
    /// index-buffer BIND RECORD, where both siblings have one (section 6.3).
    /// </para>
    /// <para>
    /// THE TWO MEMBERS ARE THE WHOLE SET AND THE SEAM HAS EXACTLY THE SAME TWO, so
    /// <c>GpuIndexFormat</c> maps onto it total and there is no unmappable arm to refuse.
    /// </para>
    /// </summary>
    internal enum MTLIndexType : ulong
    {
        /// <summary><c>MTLIndexTypeUInt16</c>: two bytes per index, which is what every shipped mesh under 65536
        /// vertices uses.</summary>
        UInt16 = 0,

        /// <summary><c>MTLIndexTypeUInt32</c>: four bytes per index.</summary>
        UInt32 = 1,
    }
}
