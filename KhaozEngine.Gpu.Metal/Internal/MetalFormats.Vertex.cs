using System;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE VERTEX-INPUT DOMAIN of the format map (see <c>MetalFormats.Pixel.cs</c> for the split and its reason).
    /// The component format an attribute is read as, and the size that packs one slot's elements.
    ///
    /// <para><b>THE SIZE LIVES HERE RATHER THAN IN THE PLAN THAT USES IT</b>, because it is the same kind of fact
    /// as the format: a property of <see cref="GpuVertexElementFormat"/> and of nothing else. Both siblings put it
    /// in the same place for the same reason (<c>VulkanVertexInput.SizeInBytes</c>,
    /// <c>D3D11InputLayoutPlan</c>), and the two answers have to agree element for element or the same mesh reads
    /// differently on two backends.</para>
    ///
    /// <para><b>THE SEAM HAS NO PER-ELEMENT OFFSET, so packing IS the layout.</b> An element sits immediately
    /// after the one before it in the same slot, and a slot that declares a non-zero stride keeps it, which is how
    /// an interleaved buffer with padding survives. <c>MetalVertexPlan</c> is where that arithmetic runs.</para>
    /// </summary>
    internal static partial class MetalFormats
    {
        /// <summary>
        /// <c>MTLFormats.VdToMTLVertexFormat</c>, restricted to the four members
        /// <see cref="GpuVertexElementFormat"/> has.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam format this map has not been taught. Every
        /// declared member is listed, so this is a new <see cref="GpuVertexElementFormat"/> rather than a caller
        /// error, and guessing at one would read every vertex of the stream through the wrong component
        /// count.</exception>
        internal static MTLVertexFormat ToVertexFormat(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => MTLVertexFormat.Float,
            GpuVertexElementFormat.Float2 => MTLVertexFormat.Float2,
            GpuVertexElementFormat.Float3 => MTLVertexFormat.Float3,
            GpuVertexElementFormat.Float4 => MTLVertexFormat.Float4,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "The native Metal backend has no MTLVertexFormat for that GPU seam vertex element format."),
        };

        /// <summary>Component size in bytes, which is what packs a slot's elements and what a zero stride sums
        /// to.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A seam format this map has no size for.</exception>
        internal static uint VertexElementSize(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => 4,
            GpuVertexElementFormat.Float2 => 8,
            GpuVertexElementFormat.Float3 => 12,
            GpuVertexElementFormat.Float4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "The native Metal backend has no size for that GPU seam vertex element format."),
        };
    }
}
