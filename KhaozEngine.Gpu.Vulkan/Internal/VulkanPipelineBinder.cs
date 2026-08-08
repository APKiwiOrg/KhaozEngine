using System;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE ONE REAL DRIVER CALL BEHIND <see cref="IVulkanPipelineBinder"/>, and nothing else. Whether a bind is
    /// owed at all, which bind records the switch invalidates and how much of them survives are decided above this
    /// line, in <see cref="VulkanCommandList"/> and <see cref="VulkanBindRecords"/>.
    ///
    /// <para><b>IT HOLDS NO WAY TO MAKE A PIPELINE.</b> That is the whole reason it is a separate type from
    /// <see cref="VulkanPipelineApi"/> rather than a seventh member on it: a command list holds this, so anything
    /// this type can reach is something a recorder can reach, and a pipeline creation on a record path is a shader
    /// compile inside a frame. See <see cref="IVulkanPipelineBinder"/> for the full argument and
    /// <c>VulkanRecordingUnreachabilityTests</c> for the enforcement.</para>
    ///
    /// <para><b>NO RESULT TO CHECK</b>, for the reason <see cref="VulkanRenderApi"/> gives: every <c>vkCmd*</c>
    /// returns void, and a recording error is reported by <c>vkEndCommandBuffer</c> or by the validation layer
    /// rather than per call.</para>
    /// </summary>
    internal sealed class VulkanPipelineBinder : IVulkanPipelineBinder
    {
        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanPipelineBinder(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
        }

        /// <inheritdoc/>
        public void BindPipeline(ulong commandBuffer, bool compute, ulong pipeline)
            => _vk.CmdBindPipeline(
                new CommandBuffer((nint)commandBuffer),
                compute ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics,
                new Pipeline(pipeline));
    }
}
