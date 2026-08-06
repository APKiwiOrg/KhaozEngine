using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT ROW 9's FACTORY ACTUALLY ASKS THE DRIVER FOR, driven against the fake resource seam: the eager views
    /// and their ranges (V-M11), the ring-backed sizing (V-M5), the memory ladders, the setup-buffer append with
    /// no queue submit anywhere (V-M10), and the terminal disposal discipline row 6's review asked this row to
    /// settle.
    /// </summary>
    public sealed class VulkanResourceCreationTests
    {
        /// <summary>
        /// A SAMPLED TEXTURE GETS ONE VIEW, OVER THE WHOLE CHAIN AND EVERY LAYER. Full-chain because the seam has
        /// no texture-view type at all, so nothing can ask for a sub-range, and eagerly because all 25
        /// <c>DEVICE_REMOVED</c> stacks in https://github.com/APKiwiOrg/KhaozEngine/issues/423 surfaced inside a
        /// lazy view constructor on the draw path.
        /// </summary>
        [Fact]
        public void ASampledTexture_GetsOneFullChainView()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                64, 32, GpuTextureUsage.Sampled, mipLevels: 5, arrayLayers: 3));

            VulkanImageViewSpec view = Assert.Single(fixture.Views);
            Assert.Equal(0u, view.BaseMipLevel);
            Assert.Equal(5u, view.MipLevels);
            Assert.Equal(0u, view.BaseArrayLayer);
            Assert.Equal(3u, view.ArrayLayers);
        }

        /// <summary>
        /// AN ATTACHMENT VIEW IS MIP 0, LAYER 0, and the reason the bound is real rather than optimistic is that
        /// the seam cannot express anything else: <c>CreateFramebuffer</c> carries no mip or layer parameter,
        /// <c>ResolveTexture</c> is subresource 0 only, and per-face cubemap rendering is not expressible.
        /// </summary>
        [Fact]
        public void AnAttachmentView_IsMipZeroLayerZero()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                64, 32, GpuTextureUsage.RenderTarget, mipLevels: 4, arrayLayers: 2));

            VulkanImageViewSpec view = Assert.Single(fixture.Views);
            Assert.Equal(0u, view.BaseMipLevel);
            Assert.Equal(1u, view.MipLevels);
            Assert.Equal(0u, view.BaseArrayLayer);
            Assert.Equal(1u, view.ArrayLayers);
        }

        /// <summary>
        /// A TEXTURE THAT IS SAMPLED, A RENDER TARGET AND A STORAGE IMAGE GETS EXACTLY THREE VIEWS, one per
        /// declared usage, all at creation. That is the whole of decision V-M11's bound, and the storage view is
        /// the one whose range differs from the sampled one: ONE mip level, because a storage-image binding must
        /// cover exactly one, which the seam's own compute note already says.
        /// </summary>
        [Fact]
        public void ATextureWithThreeUsages_GetsThreeViews_AndTheStorageOneCoversOneMip()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                64, 64, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.Storage,
                mipLevels: 3));

            Assert.Equal(3, fixture.Views.Count);

            // Sampled first, then attachment, then storage: the order VulkanTexture creates them in, which the
            // disposal order below mirrors.
            Assert.Equal(3u, fixture.Views[0].MipLevels);
            Assert.Equal(1u, fixture.Views[1].MipLevels);
            Assert.Equal(1u, fixture.Views[2].MipLevels);
        }

        /// <summary>
        /// A STAGING TEXTURE CREATES NO IMAGE AND NO VIEW AT ALL, and its <c>VkBuffer</c> is sized by the
        /// incumbent's own arithmetic (V-C7). This is where the two shapes of <c>IGpuTexture</c> on this backend
        /// diverge completely.
        /// </summary>
        [Fact]
        public void AStagingTexture_IsABufferWithNoImageAndNoView()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                16, 16, GpuTextureUsage.Staging, mipLevels: 5));

            Assert.Empty(fixture.ResourceApi.Images);
            Assert.Empty(fixture.Views);
            Assert.Equal(0, fixture.SetupSink.CommandCount);

            var native = Assert.IsType<VulkanTexture>(texture);
            Assert.Equal(0UL, native.Image);
            Assert.NotEqual(0UL, native.StagingBuffer);

            // 16x16 R8G8B8A8 over five mips: the table test's own row for this shape.
            Assert.Equal(1364UL, fixture.ResourceApi.SizeOf(native.StagingBuffer));
        }

        /// <summary>
        /// A UNIFORM BUFFER IS RING-BACKED AND ITS NATIVE ALLOCATION IS <c>FramesInFlight</c> SEGMENTS LARGE, while
        /// the size the SEAM sees stays the logical one. That asymmetry is the whole of V-M5: one
        /// <see cref="IGpuBuffer"/> is one <c>VkBuffer</c>, allocated N times larger and never re-pointed, so a
        /// resource set built once at load time still names the same handle.
        /// </summary>
        [Fact]
        public void AUniformBuffer_IsRingBackedAndAllocatesEverySegment()
        {
            var fixture = new VulkanResourceFixture(framesInFlight: 3);

            using IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(100, GpuBufferUsage.UniformBuffer));

            var native = Assert.IsType<VulkanBuffer>(buffer);
            Assert.NotNull(native.Ring);
            Assert.Equal(100u, buffer.SizeInBytes);

            // The stride is the logical size rounded to 256, and the allocation is three of them.
            Assert.Equal(256UL, native.Ring!.SegmentStrideBytes);
            Assert.Equal(768UL, fixture.ResourceApi.SizeOf(native.Handle));
        }

        /// <summary>
        /// A NON-UNIFORM BUFFER IS NOT RING-BACKED and its native buffer is exactly the size asked for.
        /// </summary>
        [Fact]
        public void ANonUniformBuffer_IsNotRingBacked()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(100, GpuBufferUsage.VertexBuffer));

            var native = Assert.IsType<VulkanBuffer>(buffer);
            Assert.Null(native.Ring);
            Assert.Equal(100UL, fixture.ResourceApi.SizeOf(native.Handle));
        }

        /// <summary>
        /// THE RING POLICY IS THE FACTORY'S FIRST STATEMENT, so the combination this backend refuses throws BEFORE
        /// anything native is created. That is the observable difference between a check at the top and a check
        /// anywhere else: a refused creation leaves no handle and no allocation behind.
        /// </summary>
        [Fact]
        public void TheRefusedRingCombination_ThrowsBeforeAnythingIsCreated()
        {
            var fixture = new VulkanResourceFixture();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => fixture.Factory.CreateBuffer(
                new GpuBufferDescription(256, GpuBufferUsage.UniformBuffer | GpuBufferUsage.VertexBuffer)));

            Assert.Contains("RING-BACKED", ex.Message, StringComparison.Ordinal);
            Assert.Contains("documented divergence", ex.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.ResourceApi.Events);
            Assert.Equal(0, fixture.MemoryApi.AllocateCount);
        }

        /// <summary>
        /// TEXTURE CREATION SUBMITS NOTHING (V-M10), which is the claim the incumbent's shape makes false: its
        /// constructor issues a whole <c>vkQueueSubmit</c> for the clear and another for the sampled transition, so
        /// loading a scene with two hundred textures is two hundred submissions before a frame is drawn. Twenty
        /// textures here, one open batch, zero submits.
        /// </summary>
        [Fact]
        public void CreatingTwentyTextures_SubmitsNothing()
        {
            var fixture = new VulkanResourceFixture();

            var textures = new IGpuTexture[20];
            for (int i = 0; i < textures.Length; i++)
            {
                textures[i] = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget));
            }

            Assert.Empty(fixture.CommandApi.Submissions);
            Assert.Equal(0, fixture.Setup.FlushCount);
            Assert.Equal(20, fixture.Setup.AppendCount);
            Assert.True(fixture.Setup.HasPendingWork);

            foreach (IGpuTexture texture in textures) texture.Dispose();
        }

        /// <summary>
        /// A RENDER TARGET IS CLEARED AT CREATION AND ENDS IN ITS RESTING LAYOUT, in the one order that is legal:
        /// out of <c>UNDEFINED</c> into <c>TRANSFER_DST_OPTIMAL</c>, the clear, then out to rest. The clear is
        /// preserved deliberately (V-M10) and only the queue submit that carried it is gone.
        /// </summary>
        [Fact]
        public void ARenderTarget_IsClearedThenLeftAtRest()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));

            Assert.Equal(2, fixture.SetupSink.ImageBarriers.Count);

            FakeImageBarrier first = fixture.SetupSink.ImageBarriers[0];
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.Undefined, first.OldLayout);
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, first.NewLayout);

            FakeClear clear = Assert.Single(fixture.SetupSink.Clears);
            Assert.False(clear.Depth);
            Assert.Equal(0f, clear.Red);

            FakeImageBarrier last = fixture.SetupSink.ImageBarriers[1];
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.TransferDstOptimal, last.OldLayout);
            // Sampled wins over the render target reading, which is V-F7's ladder.
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal, last.NewLayout);
        }

        /// <summary>
        /// A SAMPLED TEXTURE WITH NO CLEAR TAKES ONE BARRIER AND NOTHING ELSE, straight from <c>UNDEFINED</c> to
        /// <c>SHADER_READ_ONLY_OPTIMAL</c>. That is the common case, and it is where the incumbent spends a whole
        /// queue submit.
        /// </summary>
        [Fact]
        public void ASampledTexture_TakesOneBarrierAndNoClear()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.Sampled));

            FakeImageBarrier barrier = Assert.Single(fixture.SetupSink.ImageBarriers);
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.Undefined, barrier.OldLayout);
            Assert.Equal(Silk.NET.Vulkan.ImageLayout.ShaderReadOnlyOptimal, barrier.NewLayout);
            Assert.Empty(fixture.SetupSink.Clears);
        }

        /// <summary>
        /// A DEPTH TARGET TAKES THE DEPTH CLEAR AND THE DEPTH ASPECT, with the incumbent's own value of depth 0 and
        /// stencil 0. The aspect is DEPTH ALONE, matching <c>VkTextureView</c>: nothing in this engine samples or
        /// clears a stencil plane.
        /// </summary>
        [Fact]
        public void ADepthTarget_TakesTheDepthClearAndTheDepthAspect()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.DepthStencil, GpuPixelFormat.D32FloatS8UInt));

            FakeClear clear = Assert.Single(fixture.SetupSink.Clears);
            Assert.True(clear.Depth);
            Assert.Equal(0f, clear.DepthValue);

            Assert.All(fixture.SetupSink.ImageBarriers,
                b => Assert.Equal(Silk.NET.Vulkan.ImageAspectFlags.DepthBit, b.Aspect));
        }

        /// <summary>
        /// DISPOSAL IS ONE TERMINAL RETIRE THAT ENDS EVERY CHILD INLINE, IN THE ONE LEGAL ORDER: every image view
        /// first, then the image, then the memory. A view outliving its image is undefined behaviour, and a view
        /// retired as its OWN entry would be exactly that for one drain's worth of time.
        /// <para>
        /// This is the depth-2 discipline row 6's review asked this row to settle, and the answer it settled on:
        /// resource destroys stay terminal, children are destroyed inline rather than re-retired, so the only
        /// further generation is the chunk the memory free may retire, which is the generation the device's
        /// teardown already drains twice for.
        /// </para>
        /// </summary>
        [Fact]
        public void DisposingATexture_DestroysEveryChildInlineAndInOrder()
        {
            var fixture = new VulkanResourceFixture();

            IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget | GpuTextureUsage.Storage));

            int before = fixture.ResourceApi.Live.Count;
            Assert.Equal(4, before);   // one image plus three views

            texture.Dispose();

            // NOTHING has been destroyed yet: the destroy is HELD behind the timeline.
            Assert.Equal(before, fixture.ResourceApi.Live.Count);

            Assert.Equal(1, fixture.Drain());

            Assert.Empty(fixture.ResourceApi.Live);

            string[] destroys = fixture.ResourceApi.Events
                .Where(e => e.StartsWith("vkDestroy", StringComparison.Ordinal))
                .Select(e => e.Split(' ')[0])
                .ToArray();

            Assert.Equal(
                ["vkDestroyImageView", "vkDestroyImageView", "vkDestroyImageView", "vkDestroyImage"],
                destroys);
        }

        /// <summary>
        /// DISPOSING TWICE IS IDEMPOTENT. A consumer disposing a resource twice is a teardown-order accident rather
        /// than a defect, and retiring the same handles twice would double-destroy them, which the fake seam
        /// refuses by name.
        /// </summary>
        [Fact]
        public void DisposingTwice_RetiresOnce()
        {
            var fixture = new VulkanResourceFixture();

            IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));

            buffer.Dispose();
            buffer.Dispose();

            Assert.Equal(1, fixture.Drain());
            Assert.Empty(fixture.ResourceApi.Live);
        }

        /// <summary>
        /// A DEFERRED DESTROY FREES THE MEMORY TOO, and the memory free is what may retire the chunk it came out
        /// of. That one further generation is the only one this design has, and the device's teardown drains twice
        /// for exactly it.
        /// </summary>
        [Fact]
        public void ADeferredDestroy_FreesTheSuballocationAsWell()
        {
            var fixture = new VulkanResourceFixture();

            IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));

            Assert.Equal(1, fixture.MemoryApi.AllocateCount);
            Assert.Equal(0, fixture.MemoryApi.FreeCount);

            buffer.Dispose();
            fixture.Drain();

            // The pool keeps its last chunk, so the chunk itself is not freed. What matters is that the
            // suballocation went back, which the next allocation of the same size reuses without a new chunk.
            using IGpuBuffer second = fixture.Factory.CreateBuffer(
                new GpuBufferDescription(64, GpuBufferUsage.VertexBuffer));

            Assert.Equal(1, fixture.MemoryApi.AllocateCount);
        }

        /// <summary>
        /// A FAILED CREATION LEAVES NOTHING BEHIND. Between the native create and the last assignment a constructor
        /// holds objects nothing else knows about, so a throw in the middle would leak them for the process's life.
        /// They are destroyed IMMEDIATELY rather than retired, because nothing was ever submitted against a
        /// resource that failed to finish being built.
        /// </summary>
        [Fact]
        public void AFailedViewCreation_LeavesNoImageBehind()
        {
            var fixture = new VulkanResourceFixture();
            fixture.ResourceApi.FailOn = "vkCreateImageView";

            Assert.ThrowsAny<Exception>(() => fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled)));

            Assert.Empty(fixture.ResourceApi.Live);
            Assert.Equal(0, fixture.Retired.Count);
        }

        /// <summary>
        /// THE DEVICE'S SHARED SAMPLER PAIR DOES NOT OWN ITS <c>VkSampler</c>, so a consumer that disposes what
        /// <c>IGpuDevice.PointSampler</c> handed back destroys nothing. Only the device's own teardown ends them.
        /// </summary>
        [Fact]
        public void ASharedSampler_IsNotDestroyedByAConsumersDispose()
        {
            var fixture = new VulkanResourceFixture();

            var shared = new VulkanSampler(fixture.Owner, VulkanSharedSamplers.Linear,
                deviceSamplerAnisotropy: true, ownsSampler: false);

            shared.Dispose();
            fixture.Drain();
            Assert.Single(fixture.ResourceApi.Live);

            shared.DestroyShared();
            Assert.Empty(fixture.ResourceApi.Live);
        }

        /// <summary>
        /// A CONSUMER-CREATED SAMPLER DOES OWN ITS <c>VkSampler</c> and retires it behind the timeline like every
        /// other resource.
        /// </summary>
        [Fact]
        public void AConsumerSampler_RetiresItsOwnSampler()
        {
            var fixture = new VulkanResourceFixture();

            IGpuSampler sampler = fixture.Factory.CreateSampler(GpuSamplerDescription.Point);
            Assert.Single(fixture.ResourceApi.Samplers);

            sampler.Dispose();
            Assert.Single(fixture.ResourceApi.Live);

            fixture.Drain();
            Assert.Empty(fixture.ResourceApi.Live);
        }

        /// <summary>
        /// THE FACTORY'S ANISOTROPY ANSWER COMES FROM THE DEVICE'S CAPABILITY, which is the same member the
        /// engine's Veldrid path reads, so the two agree by construction rather than by luck.
        /// </summary>
        [Fact]
        public void TheFactorysSamplers_ReadTheDevicesAnisotropyCapability()
        {
            var without = new VulkanResourceFixture(samplerAnisotropy: false);

            using IGpuSampler sampler = without.Factory.CreateSampler(new GpuSamplerDescription(
                GpuSamplerFilter.Anisotropic, GpuSamplerAddress.Wrap, GpuSamplerAddress.Wrap,
                GpuSamplerAddress.Wrap, maximumAnisotropy: 8));

            VulkanSamplerSpec spec = Assert.Single(without.ResourceApi.Samplers);
            Assert.False(spec.AnisotropyEnable);
            Assert.Equal(GpuSamplerFilter.MinLinearMagLinearMipLinear, spec.Filter);
        }

        /// <summary>
        /// A SAMPLE COUNT ABOVE THE DEVICE'S CEILING IS REFUSED RATHER THAN ROUNDED DOWN (C4's departure
        /// inherited). The engine clamps upstream in <c>AntiAliasing.ResolveFor</c>, so a count arriving here came
        /// from a caller that skipped it, and a silent downgrade presents as a golden mismatch that reads like a
        /// rendering bug. The message names the capability row, because the ceiling is still the conservative 1
        /// row 4 pinned.
        /// </summary>
        [Fact]
        public void ASampleCountAboveTheCeiling_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture(maxMsaaSampleCount: 1);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget, sampleCount: 4)));

            Assert.Contains("528", ex.Message, StringComparison.Ordinal);
            Assert.Empty(fixture.ResourceApi.Events);

            // And a device whose ceiling is real accepts it.
            var multisampled = new VulkanResourceFixture(maxMsaaSampleCount: 4);
            using IGpuTexture texture = multisampled.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget, sampleCount: 4));

            Assert.Equal(4u, Assert.Single(multisampled.ResourceApi.Images).SampleCount);
        }

        /// <summary>
        /// THE MEMBERS LATER ROWS OWN REFUSE BY NAMING THEIR OWN ISSUE, which is the discipline
        /// <c>D3D11ResourceFactory</c> established between its own row and the ones that filled it in. Asserted
        /// through the seam type so the list cannot drift from what <see cref="IGpuResourceFactory"/> declares.
        /// </summary>
        [Fact]
        public void TheUnbuiltFactoryMembers_NameTheirOwnRow()
        {
            var fixture = new VulkanResourceFixture();
            IGpuResourceFactory factory = fixture.Factory;

            Assert.Contains("522",
                Assert.Throws<NotSupportedException>(() => factory.CreateFramebuffer(null)).Message,
                StringComparison.Ordinal);
            Assert.Contains("520",
                Assert.Throws<NotSupportedException>(() => factory.CreateResourceLayout(default)).Message,
                StringComparison.Ordinal);
            Assert.Contains("520",
                Assert.Throws<NotSupportedException>(() => factory.CreateResourceSet(default)).Message,
                StringComparison.Ordinal);
            Assert.Contains("526",
                Assert.Throws<NotSupportedException>(() => factory.CreateShadersFromSpirv("a", "b")).Message,
                StringComparison.Ordinal);
            Assert.Contains("526",
                Assert.Throws<NotSupportedException>(() => factory.CreateComputeShaderFromSpirv("a")).Message,
                StringComparison.Ordinal);
            Assert.Contains("523",
                Assert.Throws<NotSupportedException>(() => factory.CreateGraphicsPipeline(default)).Message,
                StringComparison.Ordinal);
            Assert.Contains("523",
                Assert.Throws<NotSupportedException>(() => factory.CreateComputePipeline(default)).Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// THE REFUSAL COVERAGE IS HONEST, which the hand-written list above cannot be on its own. Every method on
        /// <see cref="IGpuResourceFactory"/> is either a member THIS row brought alive or a member that refuses by
        /// naming its own row, and the union is exactly what the interface declares.
        /// <para>
        /// THE BUILT SET IS THE ONE THAT MOVES. Rows 10, 12, 13 and 16 each take members off the refusal list and
        /// add them here as they land. A member that simply VANISHED from the refusal list without arriving in the
        /// built set is the regression this pair of sets exists to catch, and it is the same shape
        /// <c>VulkanCommandListTests</c> uses for the recording seam.
        /// </para>
        /// </summary>
        [Fact]
        public void TheFactorysRefusalCoverage_NamesEveryFactoryMember()
        {
            string[] built =
            [
                nameof(IGpuResourceFactory.CreateBuffer),
                nameof(IGpuResourceFactory.CreateTexture),
                nameof(IGpuResourceFactory.CreateSampler),
                nameof(IGpuResourceFactory.CreateCommandList),
                nameof(IGpuResourceFactory.CreateFence),
            ];

            string[] refusing =
            [
                nameof(IGpuResourceFactory.CreateFramebuffer),
                nameof(IGpuResourceFactory.CreateResourceLayout),
                nameof(IGpuResourceFactory.CreateResourceSet),
                nameof(IGpuResourceFactory.CreateShadersFromSpirv),
                nameof(IGpuResourceFactory.CreateComputeShaderFromSpirv),
                nameof(IGpuResourceFactory.CreateGraphicsPipeline),
                nameof(IGpuResourceFactory.CreateComputePipeline),
            ];

            string[] declared = typeof(IGpuResourceFactory).GetMethods()
                .Select(m => m.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(declared, built.Concat(refusing).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Empty(built.Intersect(refusing, StringComparer.Ordinal));
        }

        /// <summary>
        /// A FENCE COMES OFF THE DEVICE'S ONE TIMELINE and creates no native object at all, because a fence on this
        /// backend is a VALUE rather than a <c>VkFence</c>. There is no capability gate in front of it either,
        /// because this backend's <c>SupportsCompletionFences</c> is unconditionally true.
        /// </summary>
        [Fact]
        public void AFence_IsAValueOnTheDevicesTimeline()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuFence fence = fixture.Factory.CreateFence();

            Assert.IsType<VulkanGpuFence>(fence);
            Assert.Empty(fixture.ResourceApi.Events);
        }

        /// <summary>
        /// A CUBEMAP'S IMAGE AND ITS SAMPLED VIEW BOTH CARRY THE LOGICAL LAYER COUNT, and the six-fold expansion
        /// happens on the far side of the seam where the incumbent does it too. Asserting it here rather than in
        /// the fake is what keeps the expansion in ONE place.
        /// </summary>
        [Fact]
        public void ACubemap_PassesItsLogicalLayerCountAcrossTheSeam()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuTexture texture = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                16, 16, GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap, arrayLayers: 2));

            VulkanImageSpec image = Assert.Single(fixture.ResourceApi.Images);
            Assert.True(image.Cubemap);
            Assert.Equal(2u, image.ArrayLayers);

            VulkanImageViewSpec view = Assert.Single(fixture.Views);
            Assert.True(view.Cubemap);
            Assert.Equal(2u, view.ArrayLayers);
        }

        /// <summary>
        /// A TEXTURE WITH A ZERO DIMENSION IS REFUSED before anything native happens. <c>vkCreateImage</c> rejects
        /// it, and a staging texture with one would be a zero-byte buffer.
        /// </summary>
        [Fact]
        public void AZeroSizedTexture_IsRefused()
        {
            var fixture = new VulkanResourceFixture();

            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(0, 8, GpuTextureUsage.Sampled)));
            Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Factory.CreateBuffer(
                new GpuBufferDescription(0, GpuBufferUsage.VertexBuffer)));

            Assert.Empty(fixture.ResourceApi.Events);
        }
    }
}
