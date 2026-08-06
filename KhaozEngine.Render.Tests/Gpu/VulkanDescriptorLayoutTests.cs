using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISIONS V-D4 AND V-D5, device-free: the seam-to-Vulkan descriptor mapping, and the content dedup that
    /// makes row 11's (https://github.com/APKiwiOrg/KhaozEngine/issues/521) pipeline compatibility test a POINTER
    /// COMPARE. Work-breakdown row 10 (https://github.com/APKiwiOrg/KhaozEngine/issues/520).
    /// <para>
    /// The dedup half is the part that is easy to mistake for a micro-optimisation and is not. Identity-shared
    /// set layouts are what make bound descriptors survive a pipeline switch: the incumbent creates one handle per
    /// <c>ResourceLayout</c> object with no dedup at all, so nothing is ever compatible with anything and every
    /// switch forces a full rebind of every set.
    /// </para>
    /// </summary>
    public sealed class VulkanDescriptorLayoutTests
    {
        static GpuResourceLayoutElement E(string name, GpuResourceKind kind,
            GpuShaderStages stages = GpuShaderStages.Fragment, bool dynamic = false)
            => new(name, kind, stages, dynamic);

        static (VulkanDescriptorSetLayoutCache Cache, FakeVulkanDescriptorApi Api) NewCache()
        {
            var api = new FakeVulkanDescriptorApi();
            return (new VulkanDescriptorSetLayoutCache(api), api);
        }

        /// <summary>
        /// EVERY UNIFORM BUFFER ELEMENT BECOMES <c>UNIFORM_BUFFER_DYNAMIC</c> (V-D4), NOT ONLY THE ONE THE LAYOUT
        /// DECLARED DYNAMIC. The per-frame ring base has to be applied at bind, and the only bind-time knob Vulkan
        /// offers on a uniform buffer is the dynamic offset, so the descriptor type is decided by the KIND alone.
        /// </summary>
        [Fact]
        public void EveryUniformBufferElement_BecomesADynamicUniformDescriptor()
        {
            (VulkanDescriptorSetLayoutCache cache, _) = NewCache();

            using var layout = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex | GpuShaderStages.Fragment),
                E("Tex", GpuResourceKind.TextureReadOnly),
                E("Samp", GpuResourceKind.Sampler)));

            Assert.Equal(VulkanDescriptorType.UniformBufferDynamic, layout.Bindings[0].Type);
            Assert.Equal(1, layout.DynamicUniformCount);

            // And the element carried NO declared dynamic flag, which is the whole point.
            Assert.Equal(0, layout.DeclaredDynamicCount);
        }

        /// <summary>
        /// THE DECLARED FLAG DECIDES EXACTLY ONE THING and it is not the descriptor type. Two layouts identical
        /// but for <see cref="GpuResourceLayoutElement.Dynamic"/> produce the same Vulkan type, the same counts
        /// and the SAME SHARED HANDLE, and differ only in
        /// <see cref="VulkanResourceLayout.DeclaredDynamicCount"/>, which is what row 11 reads to decide whether
        /// the caller's own per-draw offset is added on top of the ring base.
        /// <para>
        /// The shared handle is the load-bearing half: a key that included the declared flag would split one
        /// driver object into two for a distinction the driver cannot see, and two genuinely compatible pipelines
        /// would compare as incompatible for the rest of the run.
        /// </para>
        /// </summary>
        [Fact]
        public void TheDeclaredFlag_DecidesOnlyTheCallersOwnOffsetAndNotTheHandle()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            using var plain = new VulkanResourceLayout(cache, VulkanResourceFixture.UniformLayout(dynamic: false));
            using var declared = new VulkanResourceLayout(cache, VulkanResourceFixture.UniformLayout(dynamic: true));

            Assert.Equal(plain.Bindings[0].Type, declared.Bindings[0].Type);
            Assert.Equal(plain.Counts, declared.Counts);
            Assert.Equal(plain.SetLayout, declared.SetLayout);
            Assert.Equal(1, api.SetLayoutCreateCount);

            Assert.Equal(0, plain.DeclaredDynamicCount);
            Assert.Equal(1, declared.DeclaredDynamicCount);
        }

        /// <summary>
        /// THE KIND MAPPING IS SECTION 8.1's TABLE, one row at a time. Both structured kinds map to
        /// <c>STORAGE_BUFFER</c>, because the seam's read-only against read-write distinction is an ACCESS
        /// statement the shader makes and Vulkan carries the access on the shader's own declaration.
        /// </summary>
        [Fact]
        public void TheKindMapping_IsTheDesignsTable()
        {
            (GpuResourceKind Kind, VulkanDescriptorType Expected)[] table =
            [
                (GpuResourceKind.UniformBuffer, VulkanDescriptorType.UniformBufferDynamic),
                (GpuResourceKind.StructuredBufferReadOnly, VulkanDescriptorType.StorageBuffer),
                (GpuResourceKind.StructuredBufferReadWrite, VulkanDescriptorType.StorageBuffer),
                (GpuResourceKind.TextureReadOnly, VulkanDescriptorType.SampledImage),
                (GpuResourceKind.TextureReadWrite, VulkanDescriptorType.StorageImage),
                (GpuResourceKind.Sampler, VulkanDescriptorType.Sampler),
            ];

            // EVERY member of the seam's enum has a row, so a new kind added without a mapping fails here rather
            // than at whichever renderer first declares one.
            Assert.Equal(Enum.GetValues<GpuResourceKind>().Length, table.Length);

            foreach ((GpuResourceKind kind, VulkanDescriptorType expected) in table)
                Assert.Equal(expected, VulkanDescriptorPolicy.TypeFor(E("x", kind)));
        }

        /// <summary>
        /// AN IMAGE AND ITS SAMPLER ARE TWO DESCRIPTORS AND NEVER A <c>COMBINED_IMAGE_SAMPLER</c>, which the
        /// engine's shared GLSL sources already assume by declaring <c>texture2D</c> and <c>sampler</c>
        /// separately. A combined type here would make every fragment shader in the engine fail to link.
        /// </summary>
        [Fact]
        public void ASampledImageAndItsSampler_AreSeparateDescriptors()
        {
            (VulkanDescriptorSetLayoutCache cache, _) = NewCache();

            using var layout = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("Tex", GpuResourceKind.TextureReadOnly), E("Samp", GpuResourceKind.Sampler)));

            Assert.Equal(VulkanDescriptorType.SampledImage, layout.Bindings[0].Type);
            Assert.Equal(VulkanDescriptorType.Sampler, layout.Bindings[1].Type);
            Assert.Equal(new VulkanDescriptorCounts(0, 0, 0, 0, SampledImage: 1, StorageImage: 0, Sampler: 1),
                layout.Counts);
        }

        /// <summary>
        /// BINDING INDEX EQUALS ELEMENT INDEX AND <c>descriptorCount</c> IS ALWAYS 1 (8.1), which is what lets a
        /// resource set match its resources to its layout's elements positionally with no lookup at all.
        /// </summary>
        [Fact]
        public void BindingIndex_IsElementIndexAndTheCountIsAlwaysOne()
        {
            (VulkanDescriptorSetLayoutCache cache, _) = NewCache();

            using var layout = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("a", GpuResourceKind.TextureReadOnly), E("b", GpuResourceKind.Sampler),
                E("c", GpuResourceKind.UniformBuffer), E("d", GpuResourceKind.StructuredBufferReadWrite)));

            for (int i = 0; i < layout.ElementCount; i++)
            {
                Assert.Equal((uint)i, layout.Bindings[i].Binding);
                Assert.Equal(1u, layout.Bindings[i].DescriptorCount);
            }
        }

        /// <summary>
        /// A DECLARED-DYNAMIC ELEMENT THAT IS NOT A UNIFORM BUFFER IS REFUSED BY NAME, before anything native
        /// happens. Accepting one would leave the caller's per-draw offset with no dynamic descriptor to land on,
        /// so it is silently dropped, and a bind that supplied one anyway would carry a
        /// <c>dynamicOffsetCount</c> the set's dynamic descriptors do not match. The Direct3D 11 backend refuses
        /// the structured half of this for its own reason, so an element like this is already unshippable on two
        /// of the three backends.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.StructuredBufferReadOnly)]
        [InlineData(GpuResourceKind.StructuredBufferReadWrite)]
        [InlineData(GpuResourceKind.TextureReadOnly)]
        [InlineData(GpuResourceKind.TextureReadWrite)]
        [InlineData(GpuResourceKind.Sampler)]
        public void ADeclaredDynamicElementThatIsNotAUniformBuffer_IsRefusedByName(GpuResourceKind kind)
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new VulkanResourceLayout(
                cache, new GpuResourceLayoutDescription(E("Windowed", kind, dynamic: true))));

            Assert.Contains("Windowed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("V-D4", ex.Message, StringComparison.Ordinal);
            Assert.Empty(api.Events);
        }

        /// <summary>
        /// TWO LAYOUTS WITH IDENTICAL CONTENT SHARE ONE <c>VkDescriptorSetLayout</c> (V-D5). This is the
        /// assertion the whole decision reduces to, and the one the incumbent fails by construction.
        /// </summary>
        [Fact]
        public void TwoIdenticalLayouts_ShareOneDescriptorSetLayout()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            GpuResourceLayoutDescription description = new(
                E("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                E("Tex", GpuResourceKind.TextureReadOnly), E("Samp", GpuResourceKind.Sampler));

            using var first = new VulkanResourceLayout(cache, description);
            using var second = new VulkanResourceLayout(cache, description);

            Assert.Equal(first.SetLayout, second.SetLayout);
            Assert.Equal(1, api.SetLayoutCreateCount);
            Assert.Equal(1, cache.DistinctLayoutCount);
            Assert.Equal(2, cache.RequestCount);
        }

        /// <summary>
        /// AND SO DO TWO LAYOUTS DIFFERING ONLY IN THEIR ELEMENT NAMES, which is the omission from the key that
        /// looks like sloppiness and is load-bearing: Vulkan binds by NUMBER, so two layouts differing only in
        /// names are one object to the driver, and giving them separate handles would make a genuinely compatible
        /// pipeline pair compare as incompatible for the rest of the run.
        /// </summary>
        [Fact]
        public void LayoutsDifferingOnlyInElementNames_ShareOneHandle()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            using var frame = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("Frame", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using var water = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("Water", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));

            Assert.Equal(frame.SetLayout, water.SetLayout);
            Assert.Equal(1, api.SetLayoutCreateCount);
        }

        /// <summary>
        /// WHAT DOES NOT SHARE: a different stage mask, a different type, a different order, a different length.
        /// Each of those is a genuinely different <c>VkDescriptorSetLayout</c>, and a key that collapsed any of
        /// them would hand out a handle whose bindings do not match the shader.
        /// </summary>
        [Fact]
        public void LayoutsDifferingInAnythingTheCreateInfoCarries_DoNotShare()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            var handles = new List<ulong>();

            foreach (GpuResourceLayoutDescription description in new[]
            {
                new GpuResourceLayoutDescription(E("a", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)),
                new GpuResourceLayoutDescription(E("a", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)),
                new GpuResourceLayoutDescription(E("a", GpuResourceKind.TextureReadOnly, GpuShaderStages.Vertex)),
                new GpuResourceLayoutDescription(
                    E("a", GpuResourceKind.TextureReadOnly), E("b", GpuResourceKind.Sampler)),
                new GpuResourceLayoutDescription(
                    E("a", GpuResourceKind.Sampler), E("b", GpuResourceKind.TextureReadOnly)),
            })
            {
                using var layout = new VulkanResourceLayout(cache, description);
                handles.Add(layout.SetLayout);
            }

            Assert.Equal(5, handles.Distinct().Count());
            Assert.Equal(5, api.SetLayoutCreateCount);
        }

        /// <summary>
        /// AN EMPTY LAYOUT IS REAL, and two of them share one handle like anything else. The seam permits
        /// <c>new GpuResourceLayoutDescription()</c> and shipped tests use one, so a backend that refused it or
        /// treated it as a null would fail on the smoke tests rather than on anything interesting.
        /// </summary>
        [Fact]
        public void AnEmptyLayout_IsRealAndShares()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            using var first = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription());
            using var second = new VulkanResourceLayout(cache, default);

            Assert.Equal(0, first.ElementCount);
            Assert.True(first.Counts.IsEmpty);
            Assert.Equal(first.SetLayout, second.SetLayout);
            Assert.Equal(1, api.SetLayoutCreateCount);
            Assert.Empty(api.SetLayouts[first.SetLayout]);
        }

        /// <summary>
        /// DISPOSING A LAYOUT DESTROYS NOTHING, and it must not: the handle is shared by every layout with the
        /// same content, so ending it here would leave the others naming a destroyed object. Only the cache's own
        /// teardown releases one.
        /// </summary>
        [Fact]
        public void DisposingALayout_DestroysNothingAndTeardownDestroysEverything()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            var first = new VulkanResourceLayout(cache, VulkanResourceFixture.UniformLayout());
            var second = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                E("Tex", GpuResourceKind.TextureReadOnly)));

            first.Dispose();
            second.Dispose();

            Assert.True(first.IsDisposed);
            Assert.Equal(0, api.Events.Count(e => e.StartsWith("vkDestroy", StringComparison.Ordinal)));
            Assert.Equal(2, api.Live.Count);

            Assert.Equal(2, cache.DestroyAll());
            Assert.Empty(api.Live);
            Assert.Equal(0, cache.DistinctLayoutCount);
        }

        /// <summary>
        /// CREATION IS FREE-THREADED (V-W8), SO ONE CONTENT IS STILL ONE HANDLE UNDER CONCURRENT CREATION. Two
        /// threads that both missed the cache would both create, leaving one handle leaked and every later
        /// compatibility compare against it answering "incompatible" for a layout that is not, which is why the
        /// lock is held across the <c>vkCreateDescriptorSetLayout</c> itself rather than around the dictionary
        /// alone.
        /// </summary>
        [Fact]
        public void OneContent_IsOneHandleUnderConcurrentCreation()
        {
            (VulkanDescriptorSetLayoutCache cache, FakeVulkanDescriptorApi api) = NewCache();

            var handles = new ulong[64];
            Parallel.For(0, handles.Length, i =>
            {
                using var layout = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(
                    E("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
                    E("Tex", GpuResourceKind.TextureReadOnly)));
                handles[i] = layout.SetLayout;
            });

            Assert.Single(handles.Distinct());
            Assert.Equal(1, api.SetLayoutCreateCount);
            Assert.Equal(handles.Length, cache.RequestCount);
        }

        /// <summary>
        /// THE DESCRIPTION'S ELEMENT ARRAY IS COPIED. It is a public struct holding a reference, so a caller that
        /// reused or mutated it after creation would otherwise re-shape a layout whose native handle has already
        /// been created and SHARED with every other layout of that content.
        /// </summary>
        [Fact]
        public void TheDescriptionsElementArray_IsCopied()
        {
            (VulkanDescriptorSetLayoutCache cache, _) = NewCache();

            GpuResourceLayoutElement[] elements =
            [
                E("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex),
            ];

            using var layout = new VulkanResourceLayout(cache, new GpuResourceLayoutDescription(elements));

            elements[0] = E("Tex", GpuResourceKind.TextureReadOnly);

            Assert.Equal(VulkanDescriptorType.UniformBufferDynamic, layout.Bindings[0].Type);
            Assert.Equal("U", layout.Elements[0].Name);
        }

        /// <summary>
        /// A LAYOUT FROM ANOTHER BACKEND IS REFUSED BY NAME rather than cast-crashing, which is the message a
        /// consumer that mixed two devices actually needs.
        /// </summary>
        [Fact]
        public void ALayoutFromAnotherBackend_IsRefusedByName()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => VulkanResourceLayout.Require(new ForeignLayout(), "a resource set"));

            Assert.Contains("native Vulkan backend", ex.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentException>(() => VulkanResourceLayout.Require(null, "a resource set"));
        }

        sealed class ForeignLayout : IGpuResourceLayout
        {
            public void Dispose() { }
        }
    }
}
