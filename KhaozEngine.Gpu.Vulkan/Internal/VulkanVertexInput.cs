using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE <c>VkVertexInputBindingDescription</c>: a vertex buffer slot, its stride, and whether it advances per
    /// vertex or per instance.
    /// </summary>
    /// <param name="Binding">The buffer slot, which is the layout's index in the pipeline's layout list.</param>
    /// <param name="Stride">Bytes between consecutive elements, the declared stride when the layout gave one and
    /// the packed sum of its element sizes when it did not.</param>
    /// <param name="PerInstance">True for <c>VK_VERTEX_INPUT_RATE_INSTANCE</c>.</param>
    internal readonly record struct VulkanVertexBinding(uint Binding, uint Stride, bool PerInstance);

    /// <summary>
    /// ONE <c>VkVertexInputAttributeDescription</c>: a shader input location, the buffer slot it is read from, its
    /// component format and its byte offset inside that slot.
    /// </summary>
    /// <param name="Location">The GLSL <c>layout(location = N)</c> this attribute feeds.</param>
    /// <param name="Binding">The buffer slot it is read from.</param>
    /// <param name="Format">The component format, turned into a <c>VkFormat</c> at the seam.</param>
    /// <param name="Offset">Byte offset within its own slot.</param>
    internal readonly record struct VulkanVertexAttribute(
        uint Location, uint Binding, GpuVertexElementFormat Format, uint Offset);

    /// <summary>
    /// THE VERTEX INPUT STATE, COMPUTED FROM THE SEAM'S OWN LAYOUTS AND NOTHING ELSE. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>NO REFLECTION IS READ OFF THE MODULE, WHICH IS THE VULKAN HALF OF DECISION V-S1.</b> The Direct3D
    /// 11 backend has to reflect the compiled vertex signature, because SPIRV-Cross invents a
    /// <c>TEXCOORD&lt;location&gt;</c> semantic per location and drops any input the shader never reads, which is
    /// the holed-signature hazard that corrupted WARP. Here the caller's <see cref="GpuVertexLayoutDescription"/>
    /// list IS the input state, so there is one source of truth and nothing to reconcile.</para>
    ///
    /// <para><b>THE LOCATION COUNTS ACROSS ALL SLOTS, NOT WITHIN ONE.</b> Slot 1's first element continues where
    /// slot 0's last one left off, because a GLSL <c>location</c> is a single flat sequence over every vertex
    /// input the shader declares and knows nothing about which buffer an attribute arrives in. That is the same
    /// rule <c>D3D11InputLayoutPlan</c> applies to its semantic index, arrived at from the same shared GLSL, and
    /// getting it wrong reads the instance buffer's first attribute as the vertex buffer's second.</para>
    ///
    /// <para><b>OFFSETS ARE PACKED WITHIN THEIR OWN SLOT AND STRIDES ARE DECLARED-OR-COMPUTED.</b> The seam has no
    /// per-element offset, so an element sits immediately after the one before it in the same slot. A layout that
    /// declares a non-zero <see cref="GpuVertexLayoutDescription.Stride"/> keeps it, which is how an interleaved
    /// buffer with padding survives, and a zero stride is the sum of the slot's element sizes, which is what
    /// almost every shipped call site relies on.</para>
    ///
    /// <para><b>A STEP RATE ABOVE ONE IS REFUSED BY NAME.</b> Vulkan has no per-instance DIVISOR in core: the rate
    /// is a two-valued <c>VkVertexInputRate</c>, and a divisor needs
    /// <c>VK_EXT_vertex_attribute_divisor</c>, which this backend does not enable (V-N6). The shipped instance
    /// stream uses a rate of exactly 1, so nothing is lost, and refusing is the alternative to silently drawing
    /// every instance from the first element of the buffer.</para>
    /// </summary>
    internal static class VulkanVertexInput
    {
        /// <summary>The one per-instance rate this backend can express. See the type remarks.</summary>
        internal const uint SupportedInstanceStepRate = 1;

        /// <summary>Component size of a vertex element format, which is what packs a slot's elements.</summary>
        /// <exception cref="ArgumentOutOfRangeException">An unmapped format, which is a seam enum this backend
        /// has not been taught.</exception>
        internal static uint SizeInBytes(GpuVertexElementFormat format) => format switch
        {
            GpuVertexElementFormat.Float1 => 4,
            GpuVertexElementFormat.Float2 => 8,
            GpuVertexElementFormat.Float3 => 12,
            GpuVertexElementFormat.Float4 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format,
                "A GpuVertexElementFormat the native Vulkan backend has no size for."),
        };

        /// <summary>
        /// Flatten <paramref name="layouts"/> into the bindings and the attributes a graphics pipeline is created
        /// with. An empty or null list is the fullscreen case and yields neither, which is a legal and common
        /// vertex input state rather than an error.
        /// </summary>
        /// <param name="layouts">One layout per vertex buffer slot, in slot order.</param>
        /// <param name="attributes">The attributes, in location order.</param>
        /// <returns>The bindings, in slot order.</returns>
        /// <exception cref="ArgumentException">A layout declares an instance step rate this backend cannot
        /// express.</exception>
        internal static VulkanVertexBinding[] Build(IReadOnlyList<GpuVertexLayoutDescription>? layouts,
            out VulkanVertexAttribute[] attributes)
        {
            if (layouts is null || layouts.Count == 0)
            {
                attributes = [];
                return [];
            }

            int total = 0;
            for (int slot = 0; slot < layouts.Count; slot++) total += ElementsOf(layouts[slot]).Length;

            var bindings = new VulkanVertexBinding[layouts.Count];
            attributes = new VulkanVertexAttribute[total];

            uint location = 0;
            int next = 0;
            for (int slot = 0; slot < layouts.Count; slot++)
            {
                GpuVertexLayoutDescription layout = layouts[slot];
                RequireExpressibleRate(layout.InstanceStepRate, slot);

                GpuVertexElement[] declared = ElementsOf(layout);
                uint offset = 0;
                for (int i = 0; i < declared.Length; i++)
                {
                    attributes[next++] = new VulkanVertexAttribute(
                        location++, (uint)slot, declared[i].Format, offset);
                    offset += SizeInBytes(declared[i].Format);
                }

                bindings[slot] = new VulkanVertexBinding(
                    (uint)slot,
                    layout.Stride != 0 ? layout.Stride : offset,
                    layout.InstanceStepRate != 0);
            }

            return bindings;
        }

        static void RequireExpressibleRate(uint stepRate, int slot)
        {
            if (stepRate <= SupportedInstanceStepRate) return;

            throw new ArgumentException(
                "A native Vulkan graphics pipeline was given an instance step rate of "
                + stepRate.ToString(CultureInfo.InvariantCulture) + " at vertex buffer slot "
                + slot.ToString(CultureInfo.InvariantCulture)
                + ". Vulkan's core vertex input rate is two-valued (per vertex or per instance) with no divisor, "
                + "and the divisor extension is not enabled on this backend, so a rate above 1 cannot be honoured "
                + "and is refused rather than flattened to 1, which would draw every instance from the same "
                + "element. Every shipped instance stream declares a rate of 1.",
                nameof(stepRate));
        }

        static GpuVertexElement[] ElementsOf(in GpuVertexLayoutDescription layout) => layout.Elements ?? [];
    }
}
