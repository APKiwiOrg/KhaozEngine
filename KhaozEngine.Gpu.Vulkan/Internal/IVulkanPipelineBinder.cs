namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// ONE CALL, <c>vkCmdBindPipeline</c>, AND IT HAS ITS OWN LINE FOR THREE REASONS RATHER THAN BECAUSE A
    /// ONE-MEMBER INTERFACE IS TIDY. Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>IT IS NOT <see cref="IVkCmdSink"/> AND MUST NOT BECOME IT.</b> That seam exists to be COUNTED and
    /// covers exactly the three call classes that scale with DRAW COUNT: descriptor binds, draws and dispatches,
    /// and barriers. A pipeline bind scales with pipeline SWITCHES, which is a per-pass number, and decision V-T2
    /// freezes a budget over the other seam that widening it would quietly redefine. Its own note says so in as
    /// many words.</para>
    ///
    /// <para><b>IT IS NOT <see cref="IVulkanRenderApi"/> EITHER.</b> That one is the six calls a RENDER PASS
    /// INSTANCE is, and a compute pipeline is bound with no instance open at all: <c>SetComputePipeline</c> ends
    /// any pending rendering first (V-A4). A bind that is legal in both places does not belong on the seam whose
    /// whole subject is the place it is not.</para>
    ///
    /// <para><b>AND IT IS NOT <see cref="IVulkanPipelineApi"/>, WHICH IS THE LOAD-BEARING SPLIT.</b> That seam
    /// CREATES pipelines, and a recorder must not be able to reach a pipeline creation at all: it is a shader
    /// compile, and one inside a frame is the classic hitch. It is on the recording-unreachability list for
    /// exactly that reason, so the record-time call gets a separate interface with a separate implementation, and
    /// a list holding this one can bind a pipeline that already exists and make no new one.</para>
    ///
    /// <para><b>THE POSITIVE REASON THERE IS A LINE AT ALL is the same one
    /// <see cref="IVulkanRenderApi"/> gives.</b> Silk.NET's bindings are non-virtual, so an emission is observable
    /// only where there is something to interpose on, and what a device-free test needs to see here is that
    /// <c>SetPipeline</c> emits the bind AND adopts the pipeline's layout in the bind records, in that order, on
    /// the right bind point.</para>
    /// </summary>
    internal interface IVulkanPipelineBinder
    {
        /// <summary>
        /// <c>vkCmdBindPipeline</c> on <paramref name="commandBuffer"/>.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="compute">True for <c>VK_PIPELINE_BIND_POINT_COMPUTE</c>, false for graphics. A
        /// <c>bool</c> rather than a bind-point enum because this backend has exactly two bind points and no
        /// ray-tracing or subpass-shading pipeline anywhere in it, so a wider parameter would have unreachable
        /// values.</param>
        /// <param name="pipeline">The <c>VkPipeline</c> to bind, non-zero.</param>
        void BindPipeline(ulong commandBuffer, bool compute, ulong pipeline);
    }
}
