using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE ENTRY OF THE <c>MTLVertexDescriptor</c>'s <c>layouts</c> ARRAY, resolved: which buffer index this slot
    /// is bound at, its stride, and how it advances.
    /// </summary>
    /// <param name="BufferIndex">The <c>[[buffer(n)]]</c> index, which is M-B2's top-pinned stream index. Row 14's
    /// <c>SetVertexBuffer</c> binds at the SAME number, and the two agreeing is the whole of what the scheme
    /// owes.</param>
    /// <param name="Stride">Bytes between consecutive elements: the declared stride when the layout gave one, and
    /// the packed sum of its element sizes when it did not.</param>
    /// <param name="StepFunction">Per vertex or per instance.</param>
    /// <param name="StepRate">Elements per step, raised to at least 1 because Metal rejects 0.</param>
    internal readonly record struct MetalVertexStream(
        uint BufferIndex, uint Stride, MTLVertexStepFunction StepFunction, uint StepRate);

    /// <summary>
    /// ONE ENTRY OF THE <c>MTLVertexDescriptor</c>'s <c>attributes</c> ARRAY, resolved: which shader attribute
    /// reads which buffer slot at which offset.
    /// </summary>
    /// <param name="AttributeIndex">The <c>[[attribute(n)]]</c> index, which counts across every slot rather than
    /// within one.</param>
    /// <param name="BufferIndex">The buffer index of the slot it is read from, already top-pinned.</param>
    /// <param name="Format">The component format, turned into an <c>MTLVertexFormat</c> at the descriptor.</param>
    /// <param name="OffsetBytes">Byte offset within its own slot.</param>
    internal readonly record struct MetalVertexAttribute(
        uint AttributeIndex, uint BufferIndex, GpuVertexElementFormat Format, uint OffsetBytes);

    /// <summary>
    /// THE VERTEX INPUT STATE, COMPUTED FROM THE SEAM'S OWN LAYOUTS AND M-B2's NUMBERING, WITH NO DEVICE
    /// ANYWHERE. What <c>MetalGraphicsPipeline</c> writes into an <c>MTLVertexDescriptor</c>, and what row 14
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) reads to bind a stream at the matching index.
    ///
    /// <para><b>THE ATTRIBUTE INDEX COUNTS ACROSS ALL SLOTS, NOT WITHIN ONE.</b> Slot 1's first element continues
    /// where slot 0's last one left off, because SPIRV-Cross emits <c>[[attribute(n)]]</c> from the GLSL
    /// <c>location</c>, which is a single flat sequence over every vertex input the shader declares and knows
    /// nothing about which buffer an attribute arrives in. Both siblings apply the same rule from the same shared
    /// GLSL (<c>VulkanVertexInput</c>'s location, <c>D3D11InputLayoutPlan</c>'s semantic index), the incumbent's
    /// own <c>element</c> counter does the same thing, and getting it wrong reads the instance buffer's first
    /// attribute as the vertex buffer's second.</para>
    ///
    /// <para><b>OFFSETS ARE PACKED AND STRIDES ARE DECLARED-OR-COMPUTED.</b> The seam has no per-element offset,
    /// so an element sits immediately after the one before it in the same slot. A layout declaring a non-zero
    /// <see cref="GpuVertexLayoutDescription.Stride"/> keeps it, which is how an interleaved buffer with padding
    /// survives, and a zero stride is the sum of that slot's element sizes, which is what almost every shipped
    /// call site relies on.</para>
    ///
    /// <para><b>A STEP RATE ABOVE 1 IS HONOURED HERE, UNLIKE ON VULKAN.</b> Metal's
    /// <c>MTLVertexBufferLayoutDescriptor</c> has a real <c>stepRate</c>, so a divisor needs no extension and
    /// nothing is refused. <c>VulkanVertexInput.RequireExpressibleRate</c> refuses one because Vulkan's core
    /// vertex input rate is two-valued, which is a genuine difference between the two APIs rather than a
    /// divergence between the two backends. Every shipped instance stream declares a rate of exactly 1
    /// anyway.</para>
    ///
    /// <para><b>THE EMPTY CASE IS THE FULLSCREEN ONE AND IS LEGAL.</b> A pass that builds its geometry from
    /// <c>gl_VertexIndex</c> declares no vertex layouts at all, which yields no streams and no attributes. Six
    /// shipped renderers are in that shape.</para>
    /// </summary>
    internal static class MetalVertexPlan
    {
        /// <summary>
        /// Flatten <paramref name="layouts"/> into the streams and the attributes an
        /// <c>MTLVertexDescriptor</c> is written from.
        /// </summary>
        /// <param name="layouts">One layout per vertex buffer slot, in slot order. Null or empty is the fullscreen
        /// case.</param>
        /// <param name="attributes">The attributes, in attribute-index order.</param>
        /// <returns>The streams, indexed by the seam's vertex buffer SLOT, so row 14 can map a
        /// <c>SetVertexBuffer(slot, ...)</c> onto the buffer index this chose.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An element declares a format with no Metal vertex
        /// format.</exception>
        internal static MetalVertexStream[] Build(IReadOnlyList<GpuVertexLayoutDescription>? layouts,
            out MetalVertexAttribute[] attributes)
        {
            if (layouts is null || layouts.Count == 0)
            {
                attributes = [];
                return [];
            }

            int total = 0;
            for (int slot = 0; slot < layouts.Count; slot++) total += ElementsOf(layouts[slot]).Length;

            var streams = new MetalVertexStream[layouts.Count];
            attributes = new MetalVertexAttribute[total];

            uint attribute = 0;
            int next = 0;
            for (int slot = 0; slot < layouts.Count; slot++)
            {
                GpuVertexLayoutDescription layout = layouts[slot];
                uint bufferIndex = MetalVertexStreams.IndexOf(slot);

                GpuVertexElement[] declared = ElementsOf(layout);
                uint offset = 0;
                for (int i = 0; i < declared.Length; i++)
                {
                    attributes[next++] = new MetalVertexAttribute(
                        attribute++, bufferIndex, declared[i].Format, offset);

                    // The size map throws for a format Metal has no vertex format for, so the packing and the
                    // descriptor write cannot disagree about which formats exist.
                    offset += MetalFormats.VertexElementSize(declared[i].Format);
                }

                streams[slot] = new MetalVertexStream(
                    bufferIndex,
                    layout.Stride != 0 ? layout.Stride : offset,
                    layout.InstanceStepRate != 0 ? MTLVertexStepFunction.PerInstance
                        : MTLVertexStepFunction.PerVertex,

                    // The incumbent's own Math.Max(1, stepRate), reproduced: a per-vertex layout declares a rate
                    // of 0 and Metal rejects 0 on any layout, so the floor applies to both arms.
                    Math.Max(1u, layout.InstanceStepRate));
            }

            return streams;
        }

        static GpuVertexElement[] ElementsOf(in GpuVertexLayoutDescription layout) => layout.Elements ?? [];
    }
}
