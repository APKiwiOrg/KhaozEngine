using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE VULKAN SWAPCHAIN CAPTURE, device-free. A swapchain image is a copy source only when it was created with
    /// <c>TRANSFER_SRC</c>, so a capture request is a swapchain recreation, and only when the surface's
    /// <c>supportedUsageFlags</c> allow it. The policy, the boundary's recreate and its copyability reading, and
    /// the command list's copy of the bound swapchain attachment are asserted here over the present and resource
    /// fakes. The real recreation and the real layout transitions need a Vulkan leg with a window.
    /// </summary>
    public sealed class VulkanSwapchainCaptureTests
    {
        const string NoTransferSrc =
            "The surface does not support TRANSFER_SRC on swapchain images, so frame capture is unavailable.";

        sealed class Rig : IDisposable
        {
            internal Rig(VulkanSurfaceReport? report = null)
            {
                if (report is { } given) Surfaces.Report = given;

                Boundary = new VulkanPresentBoundary(
                    Surfaces, Swapchains, FakeVulkanSurfaceApi.Handle, new VulkanExtent(1280, 720),
                    syncToVerticalBlank: true, VulkanAcquireMode.Semaphore, framesInFlight: 3, new object(),
                    () => { }, Orphan, new WaitAccumulator());
            }

            internal FakeVulkanSurfaceApi Surfaces { get; } = new();
            internal FakeVulkanSwapchainApi Swapchains { get; } = new();
            internal FakeVulkanOrphanTarget Orphan { get; } = new();
            internal VulkanPresentBoundary Boundary { get; }

            internal void Frame()
            {
                Boundary.TakeFrameSemaphores(boundSwapchainFramebuffer: true);
                Boundary.Present();
            }

            public void Dispose() => Boundary.Dispose();
        }

        // ---- the policy ----

        [Fact]
        public void Without_a_request_the_usage_is_unchanged()
        {
            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                FakeVulkanSurfaceApi.Desktop(), new VulkanExtent(1280, 720), syncToVerticalBlank: true,
                srgb: false, copySource: false, out string? warning);

            Assert.Equal(VulkanSwapchainSpec.ColourAndTransferDst, spec.Usage);
            Assert.Null(warning);
        }

        [Fact]
        public void A_request_adds_TRANSFER_SRC_when_the_surface_supports_it()
        {
            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                FakeVulkanSurfaceApi.Desktop(), new VulkanExtent(1280, 720), syncToVerticalBlank: true,
                srgb: false, copySource: true, out string? warning);

            Assert.Equal(VulkanSwapchainSpec.ColourAndTransferDst | ImageUsageFlags.TransferSrcBit, spec.Usage);
            Assert.Equal(VulkanSwapchainSpec.ColourTransferDstAndSrc, spec.Usage);
            Assert.Null(warning);
        }

        [Fact]
        public void A_request_the_surface_cannot_honour_is_dropped_with_a_warning()
        {
            VulkanSurfaceReport surface = FakeVulkanSurfaceApi.Desktop() with
            {
                SupportedUsage = VulkanSwapchainSpec.ColourAndTransferDst,
            };

            VulkanSwapchainSpec spec = VulkanSwapchainPolicy.Decide(
                surface, new VulkanExtent(1280, 720), syncToVerticalBlank: true, srgb: false, copySource: true,
                out string? warning);

            Assert.Equal(VulkanSwapchainSpec.ColourAndTransferDst, spec.Usage);
            Assert.Equal(NoTransferSrc, warning);
            Assert.Contains("TRANSFER_SRC", warning, StringComparison.Ordinal);
        }

        // ---- the boundary ----

        [Fact]
        public void Changing_the_request_queues_one_recreate()
        {
            using var rig = new Rig();
            Assert.Single(rig.Swapchains.Created);
            Assert.False(rig.Boundary.HasPendingRecreate);

            rig.Boundary.CopySourceRequested = true;
            Assert.True(rig.Boundary.HasPendingRecreate);

            rig.Boundary.Present();

            Assert.Equal(2, rig.Swapchains.Created.Count);
            Assert.Equal(ImageUsageFlags.None, rig.Swapchains.Created[0].Usage & ImageUsageFlags.TransferSrcBit);
            Assert.Equal(ImageUsageFlags.TransferSrcBit,
                rig.Swapchains.Created[1].Usage & ImageUsageFlags.TransferSrcBit);
            Assert.False(rig.Boundary.HasPendingRecreate);
        }

        [Fact]
        public void Setting_the_same_request_twice_queues_nothing()
        {
            using var rig = new Rig();

            // The default is no request, so asking for none changes nothing.
            rig.Boundary.CopySourceRequested = false;
            Assert.False(rig.Boundary.HasPendingRecreate);

            rig.Boundary.CopySourceRequested = true;
            rig.Frame();
            Assert.Equal(2, rig.Swapchains.Created.Count);

            rig.Boundary.CopySourceRequested = true;
            Assert.False(rig.Boundary.HasPendingRecreate);

            rig.Frame();
            Assert.Equal(2, rig.Swapchains.Created.Count);
        }

        [Fact]
        public void The_current_generation_is_copyable_only_when_its_usage_has_TRANSFER_SRC()
        {
            using var rig = new Rig();
            Assert.False(rig.Boundary.CurrentGenerationCopyable);

            // REQUESTED BUT NOT YET APPLIED: the frame still renders into an image of the old usage.
            rig.Boundary.CopySourceRequested = true;
            Assert.False(rig.Boundary.CurrentGenerationCopyable);

            rig.Frame();
            Assert.True(rig.Boundary.CurrentGenerationCopyable);

            rig.Boundary.CopySourceRequested = false;
            Assert.True(rig.Boundary.CurrentGenerationCopyable);

            rig.Frame();
            Assert.False(rig.Boundary.CurrentGenerationCopyable);
            Assert.Equal(3, rig.Swapchains.Created.Count);
        }

        [Fact]
        public void A_surface_without_TRANSFER_SRC_never_makes_a_copyable_generation()
        {
            using var rig = new Rig(FakeVulkanSurfaceApi.Desktop() with
            {
                SupportedUsage = VulkanSwapchainSpec.ColourAndTransferDst,
            });

            rig.Boundary.CopySourceRequested = true;
            rig.Frame();

            Assert.Equal(2, rig.Swapchains.Created.Count);
            Assert.Equal(VulkanSwapchainSpec.ColourAndTransferDst, rig.Swapchains.Created[1].Usage);
            Assert.False(rig.Boundary.CurrentGenerationCopyable);
        }

        [Fact]
        public void The_orphan_target_is_never_copyable()
        {
            using var rig = new Rig();
            rig.Boundary.CopySourceRequested = true;
            rig.Frame();
            Assert.True(rig.Boundary.CurrentGenerationCopyable);

            rig.Surfaces.Report = FakeVulkanSurfaceApi.Minimised();
            rig.Boundary.QueueResize(0, 0);
            rig.Frame();

            Assert.True(rig.Boundary.IsOrphanBound);
            Assert.False(rig.Boundary.CurrentGenerationCopyable);
        }

        // ---- the device ----

        [Fact]
        public void A_headless_device_stores_the_request_and_is_never_copyable()
        {
            using var fixture = new VulkanDeviceFixture();
            IGpuSwapchainCapture capture = fixture.Device;

            capture.SwapchainCopySourceRequested = true;

            Assert.True(capture.SwapchainCopySourceRequested);
            Assert.False(capture.SwapchainCopyable);

            using IGpuTexture destination = fixture.StagingTexture();
            using VulkanCommandList list = fixture.RecordedList();
            Assert.Throws<InvalidOperationException>(() => capture.CopySwapchainColor(list, destination));
        }

        // ---- the copy ----

        /// <summary>
        /// A CLEAR WITH NO DRAW, THEN THE COPY, IN ONE RECORDING. The clear is a pending load action until the
        /// pass ends, and the copy ends the pass through the same helper every transfer uses, which runs the
        /// clear-only begin and end first. The swapchain image then leaves <c>COLOR_ATTACHMENT_OPTIMAL</c> for
        /// <c>TRANSFER_SRC_OPTIMAL</c>, which keeps the cleared contents rather than discarding them, and
        /// <c>End</c> puts it back to <c>PRESENT_SRC_KHR</c>.
        /// </summary>
        [Fact]
        public void A_clear_only_pass_is_applied_before_the_swapchain_copy_and_the_image_returns_to_present()
        {
            const ulong Image = 0x5C1;
            var fixture = new VulkanResourceFixture();
            var attachment = new VulkanAttachment(0x5C0, Image, GpuPixelFormat.B8G8R8A8UNorm, DepthStencil: false,
                VulkanRestingLayout.PresentSrcKhr);
            var swapchain = new VulkanSwapchainFramebuffer(
                GpuPixelFormat.B8G8R8A8UNorm, attachment, new VulkanExtent(8, 4));

            var destination = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 4, GpuTextureUsage.Sampled, GpuPixelFormat.B8G8R8A8UNorm));

            try
            {
                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                fixture.Trace.Clear();

                list.SetFramebuffer(swapchain);
                list.ClearColorTarget(0, Color.White);
                list.CopyFromSwapchain(swapchain.Attachment, destination);

                List<string> trace = fixture.Trace;
                int begin = trace.FindIndex(t => t.StartsWith("BeginRendering", StringComparison.Ordinal));
                int end = trace.IndexOf("EndRendering");
                int copy = trace.FindIndex(t => t.StartsWith("CopyImage(", StringComparison.Ordinal));

                Assert.True(begin >= 0 && begin < end && end < copy, string.Join(" | ", trace));
                Assert.Equal(VulkanLoadOp.Clear, Assert.Single(fixture.RenderApi.Begins).Colour[0].LoadOp);

                VulkanRecordedImageCopy recorded = Assert.Single(fixture.TransferSink.ImageCopies);
                Assert.Equal(Image, recorded.Source);
                Assert.Equal(destination.Image, recorded.Destination);
                Assert.Equal(8u, recorded.Region.Extent.Width);
                Assert.Equal(4u, recorded.Region.Extent.Height);

                ImageMemoryBarrier2 toSource = fixture.Barriers.Barriers.Single(
                    b => b.Image.Handle == Image && b.NewLayout == ImageLayout.TransferSrcOptimal);
                Assert.Equal(ImageLayout.ColorAttachmentOptimal, toSource.OldLayout);

                list.End();

                ImageMemoryBarrier2 restored = fixture.Barriers.Barriers.Last(b => b.Image.Handle == Image);
                Assert.Equal(ImageLayout.TransferSrcOptimal, restored.OldLayout);
                Assert.Equal(ImageLayout.PresentSrcKhr, restored.NewLayout);
            }
            finally
            {
                destination.Dispose();
            }
        }
    }
}
