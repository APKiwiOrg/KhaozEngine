using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuShaderSet"/> on the native Vulkan backend: two SHARED <c>VkShaderModule</c>s, one per stage,
    /// and nothing else. Work-breakdown row 16 (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>THE WHOLE PATH IS THREE LINES, WHICH IS DECISION V-S1 ARRIVING (12.1).</b> The GLSL goes through
    /// the engine's own front end to SPIR-V, the bytes go to <c>vkCreateShaderModule</c> verbatim, and the handles
    /// are held here for the pipeline row (https://github.com/APKiwiOrg/KhaozEngine/issues/523) to name. There is
    /// no cross-compilation, no reflection read back off the module, no register scheme and no signature check,
    /// because Vulkan's own binding model is what the shared GLSL already declares.</para>
    ///
    /// <para><b>NO REFLECTION IS TAKEN, AND THAT IS DELIBERATE.</b> The Direct3D 11 backend reads the
    /// cross-compiler's reflection because it has to map resources onto invented registers and has to check the
    /// emitted vertex input signature for the holed-<c>TEXCOORD</c> hazard. Here the pipeline's vertex input comes
    /// from the seam's own <c>GpuVertexLayoutDescription</c> and its resource bindings come from the layout array,
    /// both of which the caller passes, so a reflection read would be a second source of truth for facts the seam
    /// already carries. <c>VulkanShaderBindingTableTests</c> asserts the two agree, device-free, over every shipped
    /// program.</para>
    ///
    /// <para><b>THE HANDLES ARE SHARED AND THIS OBJECT DOES NOT OWN THEM (V-S7).</b> Eleven post-processing
    /// programs are built from one fullscreen vertex source, so eleven shader sets name one module.
    /// <see cref="Dispose"/> therefore destroys nothing, exactly as <see cref="VulkanResourceLayout"/>'s does:
    /// ending a handle here would leave every other set with the same stage naming a destroyed object.
    /// <see cref="VulkanShaderModuleCache.DestroyAll"/> ends them all in the device's teardown window.</para>
    ///
    /// <para><b>IT HOLDS NO CACHE AND NO SEAM.</b> The cache is a constructor parameter and not a field, so a
    /// shader set carries handles and no way to make another module. Same rule as the resource layout, and for the
    /// same forward-looking reason: a pipeline holds a shader set, and the recording type will hold pipelines.</para>
    /// </summary>
    internal sealed class VulkanShaderSet : IGpuShaderSet
    {
        /// <param name="modules">The device's ONE module cache, asked here and never held.</param>
        /// <param name="vertexGlsl">The vertex source, GLSL <c>#version 450</c>.</param>
        /// <param name="fragmentGlsl">The fragment source.</param>
        /// <param name="label">Optional name, included in a compile failure's message.</param>
        /// <exception cref="ShaderValidationException">A source failed to compile to SPIR-V. The message names the
        /// label and the stage.</exception>
        internal VulkanShaderSet(VulkanShaderModuleCache modules, string vertexGlsl, string fragmentGlsl,
            string? label = null)
        {
            ArgumentNullException.ThrowIfNull(modules);
            ArgumentNullException.ThrowIfNull(vertexGlsl);
            ArgumentNullException.ThrowIfNull(fragmentGlsl);

            string tag = label ?? "shader pair";

            // BOTH STAGES COMPILE BEFORE EITHER MODULE IS CREATED, so a fragment source that does not compile
            // leaves no orphaned vertex module in the cache under a program that was never built.
            byte[] vertexSpirv = SpirvFrontEnd.ToSpirv(vertexGlsl, GpuShaderStages.Vertex, tag);
            byte[] fragmentSpirv = SpirvFrontEnd.ToSpirv(fragmentGlsl, GpuShaderStages.Fragment, tag);

            VertexModule = modules.GetOrCreate(vertexSpirv);
            FragmentModule = modules.GetOrCreate(fragmentSpirv);
        }

        /// <summary>The vertex stage's SHARED <c>VkShaderModule</c>, which a graphics pipeline names.</summary>
        internal ulong VertexModule { get; }

        /// <summary>The fragment stage's SHARED <c>VkShaderModule</c>.</summary>
        internal ulong FragmentModule { get; }

        /// <summary>True once disposed. Nothing native is released either way, and the flag exists so a test can
        /// tell "disposed and destroyed nothing" from "never disposed".</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Release nothing. The modules are shared and the cache ends them at device teardown. Idempotent.
        /// </summary>
        public void Dispose() => IsDisposed = true;

        /// <summary>A shader set this backend compiled, refused by name for anything else. Row 13's pipeline
        /// creation is the caller: a set from another backend holds another backend's compiled modules, which is
        /// a refusal worth making by name rather than a cast that fails inside a create-info.</summary>
        internal static VulkanShaderSet Require(IGpuShaderSet? shaders, string what)
            => shaders as VulkanShaderSet
                ?? throw new ArgumentException(
                    $"The shader set handed to {what} was not compiled by the native Vulkan backend, so it holds "
                    + "no VkShaderModule. Compile shaders through the same IGpuDevice.Factory the pipeline is "
                    + "being created from.",
                    nameof(shaders));
    }
}
