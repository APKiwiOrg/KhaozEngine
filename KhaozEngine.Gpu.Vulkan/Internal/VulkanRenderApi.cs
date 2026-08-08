using System;
using KhaozEngine.Primitives;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE SIX REAL DRIVER CALLS BEHIND <see cref="IVulkanRenderApi"/>, and nothing else. No state, no guard, no
    /// cache and no decision of any kind: whether to begin, what a load op should be, whether a viewport is owed
    /// and whether a clear folds or is issued all live above this line in
    /// <see cref="VulkanRenderingSchedule"/>, which runs under a plain <c>[Fact]</c> on a machine with no Vulkan
    /// loader.
    /// <para>
    /// THE STRUCTURES ARE BUILT HERE AND NOWHERE ELSE, which is the same split
    /// <see cref="VulkanResourceApi"/> takes with <see cref="VulkanImageViewSpec"/>: the schedule decides in this
    /// backend's own values, and the translation into <c>RenderingInfo</c>, <c>RenderingAttachmentInfo</c>,
    /// <c>Viewport</c> and <c>Rect2D</c> happens at this line.
    /// </para>
    /// <para>
    /// NO RESULT TO CHECK ANYWHERE, for the reason <see cref="VulkanCmdSink"/> gives: every <c>vkCmd*</c> returns
    /// void, and a recording error is reported by <c>vkEndCommandBuffer</c> or by the validation layer rather than
    /// per call.
    /// </para>
    /// </summary>
    internal sealed unsafe class VulkanRenderApi : IVulkanRenderApi
    {
        readonly Vk _vk;

        /// <param name="vk">The instance's loaded API.</param>
        internal VulkanRenderApi(Vk vk)
        {
            ArgumentNullException.ThrowIfNull(vk);

            _vk = vk;
        }

        /// <inheritdoc/>
        public void BeginRendering(ulong commandBuffer, uint width, uint height,
            ReadOnlySpan<VulkanColourAttachment> colour, VulkanDepthAttachment? depth)
        {
            // STACK-ALLOCATED, because the count is the framebuffer's colour attachment count and every shipped
            // framebuffer has at most a handful. A heap array per begin would allocate once per pass on the frame
            // path for a structure the driver has finished reading by the time the call returns.
            Span<RenderingAttachmentInfo> attachments = stackalloc RenderingAttachmentInfo[colour.Length];
            for (int i = 0; i < colour.Length; i++)
            {
                attachments[i] = new RenderingAttachmentInfo(
                    sType: StructureType.RenderingAttachmentInfo,
                    imageView: new ImageView(colour[i].View),
                    imageLayout: ImageLayout.ColorAttachmentOptimal,
                    loadOp: Load(colour[i].LoadOp),
                    storeOp: AttachmentStoreOp.Store,
                    clearValue: new ClearValue(new ClearColorValue(
                        colour[i].ClearValue.R, colour[i].ClearValue.G, colour[i].ClearValue.B,
                        colour[i].ClearValue.A)));
            }

            RenderingAttachmentInfo depthInfo = default;
            RenderingAttachmentInfo stencilInfo = default;

            if (depth is { } attachment)
            {
                depthInfo = new RenderingAttachmentInfo(
                    sType: StructureType.RenderingAttachmentInfo,
                    imageView: new ImageView(attachment.View),
                    imageLayout: ImageLayout.DepthStencilAttachmentOptimal,
                    loadOp: Load(attachment.LoadOp),
                    storeOp: AttachmentStoreOp.Store,
                    clearValue: new ClearValue(depthStencil: new ClearDepthStencilValue(
                        attachment.ClearDepth, 0)));

                // THE STENCIL PLANE IS ITS OWN ATTACHMENT STRUCTURE over the SAME view, which is the one shape
                // that has no analogue in the render-pass model this backend does not have. See
                // VulkanDepthAttachment for why a combined format names both rather than leaving the stencil
                // plane holding whatever the last pass left in it.
                stencilInfo = depthInfo;
            }

            fixed (RenderingAttachmentInfo* pColour = attachments)
            {
                var info = new RenderingInfo(
                    sType: StructureType.RenderingInfo,
                    renderArea: new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
                    layerCount: 1,
                    colorAttachmentCount: (uint)colour.Length,
                    pColorAttachments: colour.Length == 0 ? null : pColour,
                    pDepthAttachment: depth is null ? null : &depthInfo,
                    pStencilAttachment: depth is { Stencil: true } ? &stencilInfo : null);

                _vk.CmdBeginRendering(new CommandBuffer((nint)commandBuffer), in info);
            }
        }

        /// <inheritdoc/>
        public void EndRendering(ulong commandBuffer)
            => _vk.CmdEndRendering(new CommandBuffer((nint)commandBuffer));

        /// <inheritdoc/>
        public void SetViewport(ulong commandBuffer, in VulkanViewportRect viewport)
        {
            var native = new Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height,
                viewport.MinDepth, viewport.MaxDepth);

            _vk.CmdSetViewport(new CommandBuffer((nint)commandBuffer), 0, 1, in native);
        }

        /// <inheritdoc/>
        public void SetScissor(ulong commandBuffer, in VulkanScissorRect scissor)
        {
            var native = new Rect2D(
                new Offset2D(scissor.X, scissor.Y), new Extent2D(scissor.Width, scissor.Height));

            _vk.CmdSetScissor(new CommandBuffer((nint)commandBuffer), 0, 1, in native);
        }

        /// <inheritdoc/>
        public void ClearColourAttachment(ulong commandBuffer, uint index, Color rgba, uint width, uint height)
        {
            var attachment = new ClearAttachment(
                aspectMask: ImageAspectFlags.ColorBit,
                colorAttachment: index,
                clearValue: new ClearValue(new ClearColorValue(rgba.R, rgba.G, rgba.B, rgba.A)));

            Clear(commandBuffer, in attachment, width, height);
        }

        /// <inheritdoc/>
        public void ClearDepthAttachment(ulong commandBuffer, float depth, bool stencil, uint width, uint height)
        {
            var attachment = new ClearAttachment(
                aspectMask: stencil
                    ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
                    : ImageAspectFlags.DepthBit,
                clearValue: new ClearValue(depthStencil: new ClearDepthStencilValue(depth, 0)));

            Clear(commandBuffer, in attachment, width, height);
        }

        void Clear(ulong commandBuffer, in ClearAttachment attachment, uint width, uint height)
        {
            var rect = new ClearRect(
                rect: new Rect2D(new Offset2D(0, 0), new Extent2D(width, height)),
                baseArrayLayer: 0,
                layerCount: 1);

            _vk.CmdClearAttachments(new CommandBuffer((nint)commandBuffer), 1, in attachment, 1, in rect);
        }

        static AttachmentLoadOp Load(VulkanLoadOp op)
            => op == VulkanLoadOp.Clear ? AttachmentLoadOp.Clear : AttachmentLoadOp.Load;
    }
}
