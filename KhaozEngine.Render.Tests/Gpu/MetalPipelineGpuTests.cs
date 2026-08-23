using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// ROW 11'S DEVICE HALF (https://github.com/APKiwiOrg/KhaozEngine/issues/577), and it is deliberately small.
    /// Every decision a pipeline takes is device-free and asserted on every leg
    /// (<see cref="MetalPipelinePlanTests"/>, <see cref="MetalVertexInputTests"/>,
    /// <see cref="MetalPipelineBindingTests"/>), which leaves the claims only a real <c>MTLDevice</c> can settle.
    ///
    /// <para><b>THE FIRST ONE IS M-B2 ITSELF, AND NOTHING ELSE IN THE PROGRAM CAN ANSWER IT.</b> The whole
    /// scheme rests on Metal accepting a vertex buffer layout at index 30 and an attribute pointing at it. That is
    /// what the API's stated 31-entry buffer table implies, and an implication is not a measurement: if a device
    /// refused the top of the space, every graphics pipeline in this backend would fail to create and the design's
    /// answer to the one real collision in Metal's binding model would be gone. So a pipeline is built with two
    /// top-pinned streams and its creation IS the assertion.</para>
    ///
    /// <para><b>THE SECOND IS THE REJECTION PATH, which no shipped description can reach.</b> A pipeline is the
    /// one thing Metal validates against a compiled function for this backend, and the <c>NSError</c> it writes is
    /// the entire diagnostic. A descriptor missing an attribute the vertex function declares is the cheapest way
    /// to make it say so, and it proves the out-parameter shape carries a real message rather than a nil.</para>
    ///
    /// <para><b>THE SHADERS ARE WRITTEN HERE RATHER THAN TAKEN FROM THE CATALOG</b>, because a pipeline needs a
    /// vertex layout that MATCHES its vertex function and the shipped-program catalog carries GLSL without the
    /// layouts its renderers pair with it. The resource layouts, by contrast, are built FROM the reflection the
    /// shader set carries, which is how a test declares an array 2.2b's shape check will accept without
    /// hand-copying it.</para>
    ///
    /// <para>Dormant off macOS rather than skipped, which is phase 3's row-19 lesson: under
    /// <c>KE_GPU_TESTS=1</c> the Vulkan and Direct3D 11 legs run this assembly in strict mode where a skip is a
    /// failure.</para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class MetalPipelineGpuTests
    {
        readonly ITestOutputHelper _output;

        public MetalPipelineGpuTests(ITestOutputHelper output) => _output = output;

        /// <summary>
        /// M-B2 ON REAL HARDWARE: two vertex streams at buffer indices 30 and 29, with a uniform buffer the
        /// vertex stage reads from the bottom of the same space, and Metal builds the pipeline.
        /// </summary>
        [GpuFact]
        public void TopPinnedVertexStreams_AreAcceptedByTheDevice()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(StreamVert, StreamFrag);
            var metal = (MetalShaderSet)shaders;

            List<IGpuResourceLayout> layouts = ReflectedLayouts(factory, metal);
            try
            {
                using IGpuPipeline pipeline = factory.CreateGraphicsPipeline(
                    StreamPipeline(shaders, layouts, depth: GpuPixelFormat.D32FloatS8UInt));

                var typed = (MetalGraphicsPipeline)pipeline;

                Assert.False(typed.RenderState.IsNull);
                Assert.Equal(2, typed.VertexStreamCount);
                Assert.Equal(30u, typed.Plan.Streams[0].BufferIndex);
                Assert.Equal(29u, typed.Plan.Streams[1].BufferIndex);

                // The vertex stage's own uniform buffer is at the BOTTOM of the same space, which is the whole
                // no-collision property standing up on a device rather than on paper.
                _output.WriteLine("vertex-stage buffer indices the emission chose: "
                    + string.Join(", ", MetalVertexPlan.VertexStageBufferIndices(typed.Table)));

                // A DEPTH OUTPUT MEANS A DEPTH-STENCIL STATE, which is the creation half of the depth-target
                // guard.
                Assert.False(typed.DepthStencilState.IsNull);
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        /// <summary>
        /// A colour-only pipeline creates no <c>MTLDepthStencilState</c> at all. That nil is what the pre-draw
        /// block's depth-target guard exists beside: <c>-setDepthStencilState:</c> on a pass with no depth
        /// attachment is a validation error under the debug layer M-T7 arms on every native-leg run.
        /// </summary>
        [GpuFact]
        public void AColourOnlyPipeline_HasNoDepthStencilState()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(FullscreenVert, FullscreenFrag);
            var metal = (MetalShaderSet)shaders;

            // A SHADER THAT REFERENCES NOTHING REFLECTS ZERO SETS since the toolchain swap landed #599 (the
            // incumbent's reflection produced one empty set here, which this row pinned until 18.0.0). The
            // declaration below is still built from the reflection, which is now the empty array, and the shape
            // check accepts that as readily as a single empty declared layout.
            Assert.Empty(metal.Table.Layouts);

            List<IGpuResourceLayout> layouts = ReflectedLayouts(factory, metal);
            try
            {
                GpuPipelineDescription description = FullscreenPipeline(shaders);
                description.ResourceLayouts = layouts.ToArray();

                using IGpuPipeline pipeline = factory.CreateGraphicsPipeline(description);
                var typed = (MetalGraphicsPipeline)pipeline;

                Assert.False(typed.RenderState.IsNull);
                Assert.True(typed.DepthStencilState.IsNull);

                // The fullscreen case end to end: no vertex layouts at all, which is six shipped renderers' shape
                // and the one an empty MTLVertexDescriptor has to be legal for.
                Assert.Empty(typed.Plan.Streams);
                Assert.Empty(typed.Plan.Attributes);

                // Disposal is idempotent and releases both handles once.
                pipeline.Dispose();
                pipeline.Dispose();
                Assert.True(typed.IsDisposed);
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        /// <summary>
        /// THE REJECTION PATH, with Metal's own words in it. The vertex function declares three attributes and
        /// the pipeline declares one stream carrying two of them, so the third has nowhere to come from.
        /// </summary>
        [GpuFact]
        public void ADescriptorMissingAnAttributeTheFunctionDeclares_IsRejectedWithMetalsMessage()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(StreamVert, StreamFrag);
            var metal = (MetalShaderSet)shaders;

            List<IGpuResourceLayout> layouts = ReflectedLayouts(factory, metal);
            try
            {
                GpuPipelineDescription description = StreamPipeline(shaders, layouts, depth: null);

                // Slot 1, the instance stream, removed. Attribute 2 is now undefined.
                description.VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    description.VertexLayouts[0],
                };

                ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                    () => factory.CreateGraphicsPipeline(description));

                _output.WriteLine(error.Message);

                // Metal's own diagnostic, not a paraphrase. It names the attribute, which is the only thing that
                // makes a rejected pipeline actionable.
                Assert.Contains("rejected this graphics pipeline", error.Message, StringComparison.Ordinal);

                // THE TOKEN ONLY METAL CAN SUPPLY. The wrapper's own prefix and tail already contain "rejected
                // this graphics pipeline" and the words "vertex layouts", so an assertion on either passes on
                // the arm where -newRenderPipelineStateWithDescriptor:error: answered nil and wrote NO NSError,
                // which is precisely the case this row exists to distinguish. "attribute" appears nowhere in the
                // wrapper and is the word Metal's own message is built around (measured: "Vertex attribute
                // m_17(2) is missing from the vertex descriptor").
                Assert.Contains("attribute", error.Message, StringComparison.Ordinal);

                // And the nil arm named outright, so a future edit to the wrapper's wording cannot make the line
                // above pass for the wrong reason.
                Assert.DoesNotContain("wrote no NSError", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        /// <summary>A pipeline built for a multisampled target creates, which is the one setter the incumbent
        /// writes conditionally and the one output field a colour format does not carry.</summary>
        [GpuFact]
        public void AMultisampledPipeline_IsCreatedWithItsSampleCount()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuShaderSet shaders = factory.CreateShadersFromSpirv(FullscreenVert, FullscreenFrag);

            List<IGpuResourceLayout> layouts = ReflectedLayouts(factory, (MetalShaderSet)shaders);
            try
            {
                GpuPipelineDescription description = FullscreenPipeline(shaders);
                description.ResourceLayouts = layouts.ToArray();
                description.Outputs = description.Outputs.WithSampleCount(4);

                using IGpuPipeline pipeline = factory.CreateGraphicsPipeline(description);

                var typed = (MetalGraphicsPipeline)pipeline;

                // WHAT THIS PINS IS THE CREATION, not the setter. Metal accepting the descriptor says the sample
                // count reached it in a shape the driver liked, and the plan carrying 4 says this backend
                // resolved it. Neither reads the value back off the MTLRenderPipelineState, because there is no
                // getter for it, so a wrong count that Metal happened to accept would still pass here. The
                // rendered proof is the MSAA golden the draw row bakes.
                Assert.False(typed.RenderState.IsNull);
                Assert.Equal(4, typed.Plan.SampleCount);
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        /// <summary>
        /// The compute half: a kernel becomes a pipeline state through the FUNCTION route, with no descriptor and
        /// no mutability bookkeeping, and it carries the workgroup size the module declared.
        /// </summary>
        [GpuFact]
        public void AComputePipeline_IsCreatedFromTheFunctionAlone()
        {
            if (!Available()) return;

            using IGpuDevice device = CreateHeadless();
            IGpuResourceFactory factory = device.Factory;

            using IGpuComputeShader shader = factory.CreateComputeShaderFromSpirv(ComputeSrc);
            var metal = (MetalComputeShader)shader;

            List<IGpuResourceLayout> layouts = ReflectedLayouts(factory, metal.Table);
            try
            {
                using IGpuComputePipeline pipeline = factory.CreateComputePipeline(
                    new GpuComputePipelineDescription(shader, layouts.ToArray()));

                var typed = (MetalComputePipeline)pipeline;
                Assert.False(typed.State.IsNull);
                Assert.Same(metal.Table, typed.Table);
                Assert.Equal(64u, shader.ThreadGroupSizeX);

                pipeline.Dispose();
                pipeline.Dispose();
                Assert.True(typed.IsDisposed);
            }
            finally
            {
                foreach (IGpuResourceLayout layout in layouts) layout.Dispose();
            }
        }

        // ---- fixtures ------------------------------------------------------------------------------------

        // A layout array built FROM the reflection the shader set carries, which is what makes 2.2b's shape check
        // pass without a test hand-copying the declaration. The names are the reflection's own, which the table
        // deliberately does not read.
        static List<IGpuResourceLayout> ReflectedLayouts(IGpuResourceFactory factory, MetalShaderSet shaders)
            => ReflectedLayouts(factory, shaders.Table);

        static List<IGpuResourceLayout> ReflectedLayouts(IGpuResourceFactory factory, MetalShaderIndexTable table)
        {
            var layouts = new List<IGpuResourceLayout>();
            foreach (GpuResourceLayoutDescription reflected in table.Layouts)
                layouts.Add(factory.CreateResourceLayout(reflected));
            return layouts;
        }

        static GpuPipelineDescription FullscreenPipeline(IGpuShaderSet shaders)
            => new()
            {
                ShaderSet = shaders,
                ResourceLayouts = [],
                BlendAttachments = [GpuBlendAttachment.AlphaBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm),
            };

        static GpuPipelineDescription StreamPipeline(IGpuShaderSet shaders, List<IGpuResourceLayout> layouts,
            GpuPixelFormat? depth)
            => new()
            {
                ShaderSet = shaders,
                ResourceLayouts = layouts.ToArray(),
                BlendAttachments = [GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.PreserveDestination],
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.Back, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: true),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>
                {
                    // Slot 0, per vertex: position and uv, which are attributes 0 and 1.
                    new(new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
                        new GpuVertexElement("Uv", GpuVertexElementFormat.Float2)),

                    // Slot 1, per instance: attribute 2. The model pass's shape.
                    new(0, 1, [new GpuVertexElement("InstanceTint", GpuVertexElementFormat.Float4)]),
                },
                Outputs = new GpuOutputDescription(
                    depth, GpuPixelFormat.B8G8R8A8UNorm, GpuPixelFormat.R16G16B16A16Float),
            };

        const string FullscreenVert = @"#version 450
void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2);
    gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}";

        const string FullscreenFrag = @"#version 450
layout(location=0) out vec4 Colour;
void main() { Colour = vec4(0.0, 1.0, 0.0, 1.0); }";

        // Three attributes across two buffers, plus a vertex-stage uniform block, which is the shape M-B2 exists
        // for: the streams take the top of the buffer space and the uniform block takes the bottom.
        const string StreamVert = @"#version 450
layout(location=0) in vec3 Position;
layout(location=1) in vec2 Uv;
layout(location=2) in vec4 InstanceTint;
layout(set=0, binding=0) uniform FrameBlock { mat4 ViewProjection; };
layout(location=0) out vec2 vUv;
layout(location=1) out vec4 vTint;
void main()
{
    vUv = Uv;
    vTint = InstanceTint;
    gl_Position = ViewProjection * vec4(Position, 1.0);
}";

        const string StreamFrag = @"#version 450
layout(location=0) in vec2 vUv;
layout(location=1) in vec4 vTint;
layout(set=0, binding=1) uniform texture2D Albedo;
layout(set=0, binding=2) uniform sampler AlbedoSampler;
layout(location=0) out vec4 Colour;
layout(location=1) out vec4 Normal;
void main()
{
    Colour = texture(sampler2D(Albedo, AlbedoSampler), vUv) * vTint;
    Normal = vec4(0.0, 0.0, 1.0, 1.0);
}";

        const string ComputeSrc = @"#version 450
layout(local_size_x=64) in;
layout(set=0, binding=0) buffer Values { float Data[]; };
void main() { Data[gl_GlobalInvocationID.x] = Data[gl_GlobalInvocationID.x] * 2.0; }";

        static IGpuDevice CreateHeadless() => new MetalBackendProvider().CreateHeadless().Device;

        [SupportedOSPlatformGuard("macos")]
        bool Available()
        {
            if (!KhaozEngineMetal.IsPlatformSupported)
            {
                // KE_METAL_REQUIRED=1 turns this into a throw on the leg that declared a device mandatory.
                MetalDormancy.ThrowIfRequired("this is not macOS at all");
                _output.WriteLine("dormant: not macOS, so there is no Metal device to create pipelines on.");
                return false;
            }

            string? missing = MetalSupportProbe.MissingRequirement();
            if (missing is null) return true;

            MetalDormancy.ThrowIfRequired(missing);
            _output.WriteLine("dormant: this machine cannot run the native Metal backend (" + missing + ").");
            return false;
        }
    }
}
