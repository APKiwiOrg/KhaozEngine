using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE FRAMEBUFFER THAT CREATES NOTHING (V-A1), AND THE FIVE SEAM MEMBERS THAT STOP REFUSING. Work-breakdown
    /// row 12 (https://github.com/APKiwiOrg/KhaozEngine/issues/522).
    ///
    /// <para><b>THE LARGEST STRUCTURAL DECISION IN THE DESIGN IS VISIBLE HERE AS AN ABSENCE.</b> There is no
    /// <c>VkRenderPass</c>, no <c>VkFramebuffer</c>, no cache for either and no invalidation on resize, so
    /// <c>CreateFramebuffer</c> makes no driver object at all and its disposal releases nothing. The render-pass
    /// port would have had to write a render-pass cache, a framebuffer cache and the invalidation problem that
    /// comes with both, and section 2.3 argues that at length. What is asserted below is the observable half: the
    /// resource seam is not touched.</para>
    ///
    /// <para><b>AND THE SEAM MEMBERS ARE DRIVEN THROUGH A REAL LIST</b>, on the same rig row 11 binds through, so
    /// the wiring between <c>IGpuCommandList</c> and <see cref="VulkanRenderingSchedule"/> is covered rather than
    /// assumed. The schedule's own rules are <see cref="VulkanRenderingScheduleTests"/>.</para>
    /// </summary>
    public sealed class VulkanFramebufferTests
    {
        // ---- Creation ----

        /// <summary>
        /// CREATING A FRAMEBUFFER TOUCHES THE RESOURCE SEAM NOT AT ALL: no image, no view, no render pass and no
        /// framebuffer object. Every attachment view already exists, made at TEXTURE creation from the declared
        /// usage bits (V-M11), and a framebuffer is an aggregate of borrowed handles.
        /// </summary>
        [Fact]
        public void CreatingAFramebuffer_MakesNoNativeObjectAtAll()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture colour = Colour(fixture, owned);
                IGpuTexture depth = Depth(fixture, owned);

                int viewsBefore = fixture.Views.Count;
                int callsBefore = fixture.ResourceApi.Events.Count;

                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(depth, colour);
                owned.Add(framebuffer);

                Assert.Equal(viewsBefore, fixture.Views.Count);
                Assert.Equal(callsBefore, fixture.ResourceApi.Events.Count);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// ITS <c>Outputs</c> CARRY THE ATTACHMENT FORMATS AND THE SAMPLE COUNT, which is what row 13 builds a
        /// pipeline's state from: the formats become <c>VkPipelineRenderingCreateInfo</c> verbatim and the count
        /// becomes <c>VkPipelineMultisampleStateCreateInfo.rasterizationSamples</c>. A pipeline's sample count
        /// must match the framebuffer it draws into, so a wrong count here is a pipeline the driver refuses to
        /// use with this target.
        /// </summary>
        [Fact]
        public void ItsOutputs_CarryTheAttachmentFormatsAndTheSampleCount()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture colour = Colour(fixture, owned);
                IGpuTexture depth = Depth(fixture, owned);

                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(depth, colour);
                owned.Add(framebuffer);

                Assert.Equal(GpuPixelFormat.D32FloatS8UInt, framebuffer.Outputs.Depth);
                Assert.Equal([GpuPixelFormat.R8G8B8A8UNorm], framebuffer.Outputs.Colour);
                Assert.Equal(1, framebuffer.Outputs.SampleCount);
                Assert.Equal(64u, framebuffer.Width);
                Assert.Equal(64u, framebuffer.Height);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// A DEPTH-ONLY FRAMEBUFFER IS LEGAL AND TAKES ITS EXTENT FROM THE DEPTH ATTACHMENT, which is the shadow
        /// pass's exact shape and the one case where the colour array is empty. A begin over it names zero colour
        /// attachments, which is legal in dynamic rendering and is why the render area cannot be derived from
        /// colour attachment 0.
        /// </summary>
        [Fact]
        public void ADepthOnlyFramebuffer_IsLegalAndTakesItsExtentFromTheDepthAttachment()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture depth = Depth(fixture, owned, size: 2048);

                var framebuffer = (VulkanFramebuffer)fixture.Factory.CreateFramebuffer(depth);
                owned.Add(framebuffer);

                Assert.Equal(2048u, framebuffer.Width);
                Assert.Equal(2048u, framebuffer.Height);
                Assert.Empty(framebuffer.Outputs.Colour);
                Assert.Equal(0, framebuffer.AsBound.ColourCount);
                Assert.True(framebuffer.AsBound.HasDepth);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>Two framebuffers have different identities, which is what the framebuffer-change guard
        /// compares. Plain data has no reference to compare, so the identity is a process-unique number taken at
        /// construction.</summary>
        [Fact]
        public void TwoFramebuffers_HaveDistinctNonZeroIdentities()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var first = (VulkanFramebuffer)fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                var second = (VulkanFramebuffer)fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                owned.Add(first);
                owned.Add(second);

                Assert.NotEqual(0ul, first.Id);
                Assert.NotEqual(first.Id, second.Id);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>A framebuffer with no attachments at all renders nowhere, and a begin over it has no render
        /// area to derive.</summary>
        [Fact]
        public void AFramebufferWithNoAttachments_IsRefused()
        {
            var fixture = new VulkanResourceFixture();

            Assert.Throws<ArgumentException>(() => fixture.Factory.CreateFramebuffer(null));
        }

        /// <summary>Attachments of different sizes are refused, because the render area and the pipeline's sample
        /// count are both single values.</summary>
        [Fact]
        public void AttachmentsOfDifferentSizes_AreRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture colour = Colour(fixture, owned);
                IGpuTexture depth = Depth(fixture, owned, size: 128);

                Assert.Throws<ArgumentException>(() => fixture.Factory.CreateFramebuffer(depth, colour));
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// A TEXTURE THAT NEVER DECLARED THE USAGE HAS NO VIEW TO BIND, and no framebuffer can conjure one:
        /// views follow from the declared usage at creation and there is no view factory reachable from here
        /// (V-M11). The refusal names the usage the caller should have asked for.
        /// </summary>
        [Fact]
        public void AColourAttachmentWithoutRenderTargetUsage_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture sampled = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.Sampled));
                owned.Add(sampled);

                Assert.Throws<ArgumentException>(() => fixture.Factory.CreateFramebuffer(null, sampled));
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>Disposing a framebuffer releases nothing, because nothing native was made. The textures
        /// outlive it and are disposed by whoever created them.</summary>
        [Fact]
        public void DisposingAFramebuffer_ReleasesNothing()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var framebuffer = (VulkanFramebuffer)fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));

                int callsBefore = fixture.ResourceApi.Events.Count;
                framebuffer.Dispose();
                framebuffer.Dispose();

                Assert.True(framebuffer.IsDisposed);
                Assert.Equal(callsBefore, fixture.ResourceApi.Events.Count);
                Assert.Equal(0, fixture.Drain());
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        // ---- The seam members, through a real list ----

        /// <summary>
        /// THE FIVE SEAM MEMBERS STOP REFUSING AND GO THROUGH THE DEFERRED BEGIN. A bind plus a clear plus an
        /// <c>End</c> is the clear-only pass, so the whole recording is one begin-and-end pair carrying
        /// <c>loadOp = CLEAR</c> and no <c>vkCmdClearAttachments</c> at all.
        /// </summary>
        [Fact]
        public void TheSeamMembers_DriveTheDeferredBeginThroughARealList()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(
                    Depth(fixture, owned), Colour(fixture, owned));
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.ClearColorTarget(0, Color.Black);
                list.ClearDepthStencil(1f);
                list.SetScissorRect(0, 0, 0, 32, 32);
                list.SetFullScissorRects();
                list.End();

                VulkanRecordedBegin begin = Assert.Single(fixture.RenderApi.Begins);
                Assert.Equal(VulkanLoadOp.Clear, begin.Colour[0].LoadOp);
                Assert.Equal(VulkanLoadOp.Clear, Assert.NotNull(begin.Depth).LoadOp);
                Assert.Equal(1, fixture.RenderApi.EndCount);
                Assert.Empty(fixture.RenderApi.Clears);

                // NO DRAW CAME, so the viewport and the scissor were never owed: the pair is emitted by a draw,
                // and a clear-only pass has none.
                Assert.Empty(fixture.RenderApi.Viewports);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// A <c>Begin</c> RESETS THE BOUND FRAMEBUFFER, so the next recording's first bind is a change. A fresh
        /// <c>VkCommandBuffer</c> holds no framebuffer, and a retained one would let that bind take the redundant
        /// path and draw into a target this buffer never bound.
        /// </summary>
        [Fact]
        public void ASecondRecording_RebindsTheSameFramebufferAsAChange()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();

                for (int recording = 0; recording < 2; recording++)
                {
                    list.Begin();
                    list.SetFramebuffer(framebuffer);
                    list.PrepareDraw();
                    list.End();
                }

                Assert.Equal(2, fixture.RenderApi.Viewports.Count);
                Assert.Equal(2, fixture.RenderApi.Begins.Count);
                Assert.Equal(2, fixture.RenderApi.EndCount);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// A RENDERING MEMBER OUTSIDE A RECORDING IS REFUSED, and the asymmetry with a resource-set bind (which is
        /// DISCARDED) is deliberate: a bind touches only this list's own array, while a rendering member can emit
        /// a <c>vkCmd*</c> immediately, and a command recorded into a buffer that was never begun is undefined
        /// behaviour rather than a no-op.
        /// </summary>
        [Fact]
        public void ARenderingMemberOutsideARecording_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();

                Assert.Throws<InvalidOperationException>(() => list.SetFramebuffer(framebuffer));
                Assert.Throws<InvalidOperationException>(() => list.ClearColorTarget(0, Color.White));
                Assert.Throws<InvalidOperationException>(() => list.SetScissorRect(0, 0, 0, 8, 8));
                Assert.Throws<InvalidOperationException>(list.SetFullScissorRects);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>A framebuffer from another backend carries no <c>VkImageView</c> to render into, and it is
        /// refused BEFORE the identity guard so the mistake cannot pass silently on a redundant rebind.</summary>
        [Fact]
        public void AFramebufferFromAnotherBackend_IsRefused()
        {
            var fixture = new VulkanResourceFixture();

            using VulkanCommandList list = fixture.CreateList();
            list.Begin();

            Assert.Throws<ArgumentException>(() => list.SetFramebuffer(new ForeignFramebuffer()));
        }

        // ---- Fixtures ----

        static IGpuTexture Colour(VulkanResourceFixture fixture, List<IDisposable> owned, uint size = 64)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(size, size, GpuTextureUsage.RenderTarget));
            owned.Add(texture);
            return texture;
        }

        static IGpuTexture Depth(VulkanResourceFixture fixture, List<IDisposable> owned, uint size = 64)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(size, size, GpuTextureUsage.DepthStencil,
                    GpuPixelFormat.D32FloatS8UInt));
            owned.Add(texture);
            return texture;
        }

        sealed class ForeignFramebuffer : IGpuFramebuffer
        {
            public GpuOutputDescription Outputs => new(null, GpuPixelFormat.R8G8B8A8UNorm);

            public uint Width => 64;

            public uint Height => 64;

            public void Dispose()
            {
            }
        }
    }
}
