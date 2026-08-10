using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROW 10'S DEVICE-FREE HALF (https://github.com/APKiwiOrg/KhaozEngine/issues/576): everything a resource
    /// layout decides, plus the one piece of a resource set's resolution that is arithmetic rather than a check
    /// against a wrapper only a device can make.
    ///
    /// <para>
    /// THE HEADLINE ROW IS THAT NOTHING COUNTS. The incumbent's <c>MTLResourceLayout</c> is this class plus
    /// per-kind counters, and <c>GetBufferBase</c> and its siblings re-walk the layout array on every single bind
    /// to sum them. Section 2.2b rules that arithmetic is written ONCE, as the comparison inside
    /// <c>MetalShaderIndexTableTests</c>, and never on a shipped path, so what a layout carries here is
    /// declaration ORDER and nothing derived from it. The index authority is <c>MetalShaderIndexTable</c>, read
    /// off the emitted MSL.
    /// </para>
    /// <para>
    /// A LAYOUT MAKES NO NATIVE CALL AT ALL, which is why this is a plain <c>[Fact]</c> suite rather than a
    /// dormant-off-macOS one. Metal has no layout object: an argument table is addressed by integer per stage, so
    /// a layout is purely the engine's own bookkeeping and its whole decision surface runs on the free Linux leg.
    /// <c>MetalResourceSetGpuTests</c> is the other half, where a set's resolution meets real buffers, textures
    /// and samplers.
    /// </para>
    /// </summary>
    public sealed class MetalResourceLayoutTests
    {
        [Fact]
        public void ADeclaredLayout_KeepsItsElementsInDeclarationOrder()
        {
            var liveness = new FakeMetalDeviceLiveness();
            using var layout = new MetalResourceLayout(liveness, new GpuResourceLayoutDescription(
                Element("Frame", GpuResourceKind.UniformBuffer),
                Element("Albedo", GpuResourceKind.TextureReadOnly),
                Element("AlbedoSampler", GpuResourceKind.Sampler)));

            Assert.Equal(3, layout.ElementCount);
            Assert.Equal("Frame", layout.ElementAt(0).Name);
            Assert.Equal("Albedo", layout.ElementAt(1).Name);
            Assert.Equal("AlbedoSampler", layout.ElementAt(2).Name);

            // ELEMENT INDEX IS THE BINDING NUMBER, which is the whole of what declaration order is for here: the
            // index table is keyed on (set, binding, stage) with binding counted in exactly this order.
            Assert.Equal(GpuResourceKind.Sampler, layout.Elements[2].Kind);
            Assert.Equal(3, layout.Description.Elements.Length);
            Assert.Same(liveness, layout.Owner);
        }

        /// <summary>
        /// THE ARRAY IS COPIED, because <see cref="GpuResourceLayoutDescription"/> is a public struct holding a
        /// reference. A caller reusing its array to build a second layout would otherwise re-shape the first one
        /// after sets and pipelines had been built against it.
        /// </summary>
        [Fact]
        public void TheCallersArray_IsCopiedRatherThanHeld()
        {
            GpuResourceLayoutElement[] elements =
            [
                Element("Frame", GpuResourceKind.UniformBuffer),
                Element("Albedo", GpuResourceKind.TextureReadOnly),
            ];

            using var layout = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(), new GpuResourceLayoutDescription(elements));

            elements[1] = Element("Sampler", GpuResourceKind.Sampler);

            Assert.Equal(GpuResourceKind.TextureReadOnly, layout.ElementAt(1).Kind);
            Assert.Equal(GpuResourceKind.TextureReadOnly, layout.Description.Elements[1].Kind);
        }

        [Fact]
        public void AnEmptyLayout_IsLegalAndCarriesNoElements()
        {
            using var declared = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(), new GpuResourceLayoutDescription());
            using var missing = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(), default);

            Assert.Equal(0, declared.ElementCount);
            Assert.Equal(0, missing.ElementCount);
            Assert.Empty(missing.Description.Elements);
        }

        /// <summary>
        /// A PER-DRAW DYNAMIC OFFSET ON A TEXTURE OR A SAMPLER HAS NOWHERE TO GO. On Metal the offset is applied
        /// with <c>-setVertexBufferOffset:atIndex:</c> or its stage sibling, which exists only in the
        /// <c>[[buffer(n)]]</c> space, so declaring one on the other two spaces would be silently dropped at every
        /// bind. Refused at layout creation, which is the last moment the declaration is still in front of the
        /// caller.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.TextureReadOnly)]
        [InlineData(GpuResourceKind.TextureReadWrite)]
        [InlineData(GpuResourceKind.Sampler)]
        public void ADynamicOffsetOutsideTheBufferSpace_IsRefusedAtCreation(GpuResourceKind kind)
        {
            ArgumentException failed = Assert.Throws<ArgumentException>(() => new MetalResourceLayout(
                new FakeMetalDeviceLiveness(),
                new GpuResourceLayoutDescription(Element("Bad", kind, dynamic: true))));

            Assert.Contains("'Bad' at binding 0", failed.Message, StringComparison.Ordinal);
            Assert.Contains("setVertexBufferOffset", failed.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// AND THE REFUSAL IS NARROWER THAN THE VULKAN SIBLING'S, deliberately.
        /// <c>VulkanDescriptorPolicy.TypeFor</c> refuses a dynamic element that is not a UNIFORM buffer, because a
        /// storage descriptor there has no dynamic offset at all. Metal's <c>setBufferOffset:</c> works at any
        /// buffer index whatever the kind, so a dynamic structured buffer is expressible here and reproducing the
        /// wider refusal would be inheriting a constraint that is not this API's.
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.UniformBuffer)]
        [InlineData(GpuResourceKind.StructuredBufferReadOnly)]
        [InlineData(GpuResourceKind.StructuredBufferReadWrite)]
        public void ADynamicOffsetOnAnyBufferKind_IsAccepted(GpuResourceKind kind)
        {
            using var layout = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(),
                new GpuResourceLayoutDescription(Element("Ok", kind, dynamic: true)));

            Assert.True(layout.ElementAt(0).Dynamic);
        }

        /// <summary>The name is a LABEL on this backend and nothing joins through it (2.2b), so a blank one is not
        /// refused: it is not wrong, it is unread. It still has to reach a message, because "element 4" is
        /// unactionable in a seven-element material layout.</summary>
        [Fact]
        public void AnUnnamedElement_IsNotRefusedAndStillReachesAMessage()
        {
            using var layout = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(),
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));

            Assert.Equal(1, layout.ElementCount);

            ArgumentException failed = Assert.Throws<ArgumentException>(() => new MetalResourceLayout(
                new FakeMetalDeviceLiveness(),
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("", GpuResourceKind.Sampler, GpuShaderStages.Vertex,
                        dynamic: true))));

            Assert.Contains("<unnamed>", failed.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A LAYOUT IS PLAIN MANAGED DATA, so the wrong one is invisible without this: it carries the liveness
        /// token like every other resource, and a set or a pipeline built from another device's layout is refused
        /// by name rather than resolving positionally against an array that means something else.
        /// </summary>
        [Fact]
        public void ALayoutFromAnotherBackendOrAnotherDevice_IsRefusedByName()
        {
            var mine = new FakeMetalDeviceLiveness();
            var theirs = new FakeMetalDeviceLiveness();

            using var foreignDevice = new MetalResourceLayout(theirs, new GpuResourceLayoutDescription());
            using var foreignBackend = new NotAMetalLayout();

            Assert.Contains("no resource layout",
                Assert.Throws<ArgumentException>(
                    () => MetalResourceLayout.Require(null, mine, "a set")).Message,
                StringComparison.Ordinal);

            Assert.Contains("not created by the native Metal backend",
                Assert.Throws<ArgumentException>(
                    () => MetalResourceLayout.Require(foreignBackend, mine, "a set")).Message,
                StringComparison.Ordinal);

            Assert.Contains("DIFFERENT native Metal device",
                Assert.Throws<ArgumentException>(
                    () => MetalResourceLayout.Require(foreignDevice, mine, "a set")).Message,
                StringComparison.Ordinal);

            // The positive control, so the three refusals above are not passing because Require refuses
            // everything.
            using var ours = new MetalResourceLayout(mine, new GpuResourceLayoutDescription());
            Assert.Same(ours, MetalResourceLayout.Require(ours, mine, "a set"));
        }

        /// <summary>Disposal releases nothing, because there is nothing native to release. The flag exists so a
        /// use-after-dispose is a stated error rather than a silently working call.</summary>
        [Fact]
        public void Disposal_IsAFlagAndNotARelease()
        {
            var layout = new MetalResourceLayout(
                new FakeMetalDeviceLiveness(),
                new GpuResourceLayoutDescription(Element("Frame", GpuResourceKind.UniformBuffer)));

            Assert.False(layout.IsDisposed);
            layout.Dispose();
            layout.Dispose();

            Assert.True(layout.IsDisposed);
            Assert.Equal(1, layout.ElementCount);
        }

        /// <summary>
        /// THE BIND WINDOW HAS TO EXIST INSIDE THE BUFFER, and this is the half of a set's resolution that is
        /// arithmetic rather than a wrapper check, so it is asserted here rather than only on a device. Metal's
        /// own setters carry no length at all, so nothing downstream would ever report a window that does not
        /// exist: the shader would read whatever follows the buffer.
        /// </summary>
        [Theory]
        [InlineData(0u, 256u, 256u, true)]      // the whole buffer
        [InlineData(64u, 192u, 256u, true)]     // a window ending exactly at the end
        [InlineData(64u, 193u, 256u, false)]    // one byte past it
        [InlineData(0u, 0u, 256u, false)]       // a zero-length window binds nothing and is a caller mistake
        [InlineData(256u, 16u, 256u, false)]    // an offset at the end
        [InlineData(4294967295u, 16u, 256u, false)]
        public void TheBindWindow_HasToBeAWindowInsideTheBuffer(uint offset, uint range, uint size, bool allowed)
        {
            if (allowed)
            {
                MetalResourceSet.RequireWindowInBuffer(offset, range, size, "a row");
                return;
            }

            ArgumentException failed = Assert.Throws<ArgumentException>(
                () => MetalResourceSet.RequireWindowInBuffer(offset, range, size, "a row"));
            Assert.Contains("a row binds", failed.Message, StringComparison.Ordinal);
        }

        static GpuResourceLayoutElement Element(string name, GpuResourceKind kind, bool dynamic = false)
            => new(name, kind, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic);

        sealed class NotAMetalLayout : IGpuResourceLayout
        {
            public void Dispose() { }
        }
    }
}
