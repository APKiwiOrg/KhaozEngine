using System.Collections.Generic;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>ONE <c>vkCmdBindPipeline</c> AS THE DRIVER WOULD HAVE RECEIVED IT.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="Compute">True for the compute bind point.</param>
    /// <param name="Pipeline">The <c>VkPipeline</c> handle.</param>
    internal readonly record struct VulkanRecordedPipelineBind(ulong CommandBuffer, bool Compute, ulong Pipeline);

    /// <summary>
    /// AN <see cref="IVulkanPipelineBinder"/> WITH NO DEVICE BEHIND IT, so the identity guard, the bind-point
    /// split and the pipeline-layout adoption that goes with a switch are all observable under a plain
    /// <c>[Fact]</c>. Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para>IT CAN CREATE NOTHING, which is not an omission but the whole point of the seam it implements: a
    /// command list holds this, and a fake that could also create a pipeline would let the recording tests reach
    /// a shape the real recorder structurally cannot.</para>
    /// </summary>
    internal sealed class FakeVulkanPipelineBinder : IVulkanPipelineBinder
    {
        readonly List<VulkanRecordedPipelineBind> _binds = new();

        /// <summary>Every bind, in order.</summary>
        internal IReadOnlyList<VulkanRecordedPipelineBind> Binds => _binds;

        /// <inheritdoc/>
        public void BindPipeline(ulong commandBuffer, bool compute, ulong pipeline)
            => _binds.Add(new VulkanRecordedPipelineBind(commandBuffer, compute, pipeline));
    }
}
