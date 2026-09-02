using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT <see cref="GpuResourceLayoutElement.Dynamic"/> ACTUALLY GUARANTEES, asserted across all three
    /// backends in one place (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/597">#597</see>).
    ///
    /// <para><b>THE SEAM USED TO PROMISE A WIDTH TWO OF THREE BACKENDS REFUSE.</b> The doc said "a dynamic-offset
    /// uniform/structured buffer", so both structured kinds were inside the stated contract, and both siblings
    /// throw at layout creation on one: Vulkan because a storage descriptor has no dynamic offset at all, and
    /// Direct3D 11 because a structured buffer binds through a view created once over the whole buffer and
    /// neither <c>*SetShaderResources</c> nor <c>*SetUnorderedAccessViews</c> carries a per-bind window. The
    /// second is not a gap anyone can close, so the seam narrowed to the uniform buffer and the Metal backend's
    /// extra width became a documented superset rather than the contract.</para>
    ///
    /// <para><b>THE CHANGE WAS PROSE, SO THIS CLASS ASSERTS RATHER THAN DRIVES.</b> No backend behaviour moved
    /// and no golden could. What it pins is that the narrowed doc describes what the code does, in both
    /// directions, which is the thing a doc-only change can silently stop being true about. Each backend already
    /// has its own row for its own refusal in its own suite. This one exists because a contract nobody reads in
    /// one sitting is how the old claim survived three backends.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Either a sibling started accepting a dynamic structured element, in
    /// which case the seam can widen, or Metal started refusing one, in which case the superset paragraph on its
    /// README is now wrong. Device-free on every leg.</para>
    /// </summary>
    public sealed class DynamicOffsetContractTests
    {
        static GpuResourceLayoutElement Dynamic(GpuResourceKind kind)
            => new("Windowed", kind, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic: true);

        /// <summary>
        /// THE CONTRACT ITSELF: a dynamic UNIFORM buffer is accepted by all three, which is the whole of what the
        /// narrowed doc promises. Everything below is about what is OUTSIDE it.
        /// </summary>
        [Fact]
        public void ADynamicUniformBuffer_IsAcceptedByEveryBackend()
        {
            GpuResourceLayoutElement element = Dynamic(GpuResourceKind.UniformBuffer);

            using var d3d11 = new D3D11ResourceLayout(new GpuResourceLayoutDescription(element));
            Assert.True(d3d11.Elements[0].Dynamic);

            using var vulkan = new VulkanResourceLayout(
                new VulkanDescriptorSetLayoutCache(new FakeVulkanDescriptorApi()),
                new GpuResourceLayoutDescription(element));
            Assert.Equal(1, vulkan.ElementCount);

            using var metal = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(), new GpuResourceLayoutDescription(element));
            Assert.True(metal.ElementAt(0).Dynamic);
        }

        /// <summary>
        /// AND THE STRUCTURED KINDS ARE OUTSIDE IT, on both siblings, which is why the doc narrowed instead of the
        /// siblings widening. Both refuse at LAYOUT CREATION, which is the last moment the declaration is still in
        /// front of the caller rather than dropped at a bind nothing reports.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.StructuredBufferReadOnly)]
        [InlineData(GpuResourceKind.StructuredBufferReadWrite)]
        public void ADynamicStructuredElement_IsRefusedByBothSiblings(GpuResourceKind kind)
        {
            var description = new GpuResourceLayoutDescription(Dynamic(kind));

            Assert.Throws<System.ArgumentException>(() => new D3D11ResourceLayout(description));

            Assert.Throws<System.ArgumentException>(() => new VulkanResourceLayout(
                new VulkanDescriptorSetLayoutCache(new FakeVulkanDescriptorApi()), description));
        }

        /// <summary>
        /// AND METAL TAKES IT, which the seam names as a documented superset rather than pretending away. It is
        /// kept because <c>setBufferOffset:</c> genuinely works at any buffer index and refusing a declaration
        /// this backend can honour would buy nothing. Writing one still makes the consumer macOS-only, which is
        /// the sentence the narrowed doc and this backend's README both carry.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.StructuredBufferReadOnly)]
        [InlineData(GpuResourceKind.StructuredBufferReadWrite)]
        public void ADynamicStructuredElement_IsAcceptedByMetalAsADocumentedSuperset(GpuResourceKind kind)
        {
            using var metal = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(), new GpuResourceLayoutDescription(Dynamic(kind)));

            Assert.True(metal.ElementAt(0).Dynamic);
        }
    }
}
