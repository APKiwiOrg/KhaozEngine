using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// EVERY DECISION AND EVERY REFUSAL A PIPELINE OWNS, WITH NO DEVICE ANYWHERE. Work-breakdown row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577).
    ///
    /// <para><b>THIS IS THE FILE THAT MAKES ROW 11 ASSERTED ON FIVE LEGS RATHER THAN ONE.</b> Pipeline creation
    /// makes exactly two native calls and takes six decisions in front of them, and every one of the six is a
    /// fact about managed data: the ownership checks, 2.2b's layout-shape check, M-B2's collision assertion, the
    /// vertex plan, the blend and depth mapping, and the attachment formats. <c>MetalPipelineGpuTests</c> is the
    /// device half and it is deliberately small.</para>
    ///
    /// <para><b>THE LAYOUT-SHAPE ROW IS PIN 4 OF SECTION 2.2b ARRIVING AT ITS CALL SITE.</b> Row 9 wrote
    /// <c>MetalShaderIndexTable.RequireLayoutShape</c> and left it with no caller, because pipeline creation is
    /// the first moment the ENGINE-declared layout array and the reflection the table was built from exist
    /// together. <c>MetalShaderIndexTableRefusalTests</c> drives the check itself over all three mismatch kinds.
    /// What is asserted HERE is the thing that file cannot see: that a pipeline actually calls it.</para>
    ///
    /// <para><b>THE SHADER SETS ARE HAND-BUILT WITH NIL FUNCTIONS, which is what makes them device-free.</b> A
    /// <c>MetalShaderSet</c> is a liveness token, an array of compiled stages and a binding table, and only the
    /// first native call would ever dereference the stages. Nothing below reaches one.</para>
    /// </summary>
    public sealed class MetalPipelinePlanTests
    {
        readonly FakeMetalDeviceLiveness _liveness = new();

        /// <summary>
        /// PIN 4's CALL SITE. A pipeline whose declared layout array disagrees with its shader's reflection is
        /// refused at creation, before anything native happens.
        /// </summary>
        [Fact]
        public void ADeclaredLayoutArrayOfADifferentShape_IsRefusedAtPipelineCreation()
        {
            // The shader reflected one uniform buffer. The pipeline declares a SAMPLER at the same position.
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));

            GpuPipelineDescription description = Description(shaders, Layout(GpuResourceKind.Sampler));

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, description));

            Assert.Contains("A native Metal graphics pipeline", error.Message, StringComparison.Ordinal);
            Assert.Contains("Sampler", error.Message, StringComparison.Ordinal);

            // And the matching declaration passes, so the row above is about the SHAPE rather than about the
            // check being unsatisfiable.
            MetalGraphicsPipelinePlan plan = MetalGraphicsPipelinePlan.Build(
                _liveness, Description(shaders, Layout(GpuResourceKind.UniformBuffer)));

            Assert.Single(plan.Layouts);
            Assert.Same(shaders.Table, plan.Table);
        }

        /// <summary>An element count that disagrees is the same failure through a different arm, and a pipeline
        /// with no layouts at all against a shader that reflected one is the case a caller reaches by forgetting
        /// the array rather than by getting it wrong.</summary>
        [Fact]
        public void AnEmptyLayoutArrayAgainstAReflectedOne_IsRefused()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, Description(shaders)));

            Assert.Contains("0 resource layouts", error.Message, StringComparison.Ordinal);
        }

        /// <summary>A shader set from another device is refused by name, and so is a layout, both through the one
        /// helper row 10 wrote so a pipeline and a resource set say the same thing.</summary>
        [Fact]
        public void AnotherDevicesShaderSetOrLayout_IsRefused()
        {
            var otherDevice = new FakeMetalDeviceLiveness();
            MetalShaderIndexTable table = TableFor(GpuResourceKind.UniformBuffer);

            ArgumentException byShaders = Assert.Throws<ArgumentException>(
                () => MetalGraphicsPipelinePlan.Build(
                    _liveness,
                    Description(ShaderSet(table, otherDevice), Layout(GpuResourceKind.UniformBuffer))));
            Assert.Contains("DIFFERENT native Metal device", byShaders.Message, StringComparison.Ordinal);

            ArgumentException byLayout = Assert.Throws<ArgumentException>(
                () => MetalGraphicsPipelinePlan.Build(
                    _liveness,
                    Description(ShaderSet(table), LayoutOn(otherDevice, GpuResourceKind.UniformBuffer))));
            Assert.Contains("DIFFERENT native Metal device", byLayout.Message, StringComparison.Ordinal);
        }

        /// <summary>A disposed layout is refused, which is the flag row 10 kept precisely because a Metal layout
        /// releases nothing and the call would otherwise work.</summary>
        [Fact]
        public void ADisposedLayout_IsRefused()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));
            IGpuResourceLayout[] layouts = Layout(GpuResourceKind.UniformBuffer);
            layouts[0].Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, Description(shaders, layouts)));
        }

        /// <summary>
        /// A DISPOSED SHADER SET IS REFUSED ON EVERY LEG, which is <c>ADisposedLayout_IsRefused</c>'s shape
        /// applied to the other input, and it is here rather than only on macOS for a reason worth stating.
        /// <c>MetalShaderSet.FunctionFor</c> throws for a disposed set, but the only caller of it is the native
        /// half, so before this check the refusal was made on ONE leg out of five, by the very type whose reason
        /// for existing is that every pipeline refusal is a fact about managed data. Both kinds are driven,
        /// because the compute half has its own copy of the check and no shared helper to keep them in step.
        /// </summary>
        [Fact]
        public void ADisposedShaderSetOrKernel_IsRefusedDeviceFree()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));
            shaders.Dispose();

            ObjectDisposedException refused = Assert.Throws<ObjectDisposedException>(
                () => MetalGraphicsPipelinePlan.Build(
                    _liveness, Description(shaders, Layout(GpuResourceKind.UniformBuffer))));
            Assert.Contains("already disposed", refused.Message, StringComparison.Ordinal);

            MetalComputeShader kernel = new(
                _liveness, default, TableFor(GpuResourceKind.UniformBuffer), 64, 1, 1);
            kernel.Dispose();

            ObjectDisposedException refusedKernel = Assert.Throws<ObjectDisposedException>(
                () => MetalComputePipeline.Check(
                    _liveness,
                    new GpuComputePipelineDescription(kernel, Layout(GpuResourceKind.UniformBuffer))));
            Assert.Contains("already disposed", refusedKernel.Message, StringComparison.Ordinal);
        }

        /// <summary>No shader set at all is a named refusal rather than a null reference from inside the
        /// cast.</summary>
        [Fact]
        public void NoShaderSet_IsRefusedByName()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, default));

            Assert.Contains("was given no shader set", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// M-B2's COLLISION ASSERTION IS REACHED FROM PIPELINE CREATION, which is where the stream count comes
        /// from. <c>MetalVertexInputTests</c> drives the check itself.
        /// </summary>
        [Fact]
        public void AVertexStreamCollision_IsRefusedAtPipelineCreation()
        {
            // A vertex stage reading a uniform buffer at [[buffer(30)]], which is where stream 0 goes.
            MetalShaderSet shaders = ShaderSet(VertexTableAt(30));

            GpuPipelineDescription description = Description(shaders, Layout(GpuResourceKind.UniformBuffer));
            description.VertexLayouts = new List<GpuVertexLayoutDescription>
            {
                new(new GpuVertexElement("Position", GpuVertexElementFormat.Float3)),
            };

            ShaderValidationException error = Assert.Throws<ShaderValidationException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, description));
            Assert.Contains("[[buffer(30)]]", error.Message, StringComparison.Ordinal);

            // The same program with NO vertex streams is fine, because nothing occupies the top of the space.
            description.VertexLayouts = new List<GpuVertexLayoutDescription>();
            MetalGraphicsPipelinePlan plan = MetalGraphicsPipelinePlan.Build(_liveness, description);
            Assert.Empty(plan.Streams);
        }

        /// <summary>Fewer blend states than colour attachments cannot be described at all, because Metal carries
        /// the blend state ON the attachment.</summary>
        [Fact]
        public void FewerBlendStatesThanColourAttachments_IsRefused()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));
            GpuPipelineDescription description = Description(shaders, Layout(GpuResourceKind.UniformBuffer));
            description.Outputs = new GpuOutputDescription(
                null, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float);
            description.BlendAttachments = [GpuBlendAttachment.OverrideBlend];

            ArgumentException error = Assert.Throws<ArgumentException>(
                () => MetalGraphicsPipelinePlan.Build(_liveness, description));

            Assert.Contains("2 colour attachments and 1 blend states", error.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE DEPTH STATE, RESOLVED, and the field the seam has that Metal does not. A depth-stencil descriptor
        /// carries no test enable, so a disabled test is <c>Always</c> with writes off, and the write flag is
        /// ANDed rather than passed through.
        /// </summary>
        [Fact]
        public void ADisabledDepthTestResolvesToAlwaysWithNoWrite()
        {
            MetalPipelineState off = MetalPipelineSpecs.ResolveState(
                WithDepth(GpuDepthStencilState.Disabled));

            Assert.Equal(MTLCompareFunction.Always, off.DepthComparison);
            Assert.False(off.DepthWriteEnabled);

            MetalPipelineState on = MetalPipelineSpecs.ResolveState(
                WithDepth(GpuDepthStencilState.DepthOnlyLessEqual));

            Assert.Equal(MTLCompareFunction.LessEqual, on.DepthComparison);
            Assert.True(on.DepthWriteEnabled);

            MetalPipelineState noWrite = MetalPipelineSpecs.ResolveState(
                WithDepth(GpuDepthStencilState.DepthTestLessEqualNoWrite));

            Assert.Equal(MTLCompareFunction.LessEqual, noWrite.DepthComparison);
            Assert.False(noWrite.DepthWriteEnabled);

            // A description asking for writes with the test off cannot be expressed as a Metal depth state, and
            // the AND is what stops it becoming a write that always passes.
            MetalPipelineState contradictory = MetalPipelineSpecs.ResolveState(
                WithDepth(new GpuDepthStencilState(false, true, GpuComparison.Less)));

            Assert.Equal(MTLCompareFunction.Always, contradictory.DepthComparison);
            Assert.False(contradictory.DepthWriteEnabled);
        }

        /// <summary>
        /// THE DEPTH CLIP MODE COMES FROM THE DEPTH TEST, which is the incumbent's derivation reproduced and
        /// which https://github.com/APKiwiOrg/KhaozEngine/issues/598 records as a seam question. The seam's own
        /// <c>DepthClipEnabled</c> reaches nothing here, and this row says so out loud rather than leaving the
        /// absence to be discovered.
        /// </summary>
        [Fact]
        public void TheDepthClipModeIgnoresTheSeamsFlagAndFollowsTheDepthTest()
        {
            GpuPipelineDescription clipAskedForTestOff = WithDepth(GpuDepthStencilState.Disabled);
            clipAskedForTestOff.Rasterizer = new GpuRasterizerState(
                GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: true, scissorTestEnabled: false);

            Assert.Equal(MTLDepthClipMode.Clamp,
                MetalPipelineSpecs.ResolveState(clipAskedForTestOff).DepthClipMode);

            GpuPipelineDescription clampAskedForTestOn = WithDepth(GpuDepthStencilState.DepthOnlyLessEqual);
            clampAskedForTestOn.Rasterizer = new GpuRasterizerState(
                GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: false, scissorTestEnabled: false);

            Assert.Equal(MTLDepthClipMode.Clip,
                MetalPipelineSpecs.ResolveState(clampAskedForTestOn).DepthClipMode);
        }

        /// <summary>The rasterizer and topology maps, and the two values the state block carries that come from
        /// nowhere else: the blend colour and the stencil reference the seam has no member for.</summary>
        [Fact]
        public void TheRasterizerStateAndTheTwoCarriedValuesResolve()
        {
            GpuPipelineDescription description = WithDepth(GpuDepthStencilState.Disabled);
            description.Rasterizer = new GpuRasterizerState(
                GpuFaceCull.Front, GpuPolygonFill.Wireframe, GpuFrontFace.CounterClockwise,
                depthClipEnabled: true, scissorTestEnabled: true);
            description.Topology = GpuPrimitiveTopology.LineStrip;
            description.BlendFactor = new Vector4(0.25f, 0.5f, 0.75f, 1f);

            MetalPipelineState state = MetalPipelineSpecs.ResolveState(description);

            Assert.Equal(MTLCullMode.Front, state.CullMode);
            Assert.Equal(MTLTriangleFillMode.Lines, state.FillMode);
            Assert.Equal(MTLWinding.CounterClockwise, state.FrontFace);
            Assert.Equal(MTLPrimitiveType.LineStrip, state.PrimitiveType);
            Assert.True(state.ScissorTestEnabled);
            Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1f), state.BlendColour);

            // ALWAYS ZERO, and named rather than left implicit: the seam carries no stencil state at all, so
            // there is no engine value a reference could come from.
            Assert.Equal(0u, state.StencilReference);
        }

        /// <summary>The three shipped blend presets, resolved. The multiple-render-target case is what makes this
        /// per attachment rather than one shared state.</summary>
        [Fact]
        public void EachAttachmentKeepsItsOwnBlendState()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));
            GpuPipelineDescription description = Description(shaders, Layout(GpuResourceKind.UniformBuffer));
            description.Outputs = new GpuOutputDescription(
                null, GpuPixelFormat.R8G8B8A8UNorm, GpuPixelFormat.R16G16B16A16Float, GpuPixelFormat.R32Float);
            description.BlendAttachments =
            [
                GpuBlendAttachment.AlphaBlend,
                GpuBlendAttachment.PreserveDestination,
                GpuBlendAttachment.OverrideBlend,
            ];

            MetalColourAttachmentState[] attachments = MetalPipelineSpecs.ResolveColourAttachments(
                description, "test");

            Assert.Equal(3, attachments.Length);

            Assert.True(attachments[0].BlendingEnabled);
            Assert.Equal(MTLBlendFactor.SourceAlpha, attachments[0].SourceColour);
            Assert.Equal(MTLBlendFactor.OneMinusSourceAlpha, attachments[0].DestinationColour);
            Assert.Equal(MTLBlendOperation.Add, attachments[0].ColourOperation);

            // PreserveDestination is out = dst, which the model pass's normal and linear-depth targets take under
            // the billboard pass. Collapsing the three onto one state would paint them with the colour pass's.
            Assert.True(attachments[1].BlendingEnabled);
            Assert.Equal(MTLBlendFactor.Zero, attachments[1].SourceColour);
            Assert.Equal(MTLBlendFactor.One, attachments[1].DestinationColour);

            Assert.False(attachments[2].BlendingEnabled);

            // R32Float AS A COLOUR ATTACHMENT, which is the one seam format that means two Metal formats. Reading
            // it as a depth format here would give the linear-depth target one the fragment function cannot
            // write.
            Assert.Equal(MTLPixelFormat.R32Float, attachments[2].Format);
            Assert.Equal(MTLPixelFormat.BGRA8Unorm, MetalFormats.ToPixelFormat(
                GpuPixelFormat.B8G8R8A8UNorm, depthFormat: false));

            // Every attachment writes every channel, because the seam has no write mask and the incumbent
            // reaches the same value through its own default.
            foreach (MetalColourAttachmentState attachment in attachments)
                Assert.Equal(MTLColorWriteMask.All, attachment.WriteMask);
        }

        /// <summary>The depth and stencil attachment formats, including the combined case and the one seam format
        /// that is a depth format here and a colour format above.</summary>
        [Fact]
        public void TheDepthFormatIsResolvedAsDepthAndTheStencilOneOnlyWhenCombined()
        {
            var none = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm);
            Assert.Null(MetalPipelineSpecs.ResolveDepthFormat(none));
            Assert.Null(MetalPipelineSpecs.ResolveStencilFormat(none));

            // A shadow map's R32Float, read as a DEPTH format, which is Depth32Float and not R32Float.
            var shadow = new GpuOutputDescription(GpuPixelFormat.R32Float);
            Assert.Equal(MTLPixelFormat.Depth32Float, MetalPipelineSpecs.ResolveDepthFormat(shadow));
            Assert.Null(MetalPipelineSpecs.ResolveStencilFormat(shadow));

            var combined = new GpuOutputDescription(
                GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.B8G8R8A8UNorm);
            Assert.Equal(MTLPixelFormat.Depth32FloatStencil8, MetalPipelineSpecs.ResolveDepthFormat(combined));
            Assert.Equal(MTLPixelFormat.Depth32FloatStencil8, MetalPipelineSpecs.ResolveStencilFormat(combined));
        }

        /// <summary>The sample count travels with the plan, because a pipeline must match the framebuffer it
        /// draws into.</summary>
        [Fact]
        public void TheSampleCountTravelsWithThePlan()
        {
            MetalShaderSet shaders = ShaderSet(TableFor(GpuResourceKind.UniformBuffer));
            GpuPipelineDescription description = Description(shaders, Layout(GpuResourceKind.UniformBuffer));
            description.Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm)
                .WithSampleCount(4);

            Assert.Equal(4, MetalGraphicsPipelinePlan.Build(_liveness, description).SampleCount);
        }

        /// <summary>
        /// THE COMPUTE HALF CALLS THE SAME SHAPE CHECK, and the failure there is worse rather than milder: a
        /// dispatch writing through a storage buffer resolved from another declaration corrupts memory the next
        /// pass reads.
        /// </summary>
        [Fact]
        public void AComputePipelinesLayoutArrayIsCheckedAgainstItsKernel()
        {
            MetalComputeShader kernel = new(
                _liveness, default, TableFor(GpuResourceKind.StructuredBufferReadWrite), 64, 1, 1);

            Assert.Throws<ShaderValidationException>(() => MetalComputePipeline.Check(
                _liveness,
                new GpuComputePipelineDescription(kernel, Layout(GpuResourceKind.UniformBuffer))));

            (MetalComputeShader shader, MetalResourceLayout[] layouts) = MetalComputePipeline.Check(
                _liveness,
                new GpuComputePipelineDescription(kernel, Layout(GpuResourceKind.StructuredBufferReadWrite)));

            Assert.Same(kernel, shader);
            Assert.Single(layouts);
        }

        /// <summary>No compute shader is a named refusal, matching the graphics half.</summary>
        [Fact]
        public void NoComputeShader_IsRefusedByName()
        {
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => MetalComputePipeline.Check(_liveness, default));

            Assert.Contains("was given no compute shader", error.Message, StringComparison.Ordinal);
        }

        // ---- fixtures ------------------------------------------------------------------------------------

        GpuPipelineDescription Description(MetalShaderSet shaders, params IGpuResourceLayout[] layouts)
            => new()
            {
                ShaderSet = shaders,
                ResourceLayouts = layouts,
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.B8G8R8A8UNorm),
            };

        GpuPipelineDescription WithDepth(GpuDepthStencilState depth)
        {
            GpuPipelineDescription description = Description(
                ShaderSet(TableFor(GpuResourceKind.UniformBuffer)), Layout(GpuResourceKind.UniformBuffer));
            description.DepthStencil = depth;
            return description;
        }

        MetalShaderSet ShaderSet(MetalShaderIndexTable table, IDeviceLiveness? owner = null)
            => new(owner ?? _liveness,
                [
                    new MetalCompiledStage(MetalShaderStage.Vertex, default, default),
                    new MetalCompiledStage(MetalShaderStage.Fragment, default, default),
                ],
                table);

        IGpuResourceLayout[] Layout(params GpuResourceKind[] kinds) => LayoutOn(_liveness, kinds);

        static IGpuResourceLayout[] LayoutOn(IDeviceLiveness owner, params GpuResourceKind[] kinds)
        {
            var elements = new GpuResourceLayoutElement[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                elements[i] = new GpuResourceLayoutElement("e" + i, kinds[i], GpuShaderStages.Fragment);

            return [new MetalResourceLayout(owner, new GpuResourceLayoutDescription(elements))];
        }

        // A table whose FRAGMENT stage reads element 0 of one layout of the given kinds. The first kind has to be
        // a buffer kind, because the one emitted argument is a [[buffer(0)]].
        static MetalShaderIndexTable TableFor(params GpuResourceKind[] kinds)
            => MetalShaderIndexTable.Build(
                Reflected(kinds),
                [
                    new MetalMslStageJoin(MetalShaderStage.Fragment,
                        Spirv((Id: 70, Set: 0, Binding: 0)),
                        [new MetalMslArgument(MetalIndexSpace.Buffer, 0, "_70")]),
                ],
                "hand-built");

        static MetalShaderIndexTable VertexTableAt(int index)
            => MetalShaderIndexTable.Build(
                Reflected(GpuResourceKind.UniformBuffer),
                [
                    new MetalMslStageJoin(MetalShaderStage.Vertex,
                        Spirv((Id: 70, Set: 0, Binding: 0)),
                        [new MetalMslArgument(MetalIndexSpace.Buffer, index, "_70")]),
                ],
                "hand-built");

        static GpuResourceLayoutDescription[] Reflected(params GpuResourceKind[] kinds)
        {
            var elements = new GpuResourceLayoutElement[kinds.Length];
            for (int i = 0; i < kinds.Length; i++)
                elements[i] = new GpuResourceLayoutElement("e" + i, kinds[i], GpuShaderStages.Fragment);
            return [new GpuResourceLayoutDescription(elements)];
        }

        static byte[] Spirv(params (int Id, int Set, int Binding)[] resources)
        {
            const uint opDecorate = 71, decorationBinding = 33, decorationDescriptorSet = 34;
            var words = new List<uint> { 0x07230203, 0x00010000, 0, 1, 0 };

            foreach ((int id, int set, int binding) in resources)
            {
                words.AddRange(new[] { (4u << 16) | opDecorate, (uint)id, decorationDescriptorSet, (uint)set });
                words.AddRange(new[] { (4u << 16) | opDecorate, (uint)id, decorationBinding, (uint)binding });
            }

            var bytes = new byte[words.Count * 4];
            for (int i = 0; i < words.Count; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), words[i]);
            return bytes;
        }
    }
}
