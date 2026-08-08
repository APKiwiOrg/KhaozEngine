using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION V-W5: the swapchain's framebuffer identity never changes and everything underneath it does.
    /// <para>
    /// It matters more here than on the other native backend because EVERY IMAGE VIEW OBJECT IS REPLACED on a
    /// recreate, and on top of that the bound attachment moves to a different image on every acquire even when
    /// nothing was recreated. So this wrapper changes what it points at far more often than the Direct3D 11 one
    /// does, and the thing a consumer cached has to survive all of it.
    /// </para>
    /// </summary>
    public sealed class VulkanSwapchainFramebufferTests
    {
        static VulkanAttachment Attachment(ulong view) =>
            new(view, view + 1, GpuPixelFormat.B8G8R8A8UNorm, DepthStencil: false,
                VulkanRestingLayout.ColorAttachmentOptimal);

        /// <summary>The identity is taken once, at construction, and the generation counter is what actually moves
        /// so a diagnostic can still tell that the chain was rebuilt.</summary>
        [Fact]
        public void TheIdentityIsFixedAndTheGenerationMoves()
        {
            var framebuffer = new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, Attachment(0x10), new VulkanExtent(1280, 720));

            ulong id = framebuffer.Id;
            ulong generation = framebuffer.Generation;

            framebuffer.Adopt(Attachment(0x20), new VulkanExtent(800, 600));

            Assert.Equal(id, framebuffer.Id);
            Assert.NotEqual(generation, framebuffer.Generation);
            Assert.Equal(800u, framebuffer.Width);
            Assert.Equal(0x20UL, framebuffer.Attachment.View);
        }

        /// <summary>
        /// <c>Outputs</c> IS FIXED AT CONSTRUCTION, deliberately. A recreate changes the size and never the format
        /// or the sample count, so every pipeline built against the swapchain stays valid across every recreate. A
        /// framebuffer whose output description changed under a live pipeline would be a validation failure on the
        /// first draw after a resize.
        /// </summary>
        [Fact]
        public void TheOutputDescriptionNeverChanges()
        {
            var framebuffer = new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, Attachment(0x10), new VulkanExtent(1280, 720));

            GpuOutputDescription outputs = framebuffer.Outputs;
            framebuffer.Adopt(Attachment(0x20), new VulkanExtent(800, 600));

            Assert.Equal(outputs.Colour, framebuffer.Outputs.Colour);
            Assert.Equal(new[] { GpuPixelFormat.B8G8R8A8UNorm }, framebuffer.Outputs.Colour);
            // NO DEPTH AND NO MSAA on the swapchain, both matching the incumbent (V-W1). The engine's 3D path
            // renders into its own targets and resolves into this one.
            Assert.Null(framebuffer.Outputs.Depth);
            Assert.Equal(1, framebuffer.Outputs.SampleCount);
        }

        /// <summary>A zero view is refused rather than published. A swapchain framebuffer always has a colour
        /// target, so a zero view means a recreate published before it had made one, and binding it would
        /// rasterise into nothing with no error anywhere.</summary>
        [Fact]
        public void AZeroViewIsRefused()
        {
            Assert.Throws<ArgumentException>(() => new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, default, new VulkanExtent(1280, 720)));
        }

        /// <summary>The recorder binds it through the same interface it binds an ordinary framebuffer through, so
        /// the swapchain's own attachment moving on every acquire is invisible from the recording path.</summary>
        [Fact]
        public void ItBindsThroughTheSameInterfaceAnOrdinaryFramebufferDoes()
        {
            var framebuffer = new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, Attachment(0x10), new VulkanExtent(1280, 720));

            IVulkanBoundFramebufferSource source =
                VulkanBindableFramebuffer.Require(framebuffer, "a test bind");
            VulkanBoundFramebuffer bound = source.AsBound;

            Assert.Equal(framebuffer.Id, bound.Id);
            Assert.Equal(1, bound.ColourCount);
            Assert.False(bound.HasDepth);
            Assert.True(bound.IsBound);
        }

        /// <summary>Disposing what the device handed back releases nothing and the wrapper keeps working, which is
        /// what makes a consumer that disposes <c>IGpuDevice.SwapchainFramebuffer</c> harmless.</summary>
        [Fact]
        public void DisposingItReleasesNothing()
        {
            var framebuffer = new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, Attachment(0x10), new VulkanExtent(1280, 720));

            framebuffer.Dispose();
            framebuffer.Adopt(Attachment(0x20), new VulkanExtent(800, 600));

            Assert.True(framebuffer.IsDisposed);
            Assert.Equal(0x20UL, framebuffer.Attachment.View);
        }
    }
}
