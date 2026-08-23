using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SIX NATIVE CALLS A RENDER PASS INSTANCE IS, behind an interface so the whole deferred-begin schedule
    /// above them is device-free: <c>vkCmdBeginRendering</c>, <c>vkCmdEndRendering</c>, <c>vkCmdSetViewport</c>,
    /// <c>vkCmdSetScissor</c> and the two shapes of <c>vkCmdClearAttachments</c>.
    ///
    /// <para><b>THIS IS NOT <see cref="IVkCmdSink"/> AND MUST NOT BECOME IT.</b> That seam exists to be COUNTED:
    /// it covers the three call classes that scale with draw count, decision V-T2 freezes a budget over it, and
    /// <c>VulkanBindBudgetTests.TheSeam_CannotSeeTheViewportHalfOfTheGate</c> pins that it names no viewport, no
    /// scissor and no begin. Nothing here scales with draw count: a begin happens once per pass, and a viewport
    /// and a scissor once per framebuffer CHANGE. Freezing marginals over them would gate on figures nobody should
    /// gate on, and widening the budget seam to reach them would quietly change what that budget means. So the
    /// rendering class gets its own line, and the two seams stay separate on purpose rather than by omission.</para>
    ///
    /// <para><b>IT IS A LINE AT ALL BECAUSE THE DESIGN OWES A DEVICE-FREE TEST.</b> Section 7.2 asserts the
    /// negative viewport height three ways, and one of them is a device-free test that the EMITTED viewport height
    /// is negative. Silk.NET's generated bindings are non-virtual, so an emission is observable only where there is
    /// a line to interpose on, and the alternative (assert the pure function and hope the call site passes it
    /// through) tests the arithmetic rather than the emission. The whole point of V-A5 is that the arithmetic being
    /// right and the call site being wrong look identical from a green suite.</para>
    ///
    /// <para><b>HANDLES ARE <c>ulong</c> AND THE ARGUMENTS ARE THIS BACKEND'S OWN VALUES</b>, the same split
    /// <see cref="IVulkanCommandApi"/> and <see cref="IVulkanResourceApi"/> take, so the schedule above names no
    /// Silk.NET type and a fake invents plain numbers. That is the OPPOSITE choice to <see cref="IVkCmdSink"/>,
    /// which names Silk.NET types deliberately because it exists to be a faithful picture of an argument list a
    /// budget is frozen over. Neither reason applies here: there is no budget, and the values that matter (the
    /// negative height, the load op, the clear value) are more legible as this backend's own records than as
    /// binding structs a test would have to build to read.</para>
    ///
    /// <para><b>THERE IS NO <c>VkRenderPass</c> AND NO <c>VkFramebuffer</c> BEHIND ANY OF IT (V-A1).</b> No cache
    /// for either, and therefore no invalidation on resize. <see cref="BeginRendering"/> takes the attachments
    /// themselves, because <c>IGpuFramebuffer.Outputs</c> is already <c>VkPipelineRenderingCreateInfo</c>'s input
    /// verbatim and a render-pass port would have had to write a render-pass cache, a framebuffer cache and the
    /// invalidation problem that comes with both.</para>
    /// </summary>
    internal interface IVulkanRenderApi
    {
        /// <summary>
        /// <c>vkCmdBeginRendering</c> over <paramref name="colour"/> and an optional depth attachment, with a
        /// render area of the full framebuffer and one layer.
        /// <para>
        /// EVERY ATTACHMENT'S STORE OP IS <c>STORE</c> (V-A6), which is why no store op is passed: it is not a
        /// choice this seam offers. The load ops travel on the attachments because a clear recorded before the
        /// first draw folds into one (V-A2).
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="width">Render-area width, the framebuffer's own.</param>
        /// <param name="height">Render-area height.</param>
        /// <param name="colour">The colour attachments in order, possibly empty (a depth-only shadow pass).</param>
        /// <param name="depth">The depth attachment, or null when the framebuffer declares none.</param>
        void BeginRendering(ulong commandBuffer, uint width, uint height,
            ReadOnlySpan<VulkanColourAttachment> colour, VulkanDepthAttachment? depth);

        /// <summary><c>vkCmdEndRendering</c>, closing the instance <see cref="BeginRendering"/> opened.</summary>
        void EndRendering(ulong commandBuffer);

        /// <summary>
        /// <c>vkCmdSetViewport</c> for viewport 0, which is the only one this backend ever sets. See
        /// <see cref="VulkanViewportRect"/> for the negative height and why it is the most consequential line in
        /// the design.
        /// </summary>
        void SetViewport(ulong commandBuffer, in VulkanViewportRect viewport);

        /// <summary><c>vkCmdSetScissor</c> for scissor 0.</summary>
        void SetScissor(ulong commandBuffer, in VulkanScissorRect scissor);

        /// <summary>
        /// <c>vkCmdClearAttachments</c> over one colour attachment, INSIDE an open render pass instance. What a
        /// clear that arrives after rendering has begun costs, which is what the incumbent did in the same
        /// situation and what the deferred begin exists to avoid for the common case.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="index">The colour attachment index, which is also its shader output location.</param>
        /// <param name="rgba">The clear colour.</param>
        /// <param name="width">The clear rect's width, the framebuffer's own.</param>
        /// <param name="height">The clear rect's height.</param>
        void ClearColourAttachment(ulong commandBuffer, uint index, Color rgba, uint width, uint height);

        /// <summary>
        /// <c>vkCmdClearAttachments</c> over the depth attachment, inside an open instance. The stencil plane goes
        /// with it at zero when <paramref name="stencil"/> is true, for the reason
        /// <see cref="VulkanDepthAttachment"/> gives.
        /// </summary>
        void ClearDepthAttachment(ulong commandBuffer, float depth, bool stencil, uint width, uint height);
    }
}
