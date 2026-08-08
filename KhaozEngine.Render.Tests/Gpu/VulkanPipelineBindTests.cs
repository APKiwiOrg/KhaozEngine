using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE RECORD-TIME HALF OF ROW 13: <c>SetPipeline</c> and <c>SetComputePipeline</c> stop refusing, emit
    /// <c>vkCmdBindPipeline</c> and adopt the pipeline's own <c>VkPipelineLayout</c> in the matching bind records,
    /// which is clause 4 of section 6.2 (V-R6). Work-breakdown row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523).
    ///
    /// <para><b>THE INVALIDATION IS ASSERTED THROUGH THE FLUSH RATHER THAN THROUGH THE RECORDS.</b> Row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) already pins the prefix computation on its own, so
    /// what is left for this row to prove is the WIRING: that a real pipeline bind on a real command list drives
    /// it with the right two arguments. A test reading the records directly would pass with a
    /// <c>SetPipelineLayout</c> call that was never made, because a slot nothing dirtied also reads clean.</para>
    /// </summary>
    public sealed class VulkanPipelineBindTests
    {
        const string VertGlsl =
            "#version 450\nlayout(location=0) in vec3 P;\nvoid main(){gl_Position=vec4(P,1);}";
        const string FragGlsl = "#version 450\nlayout(location=0) out vec4 C;\nvoid main(){C=vec4(1);}";
        const string ComputeGlsl = "#version 450\nlayout(local_size_x=8) in;\nvoid main(){}";

        /// <summary>
        /// A GRAPHICS BIND EMITS ONE <c>vkCmdBindPipeline</c> AT THE GRAPHICS BIND POINT, on the buffer the list
        /// is recording into.
        /// </summary>
        [Fact]
        public void SetPipeline_EmitsOneBindAtTheGraphicsBindPoint()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var pipeline = (VulkanGraphicsPipeline)Graphics(fixture, owned, UniformShape("A"));

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetPipeline(pipeline);

                VulkanRecordedPipelineBind bind = Assert.Single(fixture.PipelineBinder.Binds);
                Assert.False(bind.Compute);
                Assert.Equal(pipeline.Handle, bind.Pipeline);
                Assert.NotEqual(0UL, bind.CommandBuffer);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A SWITCH TO A PIPELINE WITH A DIFFERENT LAYOUT INVALIDATES THE RECORDED SETS, which is the whole of
        /// clause 4 arriving at its first real caller: a set bound and flushed under the outgoing layout is
        /// re-bound after the switch, under the incoming one.
        /// </summary>
        [Fact]
        public void ASwitchToAnIncompatibleLayout_RebindsTheRecordedSets()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                GpuResourceLayoutDescription shape = UniformShape("A");
                var first = (VulkanGraphicsPipeline)Graphics(fixture, owned, shape);
                var second = (VulkanGraphicsPipeline)Graphics(fixture, owned, TextureShape());

                Assert.NotEqual(first.PipelineLayout, second.PipelineLayout);

                IGpuResourceSet set = fixture.CreateSetFor(shape, owned);

                var binds = new List<VulkanRecordedBind>();
                var sink = new VulkanCapturingCmdSink(binds);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetPipeline(first);
                list.SetGraphicsResourceSet(0, set);
                list.FlushGraphicsBinds(ref sink);

                Assert.Single(binds);
                Assert.Equal(first.PipelineLayout, binds[0].PipelineLayout);

                // Nothing moved, so a second flush issues nothing.
                list.FlushGraphicsBinds(ref sink);
                Assert.Single(binds);

                list.SetPipeline(second);
                list.FlushGraphicsBinds(ref sink);

                Assert.Equal(2, binds.Count);
                Assert.Equal(second.PipelineLayout, binds[1].PipelineLayout);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// TWO PIPELINES SHARING A LAYOUT BOTH BIND AND NEITHER INVALIDATES ANYTHING, which is decision V-D5
        /// paying out at the seat it was taken for. Without the content dedup every switch would force a full
        /// rebind of every set, which is the incumbent's behaviour and the cost section 2.4 declines to pay.
        /// </summary>
        [Fact]
        public void TwoPipelinesSharingALayout_BothBindAndNeitherInvalidates()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                GpuResourceLayoutDescription shape = UniformShape("A");
                var first = (VulkanGraphicsPipeline)Graphics(fixture, owned, shape);
                var second = (VulkanGraphicsPipeline)Graphics(fixture, owned, shape);

                Assert.Equal(first.PipelineLayout, second.PipelineLayout);
                Assert.NotEqual(first.Handle, second.Handle);

                IGpuResourceSet set = fixture.CreateSetFor(shape, owned);

                var binds = new List<VulkanRecordedBind>();
                var sink = new VulkanCapturingCmdSink(binds);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetPipeline(first);
                list.SetGraphicsResourceSet(0, set);
                list.FlushGraphicsBinds(ref sink);
                Assert.Single(binds);

                list.SetPipeline(second);
                list.FlushGraphicsBinds(ref sink);

                // Two DIFFERENT programs, so two pipeline binds, and nothing to rebind between them.
                Assert.Equal(2, fixture.PipelineBinder.Binds.Count);
                Assert.Single(binds);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A REBIND OF THE PIPELINE ALREADY CURRENT DOES NOTHING AT ALL, which is the fork's pipeline-identity
        /// guard kept. It is a stronger skip than the layout guard underneath it, and it is what a renderer that
        /// re-asserts its pipeline per draw relies on.
        /// </summary>
        [Fact]
        public void ARebindOfThePipelineAlreadyCurrent_DoesNothing()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var pipeline = (VulkanGraphicsPipeline)Graphics(fixture, owned, UniformShape("A"));

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetPipeline(pipeline);
                list.SetPipeline(pipeline);
                list.SetPipeline(pipeline);

                Assert.Single(fixture.PipelineBinder.Binds);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A <c>Begin</c> FORGETS BOTH BOUND PIPELINES, which section 6.1 lists among what a recording reset
        /// covers. A fresh <c>VkCommandBuffer</c> has no pipeline bound at either point, so a retained handle
        /// would let the next recording's first bind take the identity guard's redundant path and draw with
        /// whatever the driver's own state happened to hold.
        /// </summary>
        [Fact]
        public void ABegin_ForgetsBothBoundPipelines()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var graphics = (VulkanGraphicsPipeline)Graphics(fixture, owned, UniformShape("A"));
                var compute = (VulkanComputePipeline)Compute(fixture, owned);

                using VulkanCommandList list = fixture.CreateList();

                list.Begin();
                list.SetPipeline(graphics);
                list.SetComputePipeline(compute);
                list.End();

                list.Begin();
                list.SetPipeline(graphics);
                list.SetComputePipeline(compute);

                Assert.Equal(4, fixture.PipelineBinder.Binds.Count);
                Assert.Equal([false, true, false, true], fixture.PipelineBinder.Binds.Select(b => b.Compute));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A COMPUTE BIND GOES TO THE COMPUTE BIND POINT AND INTO THE COMPUTE RECORDS, and it never disturbs the
        /// graphics arm (V-C1). Two bind points, two dirty arrays, two pipeline layouts.
        /// </summary>
        [Fact]
        public void SetComputePipeline_BindsAtTheComputePointAndLeavesTheGraphicsArmAlone()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                GpuResourceLayoutDescription shape = UniformShape("A");
                var graphics = (VulkanGraphicsPipeline)Graphics(fixture, owned, shape);
                var compute = (VulkanComputePipeline)Compute(fixture, owned);

                IGpuResourceSet set = fixture.CreateSetFor(shape, owned);

                var binds = new List<VulkanRecordedBind>();
                var sink = new VulkanCapturingCmdSink(binds);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetPipeline(graphics);
                list.SetGraphicsResourceSet(0, set);
                list.FlushGraphicsBinds(ref sink);
                Assert.Single(binds);

                list.SetComputePipeline(compute);
                list.FlushGraphicsBinds(ref sink);

                Assert.Equal(2, fixture.PipelineBinder.Binds.Count);
                Assert.True(fixture.PipelineBinder.Binds[1].Compute);
                Assert.Equal(compute.Handle, fixture.PipelineBinder.Binds[1].Pipeline);

                // The graphics slot is untouched by a compute switch, so its flush issues nothing.
                Assert.Single(binds);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A COMPUTE BIND ENDS ANY PENDING RENDERING FIRST (V-A4, section 13). The pass here collected a clear and
        /// saw no draw, so ending it flushes that clear through a begin and end pair, which is what makes the end
        /// observable at all.
        /// </summary>
        [Fact]
        public void SetComputePipeline_EndsPendingRenderingFirst()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var compute = (VulkanComputePipeline)Compute(fixture, owned);

                IGpuTexture colour = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
                owned.Add(colour);

                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, colour);
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.ClearColorTarget(0, Color.White);

                Assert.Empty(fixture.RenderApi.Begins);

                list.SetComputePipeline(compute);

                Assert.Single(fixture.RenderApi.Begins);
                Assert.Equal(VulkanLoadOp.Clear, Assert.Single(fixture.RenderApi.Begins[0].Colour).LoadOp);
                Assert.False(list.Rendering.IsRendering);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>A REDUNDANT COMPUTE REBIND DOES NOT SPLIT A PASS EITHER, because the identity guard runs
        /// before the pass end rather than after it.</summary>
        [Fact]
        public void ARedundantComputeRebind_DoesNotSplitAPass()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var compute = (VulkanComputePipeline)Compute(fixture, owned);

                IGpuTexture colour = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
                owned.Add(colour);

                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, colour);
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetComputePipeline(compute);

                list.SetFramebuffer(framebuffer);
                list.PrepareDraw();
                Assert.True(list.Rendering.IsRendering);

                list.SetComputePipeline(compute);

                Assert.True(list.Rendering.IsRendering);
                Assert.Single(fixture.PipelineBinder.Binds);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A PIPELINE FROM ANOTHER BACKEND IS REFUSED BY NAME, and it is resolved BEFORE the identity guard, so
        /// the same mistake cannot pass silently on a second bind.
        /// </summary>
        [Fact]
        public void AForeignPipeline_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture();

            using VulkanCommandList list = fixture.CreateList();
            list.Begin();

            Assert.Contains("not created by the native Vulkan backend",
                Assert.Throws<ArgumentException>(() => list.SetPipeline(new ForeignPipeline())).Message,
                StringComparison.Ordinal);
            Assert.Contains("not created by the native Vulkan backend",
                Assert.Throws<ArgumentException>(
                    () => list.SetComputePipeline(new ForeignComputePipeline())).Message,
                StringComparison.Ordinal);

            Assert.Empty(fixture.PipelineBinder.Binds);
        }

        /// <summary>
        /// A BIND OUTSIDE A RECORDING IS REFUSED BY NAME rather than discarded, which is the asymmetry with a
        /// resource-set bind: that one touches only the list's own array, and this one emits a <c>vkCmd*</c>
        /// against a buffer <c>vkBeginCommandBuffer</c> has not seen, which is undefined behaviour rather than a
        /// no-op.
        /// </summary>
        [Fact]
        public void ABindOutsideARecording_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var pipeline = (VulkanGraphicsPipeline)Graphics(fixture, owned, UniformShape("A"));

                using VulkanCommandList list = fixture.CreateList();

                Assert.Contains("needs an open recording",
                    Assert.Throws<InvalidOperationException>(() => list.SetPipeline(pipeline)).Message,
                    StringComparison.Ordinal);

                Assert.Empty(fixture.PipelineBinder.Binds);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- fixtures ----

        static GpuResourceLayoutDescription UniformShape(string name)
            => new(new GpuResourceLayoutElement(name, GpuResourceKind.UniformBuffer, GpuShaderStages.Vertex));

        static GpuResourceLayoutDescription TextureShape()
            => new(
                new GpuResourceLayoutElement("Tex", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));

        static IGpuPipeline Graphics(VulkanResourceFixture fixture, List<IDisposable> owned,
            in GpuResourceLayoutDescription shape)
        {
            IGpuShaderSet shaders = fixture.Factory.CreateShadersFromSpirv(VertGlsl, FragGlsl);
            owned.Add(shaders);

            IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(shape);
            owned.Add(layout);

            IGpuPipeline pipeline = fixture.Factory.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                    GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [layout],
                ShaderSet = shaders,
                VertexLayouts = [],
                Outputs = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm),
            });

            owned.Add(pipeline);
            return pipeline;
        }

        static IGpuComputePipeline Compute(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuComputeShader shader = fixture.Factory.CreateComputeShaderFromSpirv(ComputeGlsl);
            owned.Add(shader);

            IGpuComputePipeline pipeline = fixture.Factory.CreateComputePipeline(
                new GpuComputePipelineDescription(shader));
            owned.Add(pipeline);

            return pipeline;
        }

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }

        sealed class ForeignPipeline : IGpuPipeline
        {
            public void Dispose() { }
        }

        sealed class ForeignComputePipeline : IGpuComputePipeline
        {
            public void Dispose() { }
        }
    }
}
