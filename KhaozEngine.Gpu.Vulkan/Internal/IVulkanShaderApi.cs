using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TWO REAL DRIVER CALLS THE SHADER PATH IS, behind an interface for the same reason
    /// <see cref="IVulkanResourceApi"/> and <see cref="IVulkanDescriptorApi"/> are ones: everything that can be
    /// WRONG about a shader (which compiler options produced the SPIR-V, whether two programs sharing a stage
    /// share a module, when a module is destroyed) is engine logic, and it runs under <c>dotnet test</c> on a
    /// machine with no Vulkan loader. Work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para><b>TWO CALLS, AND THAT IS THE WHOLE SHADER PATH ON THIS API (V-S1).</b> Vulkan consumes SPIR-V, so
    /// there is no cross-compilation, no HLSL, no FXC, no register scheme to invent and no signature workaround.
    /// The front end already in <c>KhaozEngine.Gpu</c> turns a GLSL 450 source into SPIR-V and
    /// <c>vkCreateShaderModule</c> takes the bytes verbatim. Phase 2's counterpart is seventy lines of hazard and
    /// this one is two members, which is not luck: the edge was confined to one file in <c>KhaozEngine.Gpu</c>
    /// precisely so a later backend could take the half it needs.</para>
    ///
    /// <para><b>HANDLES ARE <c>ulong</c></b>, so this interface and everything above it name no Silk.NET type.
    /// <c>VkShaderModule</c> is a non-dispatchable handle and is a 64-bit integer on the native side.</para>
    /// </summary>
    internal interface IVulkanShaderApi
    {
        /// <summary><c>vkCreateShaderModule</c> over the SPIR-V bytes VERBATIM. The bytes are 4-byte aligned by
        /// construction (a SPIR-V module is a word stream) and are not inspected, rewritten or patched anywhere on
        /// this path.</summary>
        /// <param name="spirv">The module's SPIR-V, as the front end emitted it.</param>
        /// <returns>The <c>VkShaderModule</c> handle. Never 0 on success.</returns>
        ulong CreateShaderModule(ReadOnlySpan<byte> spirv);

        /// <summary><c>vkDestroyShaderModule</c>. Terminal, and skipped on a dead device, like every other destroy
        /// in this package.</summary>
        void DestroyShaderModule(ulong module);
    }
}
