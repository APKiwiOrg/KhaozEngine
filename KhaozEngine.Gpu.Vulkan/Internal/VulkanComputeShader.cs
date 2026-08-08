using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuComputeShader"/> on the native Vulkan backend: one SHARED <c>VkShaderModule</c> plus the
    /// workgroup size read straight out of the module. The single-stage sibling of <see cref="VulkanShaderSet"/>,
    /// work-breakdown row 16 (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>THE SIZE IS READ FROM THE SPIR-V RATHER THAN TAKEN FROM A CALLER</b>, through the same
    /// <see cref="SpirvLocalSize"/> parser both other backends use. That is not a shared-code preference: Vulkan
    /// takes the workgroup size from the module and ignores anything a description says, so a caller-supplied copy
    /// that disagreed with the shader would be invisible here and would produce wrong results on Metal, which is
    /// the one backend that reads it. The engine therefore never asks for it and reports what the module says.</para>
    ///
    /// <para><b>THE HANDLE IS SHARED AND THIS OBJECT DOES NOT OWN IT (V-S7)</b>, exactly as for a graphics shader
    /// set. The shipped ocean kernels are compiled per cascade resolution, so the four resolutions are four
    /// distinct sources and four distinct modules, but two producers asking for the same resolution share
    /// one.</para>
    /// </summary>
    internal sealed class VulkanComputeShader : IGpuComputeShader
    {
        /// <param name="modules">The device's ONE module cache, asked here and never held.</param>
        /// <param name="computeGlsl">The compute source, GLSL <c>#version 450</c>.</param>
        /// <param name="label">Optional name, included in a compile failure's message.</param>
        /// <exception cref="ShaderValidationException">The source failed to compile to SPIR-V, or the module
        /// declares no resolvable workgroup size.</exception>
        internal VulkanComputeShader(VulkanShaderModuleCache modules, string computeGlsl, string? label = null)
        {
            ArgumentNullException.ThrowIfNull(modules);
            ArgumentNullException.ThrowIfNull(computeGlsl);

            string tag = label ?? "compute shader";

            byte[] spirv = SpirvFrontEnd.ToSpirv(computeGlsl, GpuShaderStages.Compute, tag);
            (uint x, uint y, uint z) = SpirvLocalSize.Parse(spirv, tag);

            ThreadGroupSizeX = x;
            ThreadGroupSizeY = y;
            ThreadGroupSizeZ = z;

            // AFTER the size read, so a module the parser rejects never reaches the driver and never lands in the
            // cache under bytes nothing can dispatch.
            Module = modules.GetOrCreate(spirv);
        }

        /// <summary>The SHARED <c>VkShaderModule</c>, which a compute pipeline names.</summary>
        internal ulong Module { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeX { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeY { get; }

        /// <inheritdoc/>
        public uint ThreadGroupSizeZ { get; }

        /// <summary>True once disposed. Nothing native is released either way.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>Release nothing. The module is shared and the cache ends it at device teardown. Idempotent.
        /// </summary>
        public void Dispose() => IsDisposed = true;

        /// <summary>A compute shader this backend compiled, refused by name for anything else. The compute twin of
        /// <see cref="VulkanShaderSet.Require"/>, and row 13's compute pipeline is the caller.</summary>
        internal static VulkanComputeShader Require(IGpuComputeShader? shader, string what)
            => shader as VulkanComputeShader
                ?? throw new ArgumentException(
                    $"The compute shader handed to {what} was not compiled by the native Vulkan backend, so it "
                    + "holds no VkShaderModule. Compile shaders through the same IGpuDevice.Factory the pipeline "
                    + "is being created from.",
                    nameof(shader));
    }
}
