using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROW 10'S DEVICE-FREE HALF (https://github.com/APKiwiOrg/KhaozEngine/issues/576): everything a resource
    /// layout decides, plus the pieces of a resource set's resolution that are arithmetic or a declaration check
    /// rather than a check against a wrapper only a device can make, plus what disposal means for both types.
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
        /// AND THIS IS THE BACKEND THAT MATCHES THE SEAM, WITH BOTH SIBLINGS NARROWER THAN THE CONTRACT.
        /// <see cref="GpuResourceLayoutElement.Dynamic"/> documents a dynamic-offset "uniform/structured buffer",
        /// and Metal's <c>setBufferOffset:</c> works at any buffer index whatever the kind, so every kind the seam
        /// names is honoured here. <c>VulkanDescriptorPolicy.TypeFor</c> refuses a dynamic element that is not a
        /// UNIFORM buffer, because a storage descriptor there has no dynamic offset at all, and
        /// <c>D3D11ResourceLayout</c> refuses the same combination, because a structured buffer binds through a
        /// view created once over the whole buffer with no per-bind window. So a consumer using one is Metal-only
        /// today, which is https://github.com/APKiwiOrg/KhaozEngine/issues/597.
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

        /// <summary>
        /// DISPOSAL RELEASES NOTHING AND <c>Require</c> IS WHAT MAKES IT MEAN ANYTHING. There is nothing native
        /// behind a layout, so every member still answers afterwards and a set built on a disposed layout would
        /// resolve perfectly well against an array its owner has already let go of. The flag is only a stated
        /// error where something reads it, so both entry points the row hands out refuse on it: the shared
        /// <c>Require</c> and, through it, set creation.
        /// </summary>
        [Fact]
        public void ADisposedLayout_IsRefusedByRequireAndBySetCreation()
        {
            var liveness = new FakeMetalDeviceLiveness();
            var layout = new MetalResourceLayout(
                liveness, new GpuResourceLayoutDescription(Element("Frame", GpuResourceKind.UniformBuffer)));

            Assert.False(layout.IsDisposed);
            layout.Dispose();
            layout.Dispose();

            Assert.True(layout.IsDisposed);

            // STILL PLAIN DATA. The array is the layout, so nothing about it stops answering: that is precisely
            // why the two refusals below have to be explicit.
            Assert.Equal(1, layout.ElementCount);

            Assert.Contains("already disposed",
                Assert.Throws<ObjectDisposedException>(
                    () => MetalResourceLayout.Require(layout, liveness, "a set")).Message,
                StringComparison.Ordinal);

            Assert.Throws<ObjectDisposedException>(() => new MetalResourceSet(
                liveness, new GpuResourceSetDescription(layout)));
        }

        /// <summary>
        /// AND A DISPOSED SET IS REFUSED WHERE ROW 13 WOULD BIND IT, for the same reason and with the same
        /// mechanism: a set owns no Objective-C object, so binding one the caller has disposed would simply work
        /// and would name resources that caller considers unbound.
        /// </summary>
        [Fact]
        public void ADisposedSet_IsRefusedByRequire()
        {
            var liveness = new FakeMetalDeviceLiveness();
            using var layout = new MetalResourceLayout(liveness, new GpuResourceLayoutDescription());
            var set = new MetalResourceSet(liveness, new GpuResourceSetDescription(layout));

            // The positive control first, so the refusal below is about disposal rather than about the fixture.
            Assert.Same(set, MetalResourceSet.Require(set, liveness, "a bind"));

            set.Dispose();

            Assert.Contains("already disposed",
                Assert.Throws<ObjectDisposedException>(
                    () => MetalResourceSet.Require(set, liveness, "a bind")).Message,
                StringComparison.Ordinal);
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

        /// <summary>
        /// A TEXTURE HAS TO HAVE BEEN CREATED FOR THE DIRECTION THE ELEMENT DECLARES, which is
        /// <c>VulkanResourceSet.ResolveImage</c>'s refusal reached by this backend's own route. There are no views
        /// here to be missing (M-M10), so the thing that stands in for one is the <c>MTLTextureUsage</c> the
        /// texture was created with: no <c>Sampled</c> means no <c>ShaderRead</c> and no <c>Storage</c> means no
        /// <c>ShaderWrite</c>, and binding a texture into a table without the bit it is read or written through is
        /// a validation abort under the debug layer and undefined behaviour without it.
        /// <para>
        /// <c>GenerateMipmaps</c> ADMITS A READ-ONLY BINDING and that is the sibling's matrix rather than a hole.
        /// Vulkan creates the sampled view for it; on Metal it maps to no usage bit, so the texture is created
        /// <c>MTLTextureUsage.Unknown</c>, which Metal reads as any usage rather than none.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuResourceKind.TextureReadOnly, GpuTextureUsage.Sampled, true)]
        [InlineData(GpuResourceKind.TextureReadOnly, GpuTextureUsage.Sampled | GpuTextureUsage.Storage, true)]
        [InlineData(GpuResourceKind.TextureReadOnly, GpuTextureUsage.GenerateMipmaps, true)]
        [InlineData(GpuResourceKind.TextureReadOnly, GpuTextureUsage.Storage, false)]
        [InlineData(GpuResourceKind.TextureReadOnly, GpuTextureUsage.RenderTarget, false)]
        [InlineData(GpuResourceKind.TextureReadWrite, GpuTextureUsage.Storage, true)]
        [InlineData(GpuResourceKind.TextureReadWrite, GpuTextureUsage.Sampled | GpuTextureUsage.Storage, true)]
        [InlineData(GpuResourceKind.TextureReadWrite, GpuTextureUsage.Sampled, false)]
        [InlineData(GpuResourceKind.TextureReadWrite, GpuTextureUsage.None, false)]
        public void ATextureBoundForADirectionItWasNotCreatedFor_IsRefused(
            GpuResourceKind kind, GpuTextureUsage usage, bool allowed)
        {
            if (allowed)
            {
                MetalResourceSet.RequireTextureUsage(kind, usage, "a row");
                return;
            }

            ArgumentException failed = Assert.Throws<ArgumentException>(
                () => MetalResourceSet.RequireTextureUsage(kind, usage, "a row"));

            Assert.Contains("a row declares a " + kind, failed.Message, StringComparison.Ordinal);
            Assert.Contains(
                kind == GpuResourceKind.TextureReadWrite
                    ? "Add GpuTextureUsage.Storage"
                    : "Add GpuTextureUsage.Sampled",
                failed.Message, StringComparison.Ordinal);
        }

        static GpuResourceLayoutElement Element(string name, GpuResourceKind kind, bool dynamic = false)
            => new(name, kind, GpuShaderStages.Vertex | GpuShaderStages.Fragment, dynamic);

        sealed class NotAMetalLayout : IGpuResourceLayout
        {
            public void Dispose() { }
        }
    }
}
