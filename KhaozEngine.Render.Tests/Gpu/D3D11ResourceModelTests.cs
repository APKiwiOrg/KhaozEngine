using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native Direct3D 11 backend's resource model, in the half that has no device in it: the eager-view
    /// POLICY of decision X1, the resolution a resource set does at creation, the constants arithmetic a
    /// constant-buffer window goes through, the input layout's semantic numbering, and the liveness latch of
    /// decision X3.
    /// <para>
    /// All of it is engine types by construction, so every test here is an ordinary <c>[Fact]</c> that runs on
    /// macOS and Linux as well as Windows. The Vortice-touching half (creating the view objects, the state objects
    /// and the input layout) is verified on the Windows leg by the goldens, and separating the two is the point:
    /// the RULE is what a wrong answer breaks silently, and the rule is testable everywhere.
    /// </para>
    /// </summary>
    public sealed class D3D11ResourceModelTests
    {
        // ---- the eager-view policy (X1) ------------------------------------------------------------------

        /// <summary>A sampled texture gets one view: the full-chain shader resource view. Nothing else follows
        /// from being sampled, and a render target view it never asked for would be a second object to keep
        /// alive.</summary>
        [Fact]
        public void SampledTexture_GetsOnlyAShaderResourceView()
        {
            D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(GpuTextureUsage.Sampled);

            Assert.True(plan.ShaderResource);
            Assert.False(plan.RenderTarget);
            Assert.False(plan.DepthStencil);
            Assert.False(plan.UnorderedAccess);
            Assert.Equal(1, plan.ViewCount);
            Assert.Equal(D3D11BindUsage.ShaderResource, plan.Bind);
        }

        /// <summary>The four usage bits each earn their own view, and the combinations add up rather than
        /// override.</summary>
        [Theory]
        [InlineData(GpuTextureUsage.RenderTarget, false, true, false, false)]
        [InlineData(GpuTextureUsage.DepthStencil, false, false, true, false)]
        [InlineData(GpuTextureUsage.Storage, false, false, false, true)]
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget, true, true, false, false)]
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.DepthStencil, true, false, true, false)]
        [InlineData(GpuTextureUsage.Sampled | GpuTextureUsage.Storage, true, false, false, true)]
        public void TextureViews_FollowTheDeclaredUsageBits(GpuTextureUsage usage,
            bool srv, bool rtv, bool dsv, bool uav)
        {
            D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(usage);

            Assert.Equal(srv, plan.ShaderResource);
            Assert.Equal(rtv, plan.RenderTarget);
            Assert.Equal(dsv, plan.DepthStencil);
            Assert.Equal(uav, plan.UnorderedAccess);
        }

        /// <summary>
        /// Mip generation earns a shader resource view, and the two halves it is easy to conflate are pinned
        /// apart here. <c>GenerateMips</c> is defined as reading and writing through a shader resource view, so a
        /// texture that asked only for mip generation would otherwise have no view to generate through and would
        /// fail at the point of use rather than at creation. It carries the render target BIND FLAG as well,
        /// which Direct3D 11 requires on the resource, and NO render target view: decision X1 hangs the eager
        /// view on <see cref="GpuTextureUsage.RenderTarget"/> alone, so a view created here would be an object
        /// nothing ever binds.
        /// </summary>
        [Fact]
        public void GenerateMipmaps_TakesTheRenderTargetFlagButNotTheRenderTargetView()
        {
            D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(GpuTextureUsage.GenerateMipmaps);

            Assert.True(plan.ShaderResource);
            Assert.False(plan.RenderTarget);
            Assert.Equal(1, plan.ViewCount);
            Assert.Equal(D3D11BindUsage.ShaderResource | D3D11BindUsage.RenderTarget, plan.Bind);
        }

        /// <summary>
        /// THE BOUND OF FOUR, over every reachable combination rather than the handful above. It holds because the
        /// seam has no way to ask for a fifth view: no texture-view type, no mip or layer parameter on
        /// <c>CreateFramebuffer</c>, a resolve that names two whole textures, and no per-face cubemap rendering.
        /// </summary>
        [Fact]
        public void NoTexture_EverGetsMoreThanFourViews()
        {
            foreach (GpuTextureUsage usage in NonStagingTextureUsages())
            {
                D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(usage);
                Assert.True(plan.ViewCount <= 4, $"{usage} asked for {plan.ViewCount} views.");
            }
        }

        /// <summary>Every view in the plan has the bind flag that makes it legal, over every combination. A view
        /// without its flag is a creation failure on the first real device, which is a Windows-only symptom for a
        /// mistake that is decidable here.</summary>
        [Fact]
        public void EveryPlannedTextureView_HasItsBindFlag()
        {
            foreach (GpuTextureUsage usage in NonStagingTextureUsages())
            {
                D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(usage);
                if (plan.ShaderResource) Assert.True((plan.Bind & D3D11BindUsage.ShaderResource) != 0, $"{usage}");
                if (plan.RenderTarget) Assert.True((plan.Bind & D3D11BindUsage.RenderTarget) != 0, $"{usage}");
                if (plan.DepthStencil) Assert.True((plan.Bind & D3D11BindUsage.DepthStencil) != 0, $"{usage}");
                if (plan.UnorderedAccess) Assert.True((plan.Bind & D3D11BindUsage.UnorderedAccess) != 0, $"{usage}");
            }
        }

        /// <summary>A staging texture is CPU-mapped and binds nowhere, so it gets no views and no bind flags.</summary>
        [Fact]
        public void StagingTexture_GetsNoViewsAndNoBindFlags()
        {
            D3D11TextureViewPlan plan = D3D11ViewPolicy.ForTexture(GpuTextureUsage.Staging);

            Assert.True(plan.Staging);
            Assert.Equal(0, plan.ViewCount);
            Assert.Equal(D3D11BindUsage.None, plan.Bind);
        }

        /// <summary>Staging combined with anything else is rejected rather than silently reduced. Direct3D 11
        /// refuses a staging resource that carries bind flags, so there is no meaning to honour, and dropping the
        /// other bits quietly would hand back a texture that cannot do what was asked.</summary>
        [Fact]
        public void StagingCombinedWithAnythingElse_IsRejected()
        {
            Assert.Throws<ArgumentException>(
                () => D3D11ViewPolicy.ForTexture(GpuTextureUsage.Staging | GpuTextureUsage.Sampled));
            Assert.Throws<ArgumentException>(
                () => D3D11ViewPolicy.ForBuffer(GpuBufferUsage.Staging | GpuBufferUsage.VertexBuffer));
        }

        // ---- structured buffers stay RAW (C2) ------------------------------------------------------------

        /// <summary>
        /// DECISION C2. Both structured kinds get a full-range RAW byte-address view over a plain default-usage
        /// buffer, because SPIRV-Cross emits a GLSL storage block as a <c>ByteAddressBuffer</c> and a
        /// stride-shaped structured view would not be what the compiled shader reads. A read-write block is still
        /// readable, so it takes both views.
        /// </summary>
        [Fact]
        public void StructuredBuffers_TakeRawViews()
        {
            D3D11BufferViewPlan readOnly = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadOnly);
            Assert.True(readOnly.RawViews);
            Assert.True(readOnly.ShaderResource);
            Assert.False(readOnly.UnorderedAccess);
            Assert.Equal(D3D11BindUsage.ShaderResource, readOnly.Bind);

            D3D11BufferViewPlan readWrite = D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadWrite);
            Assert.True(readWrite.RawViews);
            Assert.True(readWrite.ShaderResource);
            Assert.True(readWrite.UnorderedAccess);
            Assert.Equal(D3D11BindUsage.ShaderResource | D3D11BindUsage.UnorderedAccess, readWrite.Bind);

            // Neither is dynamic and neither is staging: the storage path is DEFAULT usage written through the
            // command list, which is what keeps the stride advisory rather than load-bearing.
            Assert.False(readWrite.Dynamic);
            Assert.False(readWrite.Staging);
        }

        /// <summary>The ordinary buffer kinds take their bind flag and NO view. A uniform buffer's window is
        /// carried in the bind call rather than in a view object, which is why a constant buffer never earns
        /// one.</summary>
        [Fact]
        public void PlainBuffers_TakeNoViews()
        {
            NoViews(GpuBufferUsage.VertexBuffer, D3D11BindUsage.VertexBuffer);
            NoViews(GpuBufferUsage.IndexBuffer, D3D11BindUsage.IndexBuffer);
            NoViews(GpuBufferUsage.UniformBuffer, D3D11BindUsage.ConstantBuffer);

            static void NoViews(GpuBufferUsage usage, D3D11BindUsage expected)
            {
                D3D11BufferViewPlan plan = D3D11ViewPolicy.ForBuffer(usage);

                Assert.Equal(expected, plan.Bind);
                Assert.False(plan.ShaderResource);
                Assert.False(plan.UnorderedAccess);
                Assert.False(plan.RawViews);
            }
        }

        // ---- a resource set resolves at CREATION ---------------------------------------------------------

        /// <summary>
        /// A <see cref="GpuBufferRange"/> is unpacked into a buffer plus an offset plus a size AT SET CREATION.
        /// The whole point of the decision is that draw time is left with an array to walk, so the resolution is
        /// asserted on the stored binding rather than on anything a bind call does.
        /// </summary>
        [Fact]
        public void ABufferRange_ResolvesWhenTheSetIsCreated()
        {
            var buffer = new FakeBuffer(1024);
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Vp", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex,
                    dynamic: true)));

            using var set = new D3D11ResourceSet(new GpuResourceSetDescription(
                layout, new GpuBufferRange(buffer, 256, 128)));

            D3D11BoundResource binding = set.Bindings[0];
            Assert.Same(buffer, binding.Buffer);
            Assert.Equal(256u, binding.OffsetBytes);
            Assert.Equal(128u, binding.SizeBytes);
            Assert.False(binding.IsFullRange);
            Assert.True(binding.Dynamic);
            Assert.Equal(new D3D11RegisterSlot(D3D11RegisterFile.ConstantBuffer, 0), binding.Slot);
        }

        /// <summary>A bare buffer resolves to the whole buffer, so the two forms travel one path from here on
        /// rather than two the bind flush would have to tell apart.</summary>
        [Fact]
        public void ABareBuffer_ResolvesToTheWholeBuffer()
        {
            var buffer = new FakeBuffer(512);
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));

            using var set = new D3D11ResourceSet(new GpuResourceSetDescription(layout, buffer));

            Assert.Equal(0u, set.Bindings[0].OffsetBytes);
            Assert.Equal(512u, set.Bindings[0].SizeBytes);
            Assert.True(set.Bindings[0].IsFullRange);
        }

        /// <summary>Every binding carries the register its layout assigned, so the set knows where each resource
        /// goes without consulting the layout again at draw time.</summary>
        [Fact]
        public void EveryBinding_CarriesItsLayoutRegister()
        {
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));

            using var set = new D3D11ResourceSet(new GpuResourceSetDescription(
                layout,
                new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8G8B8A8UNorm),
                new FakeSampler(),
                new FakeBuffer(64)));

            Assert.Equal("t0", set.Bindings[0].Slot.ToString());
            Assert.Equal("s0", set.Bindings[1].Slot.ToString());
            Assert.Equal("b0", set.Bindings[2].Slot.ToString());
        }

        /// <summary>A resource count that does not match the layout shifts every register after it, so it is
        /// rejected at creation instead of rendering the wrong resources.</summary>
        [Fact]
        public void AResourceCountThatDoesNotMatchTheLayout_IsRejected()
        {
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));

            Assert.Throws<ArgumentException>(() => new D3D11ResourceSet(
                new GpuResourceSetDescription(layout, new FakeTexture(4, 4, 1, 1, GpuPixelFormat.R8UNorm))));
        }

        /// <summary>A resource of the wrong shape would take the wrong register file and bind nothing the shader
        /// reads, which is invisible at runtime, so the kinds are checked at creation.</summary>
        [Fact]
        public void AResourceOfTheWrongKind_IsRejected()
        {
            using var textureLayout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)));
            using var bufferLayout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));

            Assert.Throws<ArgumentException>(() => new D3D11ResourceSet(
                new GpuResourceSetDescription(textureLayout, new FakeSampler())));
            Assert.Throws<ArgumentException>(() => new D3D11ResourceSet(
                new GpuResourceSetDescription(bufferLayout, new FakeSampler())));
        }

        /// <summary>A window past the end of its buffer is sayable only because the resolution happens here. At
        /// draw time it would be a truncated read with no error anywhere.</summary>
        [Fact]
        public void ABufferRangePastTheEnd_IsRejected()
        {
            var buffer = new FakeBuffer(256);
            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));

            Assert.Throws<ArgumentException>(() => new D3D11ResourceSet(
                new GpuResourceSetDescription(layout, new GpuBufferRange(buffer, 192, 128))));
        }

        /// <summary>A layout from another backend carries another backend's numbering, so it is refused rather
        /// than assumed compatible.</summary>
        [Fact]
        public void AForeignLayout_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => new D3D11ResourceSet(
                new GpuResourceSetDescription(new FakeResourceLayout(), new FakeBuffer(16))));
        }

        // ---- the constants arithmetic --------------------------------------------------------------------

        /// <summary>
        /// Direct3D 11 counts constant-buffer windows in 16-byte constants, and a window shorter than 256 bytes is
        /// rounded UP to that minimum rather than rejected. Rounding up can name constants past the caller's
        /// window, which is safe because the shader reads only the fields its own block declares.
        /// </summary>
        [Theory]
        [InlineData(0u, 0u)]
        [InlineData(256u, 16u)]
        [InlineData(4096u, 256u)]
        public void AWindowStart_IsCountedInConstants(uint offsetBytes, uint expected)
            => Assert.Equal(expected, D3D11ConstantRange.FirstConstant(offsetBytes));

        /// <summary>
        /// The exact counts, including the windows a 16-byte round-up got wrong. A count is legal only as a
        /// multiple of 16 constants, so the round-up is to 256 bytes: 1008 bytes is 64 constants and not 63, 1040
        /// and 1120 are both 80, 8256 is 528. Everything at or below 768 here was ALREADY legal and is pinned to
        /// prove the fix moved no count Direct3D 11 was accepting before.
        /// </summary>
        [Theory]
        [InlineData(16u, 16u)]     // below the minimum, rounded up
        [InlineData(255u, 16u)]
        [InlineData(256u, 16u)]
        [InlineData(512u, 32u)]
        [InlineData(768u, 48u)]    // WaterRenderer.SlotBytes
        [InlineData(1008u, 64u)]   // ModelRenderer.UboBytes. Was 63, which dropped the whole model pass
        [InlineData(1040u, 80u)]   // PixelPostProcess.PaletteBufferBytes. Was 65
        [InlineData(1120u, 80u)]   // the splat combined UBO. Was 70, which dropped splat terrain
        [InlineData(8256u, 528u)]  // (1 + 128) * 64 unaligned. Was 516
        [InlineData(8448u, 528u)]  // ShadowMapRenderer.SkinnedDepthSlotBytes
        [InlineData(9472u, 592u)]  // ModelRenderer.SkinnedMainSlotBytes
        public void AWindowSize_IsCountedInConstantsWithAMinimum(uint sizeBytes, uint expected)
            => Assert.Equal(expected, D3D11ConstantRange.ConstantCount(sizeBytes));

        /// <summary>
        /// THE RULE AS AN INVARIANT, not a table. <c>*SetConstantBuffers1</c> wants a count that is a multiple of
        /// 16 constants in [0..4096], and it returns void, so an out-of-rule count is not an error: the runtime
        /// drops the entire call, the slot stays empty after the replay's <c>ClearState</c>, and the shader reads
        /// zeros with nothing reported.
        /// </summary>
        [Theory]
        [InlineData(16u)]
        [InlineData(255u)]
        [InlineData(256u)]
        [InlineData(512u)]
        [InlineData(1008u)]
        [InlineData(1040u)]
        [InlineData(1120u)]
        [InlineData(8256u)]
        [InlineData(9472u)]
        public void AWindowSize_YieldsACountDirect3D11WillAccept(uint sizeBytes)
            => AssertBindableConstantCount("window", sizeBytes);

        /// <summary>The same invariant swept across every size a uniform buffer can have (16-byte multiples, the
        /// creation rule) up to 12288 bytes, comfortably past the largest window the engine binds. A loop rather
        /// than a random sample, so a red run names the same size every time it runs.</summary>
        [Fact]
        public void EveryLegalWindowSize_YieldsACountDirect3D11WillAccept()
        {
            for (uint sizeBytes = 16; sizeBytes <= 12288; sizeBytes += 16)
                AssertBindableConstantCount("window", sizeBytes);
        }

        /// <summary>
        /// EVERY shipped uniform window, against the same rule. This generalises the 14.22.0 water lesson (see
        /// <c>UboLayoutTests.WaterUbo_BoundRangeAndStride_SatisfyTheD3D11ConstantCountRule</c>, which pinned the
        /// one payload that had already broken) to the whole renderer surface, because the mechanism was never
        /// water-specific: ANY bound size falling strictly between two multiples of 256 is dropped silently, and
        /// three shipped windows did exactly that.
        /// <para>
        /// Each size is referenced by its own constant wherever one is reachable. The three private literals are
        /// hardcoded against the line that owns them, so moving one fails here rather than on a Windows runner.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryShippedUniformWindow_BindsACountDirect3D11WillAccept()
        {
            (string Window, uint Bytes)[] windows =
            {
                ("ModelRenderer frame UBO", ModelRenderer.UboBytes),
                ("ModelRenderer splat combined UBO", ModelRenderer.UboBytes + SplatParamsData.SizeInBytes),
                ("ModelRenderer skinned main slot", ModelRenderer.SkinnedMainSlotBytes),
                ("ShadowMapRenderer skinned depth slot", ShadowMapRenderer.SkinnedDepthSlotBytes),
                ("ShadowMapRenderer cascade slot", 256u),          // private const, ShadowMapRenderer.cs:41
                ("WaterRenderer plane slot", WaterRenderer.SlotBytes),
                ("PixelPostProcess palette", PixelPostProcess.PaletteBufferBytes),
                ("PixelPostProcess blur", PixelPostProcess.BlurBufferBytes),
                ("PixelPostProcess edge", PixelPostProcess.EdgeBufferBytes),
                ("PixelPostProcess final", PixelPostProcess.FinalBufferBytes),
                ("PixelPostProcess fxaa", PixelPostProcess.FxaaBufferBytes),
                ("PixelPostProcess bright", PixelPostProcess.BrightBufferBytes),
                ("PixelPostProcess composite", PixelPostProcess.CompositeBufferBytes),
                ("PixelPostProcess tone", PixelPostProcess.ToneBufferBytes),
                ("PixelPostProcess apply", PixelPostProcess.ApplyBufferBytes),
                ("DistortionRenderer frame UBO", DistortionRenderer.FrameBufferBytes),
                ("SkyRenderer UBO", SkyRenderer.UboBytes),
                ("StarfieldRenderer UBO", StarfieldRenderer.UboBytes),
                ("OceanFftProducer UBO", OceanFftProducer.UboBytes),
                ("SpriteBatch view-proj payload", 64u),            // private const, SpriteBatch.cs:157
                ("OverlayMeshRenderer payload", 128u),             // private const, OverlayMeshRenderer.cs:35
                ("TexturedBillboardRenderer UBO", 64u),            // literal, TexturedBillboardRenderer.cs:36
                ("TrailRenderer UBO", 64u),                        // literal, TrailRenderer.cs:52
                ("DepthLineRenderer UBO", 64u),                    // literal, DepthLineRenderer.cs:35
                ("OverlayRenderer UBO", 64u),                      // literal, OverlayRenderer.cs:45
                ("BeamRenderer UBO", 80u),                         // literal, BeamRenderer.cs:53
                ("GroundDecalRenderer frame UBO", 80u),            // literal, GroundDecalRenderer.cs:131
                ("ParticleRenderer frame UBO", 192u),              // literal, ParticleRenderer.cs:100
                ("TransitionRenderer solid fill UBO", 16u),        // literal, TransitionRenderer.cs:60
                ("TransitionRenderer crossfade params UBO", 16u),  // literal, TransitionRenderer.cs:61
            };

            foreach ((string window, uint bytes) in windows) AssertBindableConstantCount(window, bytes);
        }

        // The whole rule in one place: a multiple of 16 constants, no more than 4096 of them, and covering the
        // bytes the caller asked for. The message says what a failure LOOKS like, because it looks like nothing.
        static void AssertBindableConstantCount(string window, uint sizeBytes)
        {
            uint count = D3D11ConstantRange.ConstantCount(sizeBytes);

            Assert.True(count % 16 == 0,
                $"{window}: {sizeBytes} bytes binds {count} constants, which is not a multiple of 16. "
                + "*SetConstantBuffers1 returns void and DROPS a call with an out-of-rule count, so the slot "
                + "stays empty after ClearState and the shader reads zeros. Nothing is logged and nothing "
                + "throws: the pass just renders from zeros on Direct3D 11 while Metal and Vulkan stay perfect.");
            Assert.True(count <= 4096,
                $"{window}: {sizeBytes} bytes binds {count} constants, past the 4096 maximum. Same silent drop.");
            Assert.True(count * D3D11ConstantRange.ConstantSizeBytes >= sizeBytes,
                $"{window}: {sizeBytes} bytes binds only {count} constants, which truncates the caller's window.");
        }

        // ---- the input layout ----------------------------------------------------------------------------

        /// <summary>
        /// Every vertex element is a <c>TEXCOORD</c> and the INDEX carries all the meaning, counted across all
        /// buffer slots in order rather than restarting per slot. That is what the cross-compiled HLSL declares,
        /// because SPIRV-Cross emits each GLSL location as <c>TEXCOORD&lt;location&gt;</c>, and a per-slot restart
        /// would collide slot 1's first element with slot 0's.
        /// </summary>
        [Fact]
        public void SemanticIndices_RunContiguouslyFromZeroAcrossSlots()
        {
            var perVertex = new GpuVertexLayoutDescription(
                new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
                new GpuVertexElement("Uv", GpuVertexElementFormat.Float2));
            var perInstance = new GpuVertexLayoutDescription(64, 1, new[]
            {
                new GpuVertexElement("IModel0", GpuVertexElementFormat.Float4),
                new GpuVertexElement("IModel1", GpuVertexElementFormat.Float4),
            });

            D3D11InputElement[] elements = D3D11InputLayoutPlan.Build(
                new[] { perVertex, perInstance }, out uint[] strides);

            Assert.Equal(5, elements.Length);
            Assert.All(elements, e => Assert.Equal("TEXCOORD", e.SemanticName));
            Assert.Equal(new uint[] { 0, 1, 2, 3, 4 }, elements.Select(e => e.SemanticIndex));
            Assert.Equal(new uint[] { 0, 0, 0, 1, 1 }, elements.Select(e => e.Slot));
        }

        /// <summary>Offsets pack within each slot and restart at zero for the next one, which is the only reading
        /// the seam supports: it carries no per-element offset.</summary>
        [Fact]
        public void ElementOffsets_PackWithinTheirOwnSlot()
        {
            var slot0 = new GpuVertexLayoutDescription(
                new GpuVertexElement("A", GpuVertexElementFormat.Float3),
                new GpuVertexElement("B", GpuVertexElementFormat.Float2));
            var slot1 = new GpuVertexLayoutDescription(
                new GpuVertexElement("C", GpuVertexElementFormat.Float4));

            D3D11InputElement[] elements = D3D11InputLayoutPlan.Build(new[] { slot0, slot1 }, out uint[] strides);

            Assert.Equal(new uint[] { 0, 12, 0 }, elements.Select(e => e.OffsetBytes));
            Assert.Equal(new uint[] { 20, 16 }, strides);
        }

        /// <summary>A declared stride wins over the computed one, which is how an interleaved buffer with padding
        /// keeps its real stride. Zero means compute it.</summary>
        [Fact]
        public void ADeclaredStride_WinsOverTheComputedOne()
        {
            var padded = new GpuVertexLayoutDescription(32, 0, new[]
            {
                new GpuVertexElement("A", GpuVertexElementFormat.Float3),
            });

            D3D11InputLayoutPlan.Build(new[] { padded }, out uint[] strides);

            Assert.Equal(new uint[] { 32 }, strides);
        }

        /// <summary>The per-instance step rate rides on every element of its slot, and a per-vertex slot stays at
        /// zero. The model pass's instance buffer is the shipped consumer.</summary>
        [Fact]
        public void ThePerInstanceStepRate_RidesOnItsSlotsElements()
        {
            var perVertex = new GpuVertexLayoutDescription(
                new GpuVertexElement("A", GpuVertexElementFormat.Float3));
            var perInstance = new GpuVertexLayoutDescription(16, 1, new[]
            {
                new GpuVertexElement("I", GpuVertexElementFormat.Float4),
            });

            D3D11InputElement[] elements = D3D11InputLayoutPlan.Build(
                new[] { perVertex, perInstance }, out _);

            Assert.False(elements[0].PerInstance);
            Assert.True(elements[1].PerInstance);
            Assert.Equal(1u, elements[1].InstanceStepRate);
        }

        /// <summary>A fullscreen pass declares no vertex layouts at all and gets no input layout, which is why the
        /// pipeline leaves its input layout null rather than creating an empty one.</summary>
        [Fact]
        public void NoVertexLayouts_ProduceNoInputElements()
        {
            Assert.Empty(D3D11InputLayoutPlan.Build(null, out uint[] none));
            Assert.Empty(none);
            Assert.Empty(D3D11InputLayoutPlan.Build(Array.Empty<GpuVertexLayoutDescription>(), out _));
        }

        // ---- the liveness latch (X3) ---------------------------------------------------------------------

        /// <summary>
        /// The latch starts alive, flips once, and never flips back. A device that has been destroyed does not
        /// come back, and an un-kill would turn a stale wrapper's disposal into a call against freed memory.
        /// </summary>
        [Fact]
        public void TheLivenessLatch_FlipsOnceAndStaysFlipped()
        {
            var liveness = new DeviceLiveness();

            Assert.True(liveness.IsAlive);
            Assert.False(liveness.IsDead);

            liveness.MarkDead();
            liveness.MarkDead();   // idempotent: teardown can be reached more than once

            Assert.True(liveness.IsDead);
            Assert.False(liveness.IsAlive);
        }

        /// <summary>
        /// The read surface is exactly two properties and NEEDS NO DEVICE, which is all this pins: the latch is
        /// constructible and both properties answer with nothing native in the process, on either side of the
        /// flip. That is why the latch is its own type rather than a flag on the device, since reaching it
        /// through the device would make every wrapper hold one.
        /// <para>
        /// The fence half of decision X3 (a fence polled after device death answering signaled, and a drain
        /// becoming a no-op) reads this same latch through <c>IDeviceLiveness</c>, which this type now
        /// implements, and is asserted against it in <c>D3D11FenceLifecycleTests</c> rather than here.
        /// </para>
        /// </summary>
        [Fact]
        public void TheLivenessLatch_IsReadableWithoutADevice()
        {
            var liveness = new DeviceLiveness();
            bool aliveBeforeDeath = liveness.IsAlive;
            bool deadBeforeDeath = liveness.IsDead;
            Assert.True(aliveBeforeDeath);
            Assert.False(deadBeforeDeath);

            liveness.MarkDead();
            bool aliveAfterDeath = liveness.IsAlive;
            bool deadAfterDeath = liveness.IsDead;
            Assert.False(aliveAfterDeath);
            Assert.True(deadAfterDeath);
        }

        // ---- the pipeline-state seam ---------------------------------------------------------------------

        /// <summary>
        /// THE PIPELINE HANDLE ANSWERS <c>ID3D11PipelineState</c>, which is the wiring between the state objects
        /// this row builds and the redundancy caches the replay row drives. Without it the emitter refuses every
        /// real pipeline by name, since a pipeline that cannot say what it is made of would rebind everything on
        /// every draw.
        /// <para>
        /// STRUCTURAL RATHER THAN BEHAVIOURAL, and deliberately so. <c>D3D11GraphicsPipeline</c> takes an
        /// <c>ID3D11Device</c> and builds four state objects in its constructor, so there is no way to make one
        /// off Windows and no way to read what it returns without a device. The behaviour on the far side of this
        /// relationship is already pinned device-free in <c>D3D11StateCacheTests</c> through a fake that
        /// implements the same interface, so what is left to check here is that the SHIPPED pipeline is the shape
        /// those tests assume. The values themselves land on the Windows leg with device creation.
        /// </para>
        /// <para>
        /// Reading the interface MAP rather than the type's own properties is not incidental: reading a property
        /// type on this class resolves an <c>ID3D11VertexShader</c> and loads the Direct3D interop, which would
        /// turn the two load-path assertions below red in whichever test happened to run afterwards. Every member
        /// of the map is a BCL type, so it stays inside the boundary.
        /// </para>
        /// </summary>
        [Fact]
        public void TheGraphicsPipeline_ImplementsThePipelineStateSeam()
        {
            Assert.True(typeof(ID3D11PipelineState).IsAssignableFrom(typeof(D3D11GraphicsPipeline)),
                "D3D11GraphicsPipeline no longer implements ID3D11PipelineState, so the redundancy caches cannot "
                + "read it and the emitter refuses every pipeline the device creates.");

            InterfaceMapping map = typeof(D3D11GraphicsPipeline)
                .GetInterfaceMap(typeof(ID3D11PipelineState));

            Assert.Equal(10, map.InterfaceMethods.Length);
            Assert.All(map.TargetMethods, m => Assert.Equal(typeof(D3D11GraphicsPipeline), m.DeclaringType));

            // The six object slots the cache compares by reference identity, plus the four VALUES: the topology,
            // the blend factor and the stencil reference that ride the two calls carrying an argument beside
            // their state object (issue #454), and the per-slot vertex strides a bind of a stream needs. Not one
            // of the ten is a Direct3D type, which is what keeps this seam and its tests device-free.
            Assert.Equal(6, map.InterfaceMethods.Count(m => m.ReturnType == typeof(object)));
            Assert.Equal(2, map.InterfaceMethods.Count(m => m.ReturnType == typeof(uint)));
            Assert.Equal(1, map.InterfaceMethods.Count(m => m.ReturnType == typeof(System.Numerics.Vector4)));
            Assert.Equal(1, map.InterfaceMethods.Count(m => m.ReturnType == typeof(uint[])));
        }

        // ---- the platform boundary -----------------------------------------------------------------------

        /// <summary>
        /// MERELY LOADING EVERY TYPE IN THE BACKEND MUST NOT PULL IN THE INTEROP, which is a stronger and much
        /// less obvious statement than "no unguarded body calls Direct3D".
        /// <para>
        /// Loading a type makes the CLR compute its layout, and that resolves every VALUE-TYPE field, which loads
        /// the assembly declaring it. A reference field costs nothing, because a pointer needs no layout, so an
        /// <c>ID3D11Device</c> field is free while one <c>Format</c> or <c>PrimitiveTopology</c> field is not. And
        /// the suite already forces exactly this: the emitter-shape check calls <c>Assembly.GetTypes</c>, which
        /// loads every type in the package. So a single interop value-type field anywhere turns every load-path
        /// assertion in the run red at once, and the failure surfaces in whichever test happened to look
        /// afterwards rather than in the type that caused it.
        /// </para>
        /// <para>
        /// THE FIX IS ALWAYS THE SAME and costs nothing measurable: keep the engine value in the field (a
        /// <see cref="GpuPixelFormat"/>, a <see cref="GpuPrimitiveTopology"/>) and expose the Direct3D reading as
        /// a COMPUTED property. Both conversions are switch expressions on paths that run once per resource
        /// creation or once per pipeline bind. <c>D3D11Texture.DxgiFormat</c> and
        /// <c>D3D11GraphicsPipeline.Topology</c> are the two that already went that way, and their comments say
        /// why, because the shape reads like an oversight otherwise.
        /// </para>
        /// </summary>
        [Fact]
        public void OffWindows_LoadingEveryTypeInTheBackend_PullsInNoInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            // Forces CLASS_LOADED on every type in the package, which is the resolution step that a value-type
            // field would fail. Deliberately not GetFields: reading a FieldType resolves reference fields too,
            // which loads the interop on its own and would make this test its own counterexample.
            Type[] all = typeof(KhaozEngineD3D11).Assembly.GetTypes();
            Assert.NotEmpty(all);

            string[] loaded = InteropAssembliesLoaded();
            Assert.True(loaded.Length == 0,
                "Loading the backend's own types pulled in the Direct3D interop on a platform that has none: ["
                + string.Join(", ", loaded) + "]. Some type now holds a Vortice VALUE-TYPE field (an enum or a "
                + "struct), so computing its layout resolved the interop assembly. Store the engine value in the "
                + "field and compute the Direct3D reading in a property instead.");
        }

        /// <summary>
        /// THE CLAIM DECISION P1 RESTS ON, checked for everything this row added: exercising the whole device-free
        /// resource model must not put the Direct3D interop assembly into the process on a platform that has none.
        /// That is what lets these be plain facts rather than <c>[GpuFact]</c>s, and it holds only while the
        /// policy types stay free of Vortice and every body that names one stays behind the platform guard.
        /// </summary>
        [Fact]
        public void OffWindows_TheResourceModelPullsInNoDirect3DInterop()
        {
            if (KhaozEngineD3D11.IsPlatformSupported) return;   // on Windows it loads, by design

            foreach (GpuTextureUsage usage in NonStagingTextureUsages()) D3D11ViewPolicy.ForTexture(usage);
            D3D11ViewPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadWrite);

            using var layout = new D3D11ResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("U", GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex)));
            using var set = new D3D11ResourceSet(new GpuResourceSetDescription(
                layout, new FakeTexture(2, 2, 1, 1, GpuPixelFormat.R8UNorm), new FakeBuffer(64)));
            // Two slots so the accumulation loop actually runs, at the last VALID index rather than one past it.
            D3D11RegisterScheme.BaseFor(new[] { layout, layout }, 1);
            D3D11ConstantRange.ConstantCount(64);
            D3D11InputLayoutPlan.Build(new[]
            {
                new GpuVertexLayoutDescription(new GpuVertexElement("A", GpuVertexElementFormat.Float2)),
            }, out _);
            new DeviceLiveness().MarkDead();

            string[] loaded = InteropAssembliesLoaded();
            Assert.True(loaded.Length == 0,
                "The Direct3D interop was loaded on a platform that has none: [" + string.Join(", ", loaded) +
                "]. Something in the device-free resource model now names a Vortice type, so the JIT resolved it "
                + "while compiling a method that runs everywhere, or a type in the package grew a Vortice "
                + "value-type field.");
        }

        static string[] InteropAssembliesLoaded() => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? "")
            .Where(n => n.StartsWith("Vortice", StringComparison.Ordinal)
                || n.StartsWith("SharpGen", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // Every texture usage combination except the staging bit, which is only legal on its own.
        static GpuTextureUsage[] NonStagingTextureUsages()
        {
            GpuTextureUsage[] bits =
            {
                GpuTextureUsage.Sampled, GpuTextureUsage.RenderTarget, GpuTextureUsage.DepthStencil,
                GpuTextureUsage.Cubemap, GpuTextureUsage.GenerateMipmaps, GpuTextureUsage.Storage,
            };

            var all = new GpuTextureUsage[1 << bits.Length];
            for (int mask = 0; mask < all.Length; mask++)
            {
                GpuTextureUsage usage = GpuTextureUsage.None;
                for (int b = 0; b < bits.Length; b++)
                    if ((mask & (1 << b)) != 0) usage |= bits[b];
                all[mask] = usage;
            }
            return all;
        }
    }
}
