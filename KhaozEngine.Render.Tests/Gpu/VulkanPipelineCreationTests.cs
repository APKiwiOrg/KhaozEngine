using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT ROW 13's PIPELINE CREATION ACTUALLY ASKS THE DRIVER FOR, driven against the fake pipeline seam: the
    /// vertex input derived from the seam's own layouts, the <c>VkPipelineRenderingCreateInfo</c> derived from
    /// <see cref="GpuOutputDescription"/>, the dynamic state held to viewport and scissor, the SHARED
    /// <c>VkPipelineLayout</c> and the dynamic-uniform limit that comes with it, and the deferred destroy on
    /// disposal. Work-breakdown row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para>ALL DEVICE-FREE, like every other row in this phase. A <c>[GpuFact]</c> here would need a Vulkan
    /// loader, which is what the golden legs have and the developer machines do not, and everything asserted below
    /// is a decision this backend takes rather than something a driver answers.</para>
    /// </summary>
    public sealed class VulkanPipelineCreationTests
    {
        const string VertGlsl =
            "#version 450\nlayout(location=0) in vec3 P;\nvoid main(){gl_Position=vec4(P,1);}";
        const string FragGlsl = "#version 450\nlayout(location=0) out vec4 C;\nvoid main(){C=vec4(1);}";
        const string ComputeGlsl =
            "#version 450\nlayout(local_size_x=8, local_size_y=4, local_size_z=2) in;\nvoid main(){}";

        // ---- vertex input ----

        /// <summary>
        /// THE LOCATION COUNTS ACROSS ALL SLOTS AND THE OFFSET PACKS WITHIN ONE. A GLSL <c>location</c> is a
        /// single flat sequence over every vertex input the shader declares and knows nothing about which buffer
        /// an attribute arrives in, so slot 1's first element continues where slot 0's last one left off. Getting
        /// it wrong reads the instance stream's first attribute as the vertex buffer's second, which renders
        /// plausible garbage rather than throwing.
        /// </summary>
        [Fact]
        public void TheVertexInput_NumbersLocationsAcrossSlotsAndPacksOffsetsWithinThem()
        {
            var layouts = new List<GpuVertexLayoutDescription>
            {
                new(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                    new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2)),
                new(
                    new GpuVertexElement("InstanceColor", GpuVertexElementFormat.Float4)),
            };

            VulkanVertexBinding[] bindings = VulkanVertexInput.Build(
                layouts, out VulkanVertexAttribute[] attributes);

            Assert.Equal([0u, 1u], bindings.Select(b => b.Binding));
            Assert.Equal([0u, 1u, 2u], attributes.Select(a => a.Location));
            Assert.Equal([0u, 0u, 1u], attributes.Select(a => a.Binding));

            // Packed within the slot, and slot 1 restarts at zero because the offset is a position inside its own
            // buffer rather than inside the flat location sequence.
            Assert.Equal([0u, 12u, 0u], attributes.Select(a => a.Offset));
        }

        /// <summary>
        /// A DECLARED STRIDE WINS AND A ZERO STRIDE IS THE SUM OF THE SLOT'S ELEMENTS. The declared arm is how an
        /// interleaved buffer with padding survives, and the computed arm is what almost every shipped call site
        /// relies on, since the seam has no per-element offset to honour.
        /// </summary>
        [Fact]
        public void ADeclaredStrideWins_AndAZeroStrideIsTheSumOfItsElements()
        {
            var layouts = new List<GpuVertexLayoutDescription>
            {
                new(0, 0, [new GpuVertexElement("P", GpuVertexElementFormat.Float3)]),
                new(64, 0, [new GpuVertexElement("P", GpuVertexElementFormat.Float3)]),
            };

            VulkanVertexBinding[] bindings = VulkanVertexInput.Build(layouts, out _);

            Assert.Equal(12u, bindings[0].Stride);
            Assert.Equal(64u, bindings[1].Stride);
        }

        /// <summary>
        /// A STEP RATE OF 1 IS THE INSTANCE RATE AND ANYTHING ABOVE IT IS REFUSED BY NAME. Vulkan's core vertex
        /// input rate is two-valued with no divisor, and the divisor extension is not enabled here (V-N6), so
        /// flattening a rate of 2 to 1 would draw every instance from the same element and would do it silently.
        /// </summary>
        [Fact]
        public void APerInstanceLayout_IsAnInstanceRateBinding_AndAHigherStepRateIsRefused()
        {
            VulkanVertexBinding[] bindings = VulkanVertexInput.Build(
                [new GpuVertexLayoutDescription(0, 1, [new GpuVertexElement("I", GpuVertexElementFormat.Float4)])],
                out _);

            Assert.True(Assert.Single(bindings).PerInstance);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => VulkanVertexInput.Build(
                [new GpuVertexLayoutDescription(0, 2, [new GpuVertexElement("I", GpuVertexElementFormat.Float4)])],
                out _));

            Assert.Contains("divisor", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>A FULLSCREEN PASS DECLARES NO VERTEX LAYOUT AT ALL, which is a legal and common vertex input
        /// state rather than an error: eleven shipped post-processing programs are exactly this shape.</summary>
        [Fact]
        public void NoVertexLayouts_IsNoBindingsAndNoAttributes()
        {
            VulkanVertexBinding[] bindings = VulkanVertexInput.Build(null, out VulkanVertexAttribute[] attributes);

            Assert.Empty(bindings);
            Assert.Empty(attributes);
        }

        // ---- the pipeline itself ----

        /// <summary>
        /// THE DYNAMIC STATE IS EXACTLY VIEWPORT AND SCISSOR, which is row 13's spec in one assertion. Everything
        /// else a description carries is baked into the pipeline object, which is the incumbent's shape kept
        /// deliberately (7.1) and is also why the disk pipeline cache is worth having: baking everything means
        /// considerably more pipeline permutations to compile on a cold start.
        /// </summary>
        [Fact]
        public void TheDynamicState_IsExactlyViewportAndScissor()
            => Assert.Equal(
                [VulkanDynamicState.Viewport, VulkanDynamicState.Scissor],
                VulkanPipelineDynamicState.States.ToArray());

        /// <summary>
        /// THE RENDERING STATE COMES FROM <see cref="GpuOutputDescription"/> AND NOWHERE ELSE (V-A1): the colour
        /// format array, the depth format and the sample count, which under dynamic rendering is the whole of what
        /// a classic render pass would have carried and is why row 12 needed no pass cache.
        /// </summary>
        [Fact]
        public void ThePipelinesRenderingState_ComesFromTheOutputDescription()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            var outputs = new GpuOutputDescription(
                GpuPixelFormat.D32FloatS8UInt,
                GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float).WithSampleCount(4);

            try
            {
                CreateGraphics(fixture, owned, outputs: outputs,
                    blends: [GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.OverrideBlend]);

                VulkanGraphicsPipelineSpec spec = Assert.Single(fixture.PipelineApi.Graphics);

                Assert.Equal(
                    [GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float], spec.ColourFormats);
                Assert.Equal(GpuPixelFormat.D32FloatS8UInt, spec.DepthFormat);
                Assert.Equal(4u, spec.SampleCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A MATCHING COUNT IS CARRIED THROUGH IN ORDER, which is the happy path the two refusals below are
        /// measured against: a resolver that threw on everything would pass them both.
        /// </summary>
        [Fact]
        public void TheDeclaredBlendStates_ArriveInOrderWhenTheCountMatches()
        {
            GpuBlendAttachment[] matched = VulkanGraphicsPipelineSpec.ResolveBlends(
                [GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.Additive], colourCount: 2);

            Assert.Equal(2, matched.Length);
            Assert.True(matched[0].BlendEnabled);
            Assert.Equal(GpuBlendFactor.InverseSourceAlpha, matched[0].DestinationColorFactor);
            Assert.Equal(GpuBlendFactor.One, matched[1].DestinationColorFactor);

            // A depth-only pass declares nothing for nothing, which is the one matching count that is empty.
            Assert.Empty(VulkanGraphicsPipelineSpec.ResolveBlends(null, 0));
        }

        /// <summary>
        /// A BLEND STATE COUNT THAT IS NOT THE COLOUR OUTPUT COUNT IS REFUSED BY NAME, IN BOTH DIRECTIONS, rather
        /// than repaired. Vulkan requires the colour blend state's attachment count to EQUAL the rendering
        /// create-info's colour attachment count, and the seam lets the two differ, but repairing the mismatch
        /// means inventing a state the caller never declared, which the Direct3D 11 native backend answers with
        /// its own struct defaults instead, so the two backends would quietly disagree about the same undeclared
        /// attachment. The seam already documents <c>BlendAttachments</c> as one per colour output and every
        /// shipped call site declares exactly that, so enforcing the contract costs nothing and only fires on a
        /// description that was already wrong.
        /// </summary>
        [Fact]
        public void ABlendStateCountThatIsNotTheOutputCount_IsRefusedByName()
        {
            // Fewer declared than there are outputs. Padding would write every channel of an attachment nobody
            // described.
            ArgumentException tooFew = Assert.Throws<ArgumentException>(
                () => VulkanGraphicsPipelineSpec.ResolveBlends([GpuBlendAttachment.AlphaBlend], colourCount: 3));

            Assert.Contains("1 blend attachment state", tooFew.Message, StringComparison.Ordinal);
            Assert.Contains("3 colour output", tooFew.Message, StringComparison.Ordinal);
            Assert.Contains("one per colour output", tooFew.Message, StringComparison.Ordinal);

            // More declared than there are outputs: the extras name attachments that do not exist, and dropping
            // them throws away a state the caller wrote and meant.
            Assert.Throws<ArgumentException>(() => VulkanGraphicsPipelineSpec.ResolveBlends(
                [GpuBlendAttachment.AlphaBlend, GpuBlendAttachment.Additive], colourCount: 1));

            // Declaring none is a mismatch too, whenever there IS a colour output to describe.
            Assert.Throws<ArgumentException>(() => VulkanGraphicsPipelineSpec.ResolveBlends(null, 1));
            Assert.Throws<ArgumentException>(
                () => VulkanGraphicsPipelineSpec.ResolveBlends([GpuBlendAttachment.OverrideBlend], 0));
        }

        /// <summary>
        /// AND THE FACTORY REFUSES IT TOO, before anything native happens, which is what makes the resolver's own
        /// refusal reachable from a description a caller actually writes rather than only from a direct call.
        /// </summary>
        [Fact]
        public void AMismatchedBlendCountOnADescription_IsRefusedBeforeCreation()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                ArgumentException ex = Assert.Throws<ArgumentException>(
                    () => fixture.Factory.CreateGraphicsPipeline(Description(shaders, [],
                        outputs: new GpuOutputDescription(
                            null, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float),
                        blends: [GpuBlendAttachment.AlphaBlend])));

                Assert.Contains("one per colour output", ex.Message, StringComparison.Ordinal);
                Assert.Empty(fixture.PipelineApi.Graphics);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A PIPELINE NAMES THE SHADER SET'S OWN MODULES AND THE SHARED PIPELINE LAYOUT, and the blend constant
        /// rides the pipeline rather than being dynamic state.
        /// <para>
        /// IT ALSO CARRIES THE VERTEX INPUT DERIVED FROM THE DESCRIPTION'S OWN LAYOUTS, asserted HERE rather than
        /// only against <see cref="VulkanVertexInput.Build"/> directly. The derivation tests above drive that
        /// helper themselves, so a factory that handed the spec a null or an empty layout list instead of the
        /// description's would pass every one of them and produce a pipeline with no vertex input at all.
        /// </para>
        /// </summary>
        [Fact]
        public void AGraphicsPipeline_NamesItsModulesItsLayoutItsVertexInputAndItsBakedBlendConstant()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            var vertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                    new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2)),
                new(0, 1, [new GpuVertexElement("InstanceColor", GpuVertexElementFormat.Float4)]),
            };

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                    VulkanResourceFixture.UniformLayout());
                owned.Add(layout);

                IGpuPipeline pipeline = fixture.Factory.CreateGraphicsPipeline(Description(
                    shaders, [layout], blendFactor: new Vector4(0.25f, 0.5f, 0.75f, 1f),
                    vertexLayouts: vertexLayouts));
                owned.Add(pipeline);

                VulkanGraphicsPipelineSpec spec = Assert.Single(fixture.PipelineApi.Graphics);
                Assert.Equal(shaders.VertexModule, spec.VertexModule);
                Assert.Equal(shaders.FragmentModule, spec.FragmentModule);
                Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), spec.BlendFactor);

                Assert.Equal([0u, 1u], spec.VertexBindings.Select(b => b.Binding));
                Assert.Equal([20u, 16u], spec.VertexBindings.Select(b => b.Stride));
                Assert.Equal([false, true], spec.VertexBindings.Select(b => b.PerInstance));
                Assert.Equal([0u, 1u, 2u], spec.VertexAttributes.Select(a => a.Location));
                Assert.Equal([0u, 0u, 1u], spec.VertexAttributes.Select(a => a.Binding));
                Assert.Equal([0u, 12u, 0u], spec.VertexAttributes.Select(a => a.Offset));

                var native = Assert.IsType<VulkanGraphicsPipeline>(pipeline);
                Assert.Equal(spec.PipelineLayout, native.PipelineLayout);
                Assert.Equal([((VulkanResourceLayout)layout).SetLayout], native.SetLayouts);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// TWO PIPELINES BUILT FROM THE SAME LAYOUT SHAPES SHARE ONE <c>VkPipelineLayout</c> (V-D5), even when
        /// their <see cref="IGpuResourceLayout"/> objects were created separately. That is not a
        /// micro-optimisation: it is what makes row 11's compatibility prefix a pointer compare and what lets
        /// bound descriptors survive a pipeline switch at all.
        /// </summary>
        [Fact]
        public void TwoPipelinesWithTheSameLayoutShapes_ShareOnePipelineLayout()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                IGpuResourceLayout first = fixture.Factory.CreateResourceLayout(
                    VulkanResourceFixture.UniformLayout());
                IGpuResourceLayout second = fixture.Factory.CreateResourceLayout(
                    VulkanResourceFixture.UniformLayout());
                owned.Add(first);
                owned.Add(second);

                owned.Add(fixture.Factory.CreateGraphicsPipeline(Description(shaders, [first])));
                owned.Add(fixture.Factory.CreateGraphicsPipeline(Description(shaders, [second])));

                Assert.Equal(2, fixture.PipelineApi.Graphics.Count);
                Assert.Equal(
                    fixture.PipelineApi.Graphics[0].PipelineLayout,
                    fixture.PipelineApi.Graphics[1].PipelineLayout);
                Assert.Equal(1, fixture.Descriptors.PipelineLayouts.DistinctPipelineLayoutCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A COMPUTE PIPELINE IS TWO HANDLES AND NOTHING ELSE. No vertex input, no blend, depth or raster state,
        /// no dynamic state and no attachment formats, which is the seam's own split
        /// (<see cref="GpuComputePipelineDescription"/> is a separate type) arriving intact.
        /// </summary>
        [Fact]
        public void AComputePipeline_IsTheLayoutAndTheModule()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shader = (VulkanComputeShader)fixture.Factory.CreateComputeShaderFromSpirv(ComputeGlsl);
                owned.Add(shader);

                IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                    new GpuResourceLayoutDescription(new GpuResourceLayoutElement(
                        "U", GpuResourceKind.UniformBuffer, GpuShaderStages.Compute)));
                owned.Add(layout);

                IGpuComputePipeline pipeline = fixture.Factory.CreateComputePipeline(
                    new GpuComputePipelineDescription(shader, layout));
                owned.Add(pipeline);

                VulkanComputePipelineSpec spec = Assert.Single(fixture.PipelineApi.Compute);
                Assert.Equal(shader.Module, spec.Module);
                Assert.NotEqual(0UL, spec.PipelineLayout);
                Assert.Empty(fixture.PipelineApi.Graphics);

                var native = Assert.IsType<VulkanComputePipeline>(pipeline);
                Assert.Equal(spec.PipelineLayout, native.PipelineLayout);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// EVERY PIPELINE IS COMPILED THROUGH THE DEVICE'S ONE <c>VkPipelineCache</c> (V-S7). The incumbent passes
        /// <c>VkPipelineCache.Null</c> at BOTH creation sites, so every launch recompiles every pipeline from
        /// SPIR-V, and asserting the handle here is what stops that being reintroduced by an edit to one of the
        /// two call sites.
        /// </summary>
        [Fact]
        public void EveryPipeline_IsCompiledThroughTheDevicesOneCache()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                var compute = (VulkanComputeShader)fixture.Factory.CreateComputeShaderFromSpirv(ComputeGlsl);
                owned.Add(shaders);
                owned.Add(compute);

                owned.Add(fixture.Factory.CreateGraphicsPipeline(Description(shaders, [])));
                owned.Add(fixture.Factory.CreateComputePipeline(new GpuComputePipelineDescription(compute)));

                ulong cache = fixture.Pipelines.Cache.Handle;
                Assert.NotEqual(0UL, cache);
                Assert.Equal([cache, cache], fixture.PipelineApi.CachesUsed);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- refusals ----

        /// <summary>
        /// A SHADER SET OR A RESOURCE LAYOUT FROM ANOTHER BACKEND IS REFUSED BY NAME, before anything native
        /// happens. It holds another backend's compiled modules, so the alternative to a named refusal is a cast
        /// failure inside a create-info.
        /// </summary>
        [Fact]
        public void AForeignShaderSetOrLayout_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                ArgumentException foreignShaders = Assert.Throws<ArgumentException>(
                    () => fixture.Factory.CreateGraphicsPipeline(Description(new ForeignShaderSet(), [])));
                Assert.Contains("not compiled by the native Vulkan backend", foreignShaders.Message,
                    StringComparison.Ordinal);

                ArgumentException foreignLayout = Assert.Throws<ArgumentException>(
                    () => fixture.Factory.CreateGraphicsPipeline(
                        Description(shaders, [new ForeignResourceLayout()])));
                Assert.Contains("not created by the native Vulkan backend", foreignLayout.Message,
                    StringComparison.Ordinal);

                ArgumentException foreignCompute = Assert.Throws<ArgumentException>(
                    () => fixture.Factory.CreateComputePipeline(
                        new GpuComputePipelineDescription(new ForeignComputeShader())));
                Assert.Contains("not compiled by the native Vulkan backend", foreignCompute.Message,
                    StringComparison.Ordinal);

                Assert.Empty(fixture.PipelineApi.Graphics);
                Assert.Empty(fixture.PipelineApi.Compute);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// THE DYNAMIC UNIFORM LIMIT IS CHECKED WHERE THE PIPELINE LAYOUT IS TAKEN, which is 8.3's third defence
        /// and the only one of its four that answers for the MACHINE at run time. Row 10 landed the check one row
        /// early, and this is its first real caller: a pipeline whose layouts spend more dynamic uniform
        /// descriptors between them than the device allows is refused rather than created and left to fail
        /// validation at a draw.
        /// </summary>
        [Fact]
        public void TheDynamicUniformLimit_IsCheckedWhereThePipelineLayoutIsTaken()
        {
            var fixture = new VulkanResourceFixture(maxDynamicUniformBuffers: 2);
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                var layouts = new IGpuResourceLayout[3];
                for (int i = 0; i < layouts.Length; i++)
                {
                    layouts[i] = fixture.Factory.CreateResourceLayout(
                        VulkanResourceFixture.UniformLayout(name: "U" + i));
                    owned.Add(layouts[i]);
                }

                Assert.Throws<NotSupportedException>(
                    () => fixture.Factory.CreateGraphicsPipeline(Description(shaders, layouts)));

                Assert.Empty(fixture.PipelineApi.Graphics);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- disposal ----

        /// <summary>
        /// DISPOSAL IS A REAL DESTROY, HELD BEHIND THE TIMELINE (V-F9), unlike a shader set's or a resource
        /// layout's, which release nothing because their handles are shared. A <c>VkPipeline</c> is not shared,
        /// and destroying one a submission in flight still names is undefined behaviour of the quiet kind.
        /// Disposing twice retires once, because a consumer disposing a pipeline twice is a teardown-order
        /// accident rather than a defect.
        /// </summary>
        [Fact]
        public void DisposingAPipeline_RetiresOneDestroyBehindTheTimeline()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
                owned.Add(shaders);

                IGpuPipeline pipeline = fixture.Factory.CreateGraphicsPipeline(Description(shaders, []));

                pipeline.Dispose();
                pipeline.Dispose();

                Assert.Empty(fixture.PipelineApi.DestroyedPipelines);

                fixture.Drain();

                ulong destroyed = Assert.Single(fixture.PipelineApi.DestroyedPipelines);
                Assert.NotEqual(0UL, destroyed);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- fixtures ----

        static GpuPipelineDescription Description(IGpuShaderSet shaders, IGpuResourceLayout[] layouts,
            GpuOutputDescription? outputs = null, GpuBlendAttachment[]? blends = null,
            Vector4 blendFactor = default, List<GpuVertexLayoutDescription>? vertexLayouts = null)
            => new()
            {
                BlendFactor = blendFactor,
                BlendAttachments = blends ?? [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Back, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = layouts,
                ShaderSet = shaders,
                VertexLayouts = vertexLayouts ?? [new GpuVertexLayoutDescription(
                    new GpuVertexElement("Position", GpuVertexElementFormat.Float3))],
                Outputs = outputs ?? new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            };

        static void CreateGraphics(VulkanResourceFixture fixture, List<IDisposable> owned,
            GpuOutputDescription outputs, GpuBlendAttachment[] blends)
        {
            var shaders = (VulkanShaderSet)fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
            owned.Add(shaders);
            owned.Add(fixture.Factory.CreateGraphicsPipeline(
                Description(shaders, [], outputs, blends)));
        }

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }

        sealed class ForeignShaderSet : IGpuShaderSet
        {
            public void Dispose() { }
        }

        sealed class ForeignComputeShader : IGpuComputeShader
        {
            public uint ThreadGroupSizeX => 1;
            public uint ThreadGroupSizeY => 1;
            public uint ThreadGroupSizeZ => 1;
            public void Dispose() { }
        }

        sealed class ForeignResourceLayout : IGpuResourceLayout
        {
            public void Dispose() { }
        }
    }
}
