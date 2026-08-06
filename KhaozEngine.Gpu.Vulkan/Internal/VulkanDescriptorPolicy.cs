using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SEAM-TO-VULKAN DESCRIPTOR MAPPING, as pure functions (8.1, 8.3). Everything a layout decides before a
    /// driver is involved: which <c>VkDescriptorType</c> an element becomes, which image layout an image
    /// descriptor is read in, and how many DYNAMIC UNIFORM descriptors a pipeline's layouts spend between them.
    ///
    /// <para><b>THE MAPPING IS ALREADY ONE TO ONE, WHICH IS THE HEADLINE RATHER THAN A CONVENIENCE.</b> The GPU
    /// seam was designed against a Vulkan-shaped API: <c>IGpuResourceLayout</c> IS a
    /// <c>VkDescriptorSetLayout</c>, <c>IGpuResourceSet</c> IS a <c>VkDescriptorSet</c> written once at creation,
    /// and <see cref="GpuResourceLayoutElement.Dynamic"/> IS the dynamic uniform buffer. Nothing here is a
    /// translation layer working around an impedance mismatch, and the incumbent maps the same way, so this is a
    /// PORT. What is new is V-D4 below and the enforcement in
    /// <c>VulkanRecordingUnreachabilityTests</c>.</para>
    ///
    /// <para><b>SEPARATE <c>SAMPLED_IMAGE</c> AND <c>SAMPLER</c>, NEVER <c>COMBINED_IMAGE_SAMPLER</c></b>, which
    /// the engine's shared GLSL sources already assume by declaring <c>texture2D</c> and <c>sampler</c>
    /// separately. Structured read-only and read-write both map to <c>STORAGE_BUFFER</c>, because the seam's
    /// distinction between them is an ACCESS statement the shader makes and Vulkan carries the access on the
    /// shader's own declaration rather than on the descriptor.</para>
    ///
    /// <para><b>AND EVERY UNIFORM BUFFER BECOMES <c>UNIFORM_BUFFER_DYNAMIC</c> (V-D4), NOT ONLY THE ONES THE
    /// ENGINE DECLARED DYNAMIC.</b> Every <see cref="GpuBufferUsage.UniformBuffer"/> buffer on this backend is
    /// ring-backed (see <see cref="VulkanBufferRingPolicy"/>), the per-frame ring base has to be applied at BIND
    /// because the buffer identity must not change across 68 shipped <c>CreateResourceSet</c> call sites, and the
    /// only bind-time knob Vulkan offers on a uniform buffer is the dynamic offset. So the descriptor type is
    /// decided by the KIND alone and the declared flag decides exactly ONE thing: whether the caller's own
    /// per-draw offset is added on top of the frame base for that element. The seam's "at most one
    /// declared-dynamic element per set" rule is a statement about the ENGINE's dynamic-offset API, not about the
    /// Vulkan descriptor type, and it is unchanged and unaffected by any of this.</para>
    ///
    /// <para><b>THE COST OF THAT IS A DEVICE LIMIT, AND IT HAS FOUR DEFENCES</b>, of which
    /// <see cref="DynamicUniformCount(in GpuResourceLayoutDescription)"/> feeds the two that fire earliest. See
    /// <see cref="VulkanDescriptorLimits"/>.</para>
    /// </summary>
    internal static class VulkanDescriptorPolicy
    {
        /// <summary>
        /// The <c>VkDescriptorType</c> one declared element becomes.
        /// </summary>
        /// <param name="element">The seam's element.</param>
        /// <exception cref="ArgumentException"><see cref="GpuResourceLayoutElement.Dynamic"/> is set on anything
        /// that is not a <see cref="GpuResourceKind.UniformBuffer"/>. See the method's own remarks.</exception>
        /// <remarks>
        /// <b>A DECLARED-DYNAMIC ELEMENT THAT IS NOT A UNIFORM BUFFER IS REFUSED, and the refusal is wider than
        /// the Direct3D 11 backend's by one case.</b> <c>D3D11ResourceLayout</c> refuses a dynamic STRUCTURED
        /// buffer because neither of that API's shader-resource binds carries a per-bind window, so the offset
        /// would be silently dropped. Here a dynamic structured buffer would additionally have to become
        /// <c>STORAGE_BUFFER_DYNAMIC</c>, which 8.1's mapping does not produce, and a dynamic TEXTURE or SAMPLER
        /// has no dynamic form at all. Both would leave row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) composing a POSITIONAL <c>pDynamicOffsets</c>
        /// array whose entries no longer line up with the set's dynamic descriptors, which reads the wrong slice
        /// of the right buffer and renders plausible garbage rather than throwing. Vacuous in the engine today:
        /// all six shipped dynamic elements are uniform buffers.
        /// </remarks>
        internal static VulkanDescriptorType TypeFor(in GpuResourceLayoutElement element)
        {
            if (element.Dynamic && element.Kind != GpuResourceKind.UniformBuffer)
            {
                throw new ArgumentException(
                    $"'{element.Name}' is declared as {element.Kind} AND dynamic, which the native Vulkan backend "
                    + "cannot honour. The engine's dynamic offset is a per-draw byte rebase, and the only Vulkan "
                    + "descriptor this backend gives a bind-time offset to is the dynamic UNIFORM buffer "
                    + "(decision V-D4): a structured buffer maps to STORAGE_BUFFER and an image or a sampler has "
                    + "no dynamic form at all. Accepting it would leave the positional pDynamicOffsets array "
                    + "misaligned against the set's real dynamic descriptors, which reads the wrong slice of the "
                    + "right buffer and renders plausible garbage rather than throwing. The Direct3D 11 backend "
                    + "refuses the structured half of this for its own reason, so an element like this is already "
                    + "unshippable on two of the three backends. Declare it as a uniform buffer, or build one "
                    + "resource set per window.",
                    nameof(element));
            }

            return element.Kind switch
            {
                // V-D4. The DECLARED flag is deliberately not read here: it decides the bind-time offset
                // composition and nothing about the descriptor's type.
                GpuResourceKind.UniformBuffer => VulkanDescriptorType.UniformBufferDynamic,
                GpuResourceKind.StructuredBufferReadOnly => VulkanDescriptorType.StorageBuffer,
                GpuResourceKind.StructuredBufferReadWrite => VulkanDescriptorType.StorageBuffer,
                GpuResourceKind.TextureReadOnly => VulkanDescriptorType.SampledImage,
                GpuResourceKind.TextureReadWrite => VulkanDescriptorType.StorageImage,
                GpuResourceKind.Sampler => VulkanDescriptorType.Sampler,
                _ => throw new ArgumentOutOfRangeException(nameof(element), element.Kind,
                    "A resource kind the native Vulkan descriptor mapping does not know. Every member of "
                    + "GpuResourceKind has a mapping in section 8.1, so this is a new member that was added "
                    + "without one."),
            };
        }

        /// <summary>
        /// The whole binding table for one layout, in declaration order. BINDING INDEX EQUALS ELEMENT INDEX and
        /// <c>descriptorCount</c> is always 1 (8.1), which is what lets a resource set's resources be matched to
        /// its layout's elements positionally with no lookup at all.
        /// </summary>
        internal static VulkanDescriptorBinding[] BindingsFor(in GpuResourceLayoutDescription description)
        {
            GpuResourceLayoutElement[] elements = description.Elements ?? [];

            var bindings = new VulkanDescriptorBinding[elements.Length];
            for (int i = 0; i < elements.Length; i++)
            {
                bindings[i] = new VulkanDescriptorBinding(
                    (uint)i, TypeFor(elements[i]), DescriptorCount: 1, elements[i].Stages);
            }

            return bindings;
        }

        /// <summary>The image layout an image descriptor is written with: sampled images bind
        /// <c>SHADER_READ_ONLY_OPTIMAL</c> and storage images bind <c>GENERAL</c> (8.1). Anything else is not an
        /// image descriptor and carries no layout at all.</summary>
        internal static VulkanDescriptorImageLayout ImageLayoutFor(VulkanDescriptorType type) => type switch
        {
            VulkanDescriptorType.SampledImage => VulkanDescriptorImageLayout.ShaderReadOnlyOptimal,
            VulkanDescriptorType.StorageImage => VulkanDescriptorImageLayout.General,
            _ => VulkanDescriptorImageLayout.None,
        };

        /// <summary>Whether a descriptor type names a buffer, which decides which payload of a
        /// <see cref="VulkanDescriptorWrite"/> is read.</summary>
        internal static bool IsBuffer(VulkanDescriptorType type)
            => type is VulkanDescriptorType.UniformBuffer or VulkanDescriptorType.UniformBufferDynamic
                or VulkanDescriptorType.StorageBuffer or VulkanDescriptorType.StorageBufferDynamic;

        /// <summary>Whether a descriptor type takes a bind-time entry in <c>pDynamicOffsets</c>. Row 11's
        /// positional array is built over exactly these, in binding order.</summary>
        internal static bool IsDynamic(VulkanDescriptorType type)
            => type is VulkanDescriptorType.UniformBufferDynamic or VulkanDescriptorType.StorageBufferDynamic;

        /// <summary>
        /// HOW MANY DYNAMIC UNIFORM DESCRIPTORS ONE LAYOUT SPENDS, which under V-D4 is simply how many
        /// <see cref="GpuResourceKind.UniformBuffer"/> elements it declares, whether or not any of them carries
        /// the engine's own dynamic flag. This is the number
        /// <c>maxDescriptorSetUniformBuffersDynamic</c> is measured against, summed across a pipeline's whole
        /// layout array.
        /// </summary>
        internal static int DynamicUniformCount(in GpuResourceLayoutDescription description)
            => DynamicUniformCount(BindingsFor(description));

        /// <summary>The same count over an already-computed binding table.</summary>
        internal static int DynamicUniformCount(ReadOnlySpan<VulkanDescriptorBinding> bindings)
        {
            int count = 0;
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].Type == VulkanDescriptorType.UniformBufferDynamic)
                    count += (int)bindings[i].DescriptorCount;
            }

            return count;
        }
    }
}
