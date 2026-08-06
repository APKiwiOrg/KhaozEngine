using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISIONS V-D1 AND V-M6, device-free: ONE <c>VkDescriptorSet</c> allocated and written ONCE at creation
    /// with a single <c>vkUpdateDescriptorSets</c> covering every binding, and a range that is the BIND WINDOW and
    /// is never <c>VK_WHOLE_SIZE</c> and never the ring stride. Work-breakdown row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/520).
    ///
    /// <para><b>THE WRITE-ONCE SET IS A PORT.</b> The incumbent already allocates one set and issues one update at
    /// creation and never touches it again. What is new is that it holds by CONSTRUCTION rather than by the
    /// incumbent happening to be written that way, which is
    /// <c>VulkanRecordingUnreachabilityTests</c>'s business, and the range, which is this file's.</para>
    ///
    /// <para><b>A STRIDE-SIZED RANGE IS THE SHAPE THAT LOOKS SAFE AND IS NOT.</b> At the last frame slot a range
    /// of <c>stride</c> overruns the buffer by exactly the caller's own per-draw offset, and five shipped
    /// renderers pass a non-zero one, so it violates
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> on one frame in three rather than on every
    /// frame.</para>
    /// </summary>
    public sealed class VulkanResourceSetTests
    {
        static GpuResourceLayoutElement E(string name, GpuResourceKind kind,
            GpuShaderStages stages = GpuShaderStages.Fragment, bool dynamic = false)
            => new(name, kind, stages, dynamic);

        static VulkanDescriptorWrite[] WritesOf(VulkanResourceFixture fixture, IGpuResourceSet set)
            => fixture.DescriptorApi.WritesFor(((VulkanResourceSet)set).DescriptorSet);

        /// <summary>
        /// ONE ALLOCATION AND ONE UPDATE COVERING EVERY BINDING (V-D1), in binding order, and nothing afterwards
        /// for the set's whole life.
        /// </summary>
        [Fact]
        public void ASet_IsAllocatedOnceAndWrittenOnceCoveringEveryBinding()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                    E("Tex", GpuResourceKind.TextureReadOnly),
                    E("Samp", GpuResourceKind.Sampler)));

            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));
            using IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
            using IGpuSampler sampler = fixture.Factory.CreateSampler(GpuSamplerDescription.Linear);

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, uniform, texture, sampler));

            Assert.Equal(1, fixture.DescriptorApi.AllocateCount);
            Assert.Equal(1, fixture.DescriptorApi.UpdateCount);

            VulkanDescriptorWrite[] writes = WritesOf(fixture, set);
            Assert.Equal(3, writes.Length);
            Assert.Equal(new uint[] { 0, 1, 2 }, writes.Select(w => w.Binding).ToArray());
            Assert.Equal(VulkanDescriptorType.UniformBufferDynamic, writes[0].Type);
            Assert.Equal(VulkanDescriptorType.SampledImage, writes[1].Type);
            Assert.Equal(VulkanDescriptorType.Sampler, writes[2].Type);
        }

        /// <summary>
        /// THE RANGE IS THE BUFFER'S OWN LOGICAL SIZE WHEN THE SET WAS CREATED FROM A BARE BUFFER, AND IT IS
        /// NEITHER <c>VK_WHOLE_SIZE</c> NOR THE STRIDE (V-M6). The buffer here is 64 bytes and its ring stride is
        /// 256, so all three candidate answers are distinguishable and the test can only pass for one of them.
        /// <para>
        /// The native allocation is <c>FramesInFlight</c> segments wide, so binding whole-size would address every
        /// frame's copy at once and adding the frame base on top of that would address past the end of the buffer.
        /// </para>
        /// </summary>
        [Fact]
        public void ABareBufferBinding_TakesTheLogicalSizeAndNeitherWholeSizeNorTheStride()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout());
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(64, GpuBufferUsage.UniformBuffer));

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, uniform));

            VulkanDescriptorWrite write = Assert.Single(WritesOf(fixture, set));

            ulong stride = VulkanRingStride.SegmentStrideFor(64, 0);
            Assert.Equal(256UL, stride);

            Assert.Equal(64UL, write.BufferRange);
            Assert.NotEqual(stride, write.BufferRange);
            Assert.NotEqual(ulong.MaxValue, write.BufferRange);
        }

        /// <summary>
        /// AND IT IS <see cref="GpuBufferRange.Size"/> WHEN THE SET WAS CREATED FROM A RANGE, which is the shape
        /// every shipped dynamic-offset call site uses: a slot array bound one slot wide and addressed per draw.
        /// </summary>
        [Fact]
        public void AWindowedBinding_TakesTheWindowSize()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout(dynamic: true));
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256 * 8, GpuBufferUsage.UniformBuffer));

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, new GpuBufferRange(uniform, 0, 64)));

            VulkanDescriptorWrite write = Assert.Single(WritesOf(fixture, set));
            Assert.Equal(64UL, write.BufferRange);
        }

        /// <summary>
        /// A DYNAMIC UNIFORM DESCRIPTOR IS WRITTEN WITH <c>offset = 0</c> AND ITS RANGE OFFSET TRAVELS AT BIND
        /// TIME. Vulkan ADDS the bind-time dynamic offset to the descriptor's own, so putting the window's offset
        /// in both would double it, which reads the wrong slice of the right buffer and renders plausible garbage
        /// rather than throwing.
        /// </summary>
        [Fact]
        public void ADynamicUniformDescriptor_HasOffsetZeroAndCarriesItsWindowToTheBind()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout(dynamic: true));
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(1024, GpuBufferUsage.UniformBuffer));

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, new GpuBufferRange(uniform, 128, 64)));

            VulkanDescriptorWrite write = Assert.Single(WritesOf(fixture, set));
            Assert.Equal(0UL, write.BufferOffset);

            VulkanDynamicUniform dynamic = Assert.Single(((VulkanResourceSet)set).DynamicUniforms.ToArray());
            Assert.Equal(0u, dynamic.Binding);
            Assert.Equal(128UL, dynamic.RangeOffset);
            Assert.Equal(64UL, dynamic.Range);
            Assert.True(dynamic.AppliesCallerOffset);
            Assert.Same(((VulkanBuffer)uniform).Ring, dynamic.Ring);
        }

        /// <summary>
        /// A UNIFORM ELEMENT THAT WAS NOT DECLARED DYNAMIC IS STILL A DYNAMIC DESCRIPTOR (V-D4) AND STILL GETS A
        /// FRAME BASE, and the ONE thing the declared flag changes is whether the caller's own offset is added on
        /// top. That distinction is the one most easily lost, so it is asserted directly.
        /// </summary>
        [Fact]
        public void AnUndeclaredUniformElement_IsStillDynamicButTakesNoCallerOffset()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout(dynamic: false));
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, uniform));

            Assert.Equal(VulkanDescriptorType.UniformBufferDynamic,
                Assert.Single(WritesOf(fixture, set)).Type);

            VulkanDynamicUniform dynamic = Assert.Single(((VulkanResourceSet)set).DynamicUniforms.ToArray());
            Assert.False(dynamic.AppliesCallerOffset);
        }

        /// <summary>
        /// A NON-DYNAMIC BUFFER PUTS ITS WINDOW OFFSET IN THE DESCRIPTOR'S OWN <c>offset</c>, because there is no
        /// bind-time term for it to travel in. The mirror image of the rule above, and getting the pair the wrong
        /// way round loses the offset in one direction and doubles it in the other.
        /// </summary>
        [Fact]
        public void ANonDynamicBufferBinding_PutsItsWindowOffsetInTheDescriptor()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("Buf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
            using IGpuBuffer storage = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(1024, GpuBufferUsage.StructuredBufferReadWrite));

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, new GpuBufferRange(storage, 256, 128)));

            VulkanDescriptorWrite write = Assert.Single(WritesOf(fixture, set));
            Assert.Equal(VulkanDescriptorType.StorageBuffer, write.Type);
            Assert.Equal(256UL, write.BufferOffset);
            Assert.Equal(128UL, write.BufferRange);
            Assert.Empty(((VulkanResourceSet)set).DynamicUniforms.ToArray());
        }

        /// <summary>
        /// SAMPLED IMAGES BIND <c>SHADER_READ_ONLY_OPTIMAL</c> AND STORAGE IMAGES BIND <c>GENERAL</c> (8.1),
        /// through the EAGER views the texture already has rather than through anything created here (V-M11).
        /// </summary>
        [Fact]
        public void ImageDescriptors_TakeTheirEagerViewAndTheirOwnLayout()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("Src", GpuResourceKind.TextureReadOnly),
                    E("Dst", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute)));

            using IGpuTexture sampled = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
            using IGpuTexture storage = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Storage));

            int viewsBefore = fixture.Views.Count;

            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, sampled, storage));

            // NO VIEW WAS CREATED HERE. Every one already existed, which is decision V-M11 seen from the side
            // that would most plausibly break it.
            Assert.Equal(viewsBefore, fixture.Views.Count);

            VulkanDescriptorWrite[] writes = WritesOf(fixture, set);
            Assert.Equal(((VulkanTexture)sampled).SampledView, writes[0].ImageView);
            Assert.Equal(VulkanDescriptorImageLayout.ShaderReadOnlyOptimal, writes[0].ImageLayout);
            Assert.Equal(((VulkanTexture)storage).StorageView, writes[1].ImageView);
            Assert.Equal(VulkanDescriptorImageLayout.General, writes[1].ImageLayout);
        }

        /// <summary>
        /// A LAYOUT WITH NO ELEMENTS ALLOCATES A SET AND WRITES NOTHING. A zero-length
        /// <c>vkUpdateDescriptorSets</c> is legal and says nothing, so it is not made at all, and the set is still
        /// real because the seam permits an empty layout and shipped tests use one.
        /// </summary>
        [Fact]
        public void AnEmptyLayout_AllocatesASetAndWritesNothing()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription());
            using IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout));

            Assert.Equal(1, fixture.DescriptorApi.AllocateCount);
            Assert.Equal(0, fixture.DescriptorApi.UpdateCount);
            Assert.NotEqual(0UL, ((VulkanResourceSet)set).DescriptorSet);
        }

        /// <summary>
        /// DISPOSAL IS ONE DEFERRED FREE, through the same retire list every other resource uses, because a
        /// descriptor set freed while a submission that binds it is still executing is undefined behaviour.
        /// Idempotent, because a consumer disposing twice is a teardown-order accident and a double free is not
        /// recoverable.
        /// </summary>
        [Fact]
        public void DisposingASet_FreesItOnceAndBehindTheTimeline()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                VulkanResourceFixture.UniformLayout());
            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));

            IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, uniform));

            set.Dispose();
            set.Dispose();

            Assert.Equal(0, fixture.DescriptorApi.FreeCount);

            fixture.Drain();

            Assert.Equal(1, fixture.DescriptorApi.FreeCount);
        }

        /// <summary>
        /// EVERY WAY A SET CAN BE BUILT WRONG IS REFUSED BY NAME, naming the ELEMENT rather than an index,
        /// because a message about "element 4" is unactionable in a seven-element material layout. A descriptor
        /// set is written once at creation and never again, so there is no later point at which any of these could
        /// be corrected.
        /// </summary>
        [Fact]
        public void EveryWrongResource_IsRefusedByTheElementsName()
        {
            var fixture = new VulkanResourceFixture();

            using IGpuResourceLayout uniformLayout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using IGpuResourceLayout textureLayout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(E("Albedo", GpuResourceKind.TextureReadOnly)));
            using IGpuResourceLayout storageImageLayout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("Target", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute)));
            using IGpuResourceLayout storageLayout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    E("Bones", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Vertex)));

            using IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));
            using IGpuBuffer vertex = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.VertexBuffer));
            using IGpuTexture renderTarget = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget));
            using IGpuTexture sampled = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));

            // Too few resources for the layout's elements.
            Assert.Contains("POSITIONALLY", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout))), StringComparison.Ordinal);

            // A null resource, which no later write could fill in.
            Assert.Contains("Frame", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout, [null!]))), StringComparison.Ordinal);

            // A texture where a buffer belongs.
            Assert.Contains("Frame", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout, sampled))), StringComparison.Ordinal);

            // A buffer with no ring on a uniform element, which would be bound with an offset nothing supplies.
            Assert.Contains("ring-backed", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout, vertex))), StringComparison.Ordinal);

            // A ring-backed buffer on a storage element, which would address segment zero forever while the
            // writes went to the current segment.
            Assert.Contains("segment zero", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(storageLayout, uniform))), StringComparison.Ordinal);

            // A texture with no sampled view, because it was not created with the usage that makes one.
            Assert.Contains("Albedo", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(textureLayout, renderTarget))), StringComparison.Ordinal);

            // And with no storage view, for the same reason.
            Assert.Contains("Target", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(storageImageLayout, sampled))), StringComparison.Ordinal);

            // A zero-length window, which is the caller's way of writing VK_WHOLE_SIZE by accident.
            Assert.Contains("BIND WINDOW", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout, new GpuBufferRange(uniform, 0, 0)))),
                StringComparison.Ordinal);

            // And one that leaves the buffer.
            Assert.Contains("BIND WINDOW", Throws(() => fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(uniformLayout, new GpuBufferRange(uniform, 200, 128)))),
                StringComparison.Ordinal);

            // NOTHING NATIVE HAPPENED for any of them: no set was allocated and none was written, which is what
            // keeps a caller error from leaving a half-built descriptor set behind.
            Assert.Equal(0, fixture.DescriptorApi.AllocateCount);
            Assert.Equal(0, fixture.DescriptorApi.UpdateCount);
        }

        /// <summary>
        /// A SET FROM ANOTHER BACKEND IS REFUSED BY NAME, which is what row 11 binds through.
        /// </summary>
        [Fact]
        public void ASetFromAnotherBackend_IsRefusedByName()
        {
            Assert.Contains("native Vulkan backend",
                Assert.Throws<ArgumentException>(
                    () => VulkanResourceSet.Require(new ForeignSet(), "a bind")).Message,
                StringComparison.Ordinal);
        }

        static string Throws(Action action) => Assert.Throws<ArgumentException>(action).Message;

        sealed class ForeignSet : IGpuResourceSet
        {
            public void Dispose() { }
        }
    }
}
