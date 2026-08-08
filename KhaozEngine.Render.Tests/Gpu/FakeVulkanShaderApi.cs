using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The two <c>vkCreateShaderModule</c> / <c>vkDestroyShaderModule</c> calls, faked, so the whole shader path
    /// above them runs on a machine with no Vulkan loader. Work-breakdown row 16
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/526).
    ///
    /// <para>THE BYTES ARE KEPT, not just counted, because the claim under test is that two programs sharing a
    /// stage produce ONE module: a counter alone cannot tell a hit from a miss that happened to return the same
    /// handle, and the recorded SPIR-V is what lets a test assert the dedup on content.</para>
    /// </summary>
    internal sealed class FakeVulkanShaderApi : IVulkanShaderApi
    {
        readonly List<byte[]> _created = new();
        readonly List<ulong> _destroyed = new();

        ulong _next = 0x5000;

        /// <summary>Every SPIR-V module handed to <c>vkCreateShaderModule</c>, in creation order.</summary>
        internal IReadOnlyList<byte[]> Created => _created;

        /// <summary>Every handle handed to <c>vkDestroyShaderModule</c>, in destroy order.</summary>
        internal IReadOnlyList<ulong> Destroyed => _destroyed;

        /// <inheritdoc/>
        public ulong CreateShaderModule(ReadOnlySpan<byte> spirv)
        {
            _created.Add(spirv.ToArray());
            return _next++;
        }

        /// <inheritdoc/>
        public void DestroyShaderModule(ulong module) => _destroyed.Add(module);
    }
}
