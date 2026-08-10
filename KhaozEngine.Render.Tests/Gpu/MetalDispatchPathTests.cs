using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// WHAT A DISPATCH ACTUALLY EMITS, DEVICE-FREE, and it is a different subject from a draw rather than a
    /// smaller one. Work-breakdown row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/580), section 6.3 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHY IT IS ITS OWN FILE.</b> A dispatch opens a COMPUTE encoder, which is a different Objective-C
    /// protocol with unprefixed argument-table selectors, it flushes a different set of bind records, and it
    /// carries a number no draw has (the threadgroup size, which only this API needs at the call). The one thing
    /// it shares with <see cref="MetalDrawPathTests"/> is the render pass it has to END, and that is asserted
    /// here because ending it is the dispatch's act. The framebuffer stand-in it binds
    /// (<see cref="RecordedFramebuffer"/>) is declared over there and shared.</para>
    ///
    /// <para><b>EVERY CLAIM HERE IS A DECISION RATHER THAN A DRIVER CALL.</b> A dispatch that flushed the
    /// GRAPHICS records would mark a draw's resources clean against a compute encoder that never received them,
    /// which is a corruption one frame removed from its cause. A threadgroup size taken from the caller instead
    /// of the kernel leaves a compute result partly written rather than failing. A pipeline state not re-set
    /// after a boundary runs the previous kernel. None of that is visible in a golden.</para>
    /// </summary>
    public sealed class MetalDispatchPathTests : IDisposable
    {
        /// <inheritdoc/>
        public void Dispose()
        {
            foreach (MetalCommandList list in _lists) list.Dispose();
            _harness.Dispose();
        }

        readonly MetalRingHarness _harness = new();
        readonly List<MetalCommandList> _lists = new();

        // THE KERNEL, whose workgroup size is read out of the SPIR-V module by the shipped path rather than
        // declared alongside it, which is what makes the threadgroup-size row a statement about where the number
        // CAME FROM. Built once for the assembly, because the cross-compile is the expensive part.
        const string KernelGlsl = @"#version 450
layout(local_size_x = 8, local_size_y = 4, local_size_z = 2) in;
layout(set = 0, binding = 0) uniform Params { vec4 Tint; };
layout(set = 0, binding = 1) buffer Out { vec4 Values[]; };
void main() { Values[gl_GlobalInvocationID.x] = Tint; }
";

        static readonly (MetalMslProgram Program, uint X, uint Y, uint Z) Kernel =
            MetalShaderBuild.Compute(KernelGlsl, "MetalDispatchPathKernel");

        // The graphics table, for the two rows that need a draw to put a render encoder in the way.
        static readonly MetalShaderIndexTable GraphicsTable = MetalBindProgram.Table();

        /// <summary>
        /// A DISPATCH OPENS A COMPUTE ENCODER, AND OPENING IT IS WHAT ENDS THE RENDER PASS (M-A5). Nothing in the
        /// dispatch path enforces that: <see cref="MetalEncoderScope.EnsureComputeEncoder"/>'s first act is to
        /// end whatever is open, so the invariant belongs to the one owner of every transition and a second copy
        /// of it here is what row 12 recorded the decision to prevent. This row is what makes the decision
        /// observable rather than merely written down.
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> A compute encoder was asked for while a render encoder was still
        /// open, which Metal refuses outright, or the dispatch was encoded into the render encoder, which is not
        /// a call that exists.</para>
        /// </summary>
        [Fact]
        public void ADispatchOpensAComputeEncoderAndThatEndsTheOpenRenderPass()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, _) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.Draw(3);
            Assert.Equal(MetalEncoderKind.Render, list.Encoders.Open);

            list.SetComputePipeline(ComputePipeline());
            list.Dispatch(2, 3, 4);

            Assert.Equal(MetalEncoderKind.Compute, list.Encoders.Open);

            int ended = At(calls.Log, "end Render");
            int began = At(calls.Log, "begin Compute");
            Assert.True(ended >= 0 && ended < began, "the render encoder is ended before the compute one opens");

            Assert.NotEqual(calls.Draws[0].Encoder, calls.Dispatches[0].Encoder);
            Assert.Equal(list.Encoders.Current, calls.Dispatches[0].Encoder);

            list.End();
        }

        /// <summary>
        /// THE COMPUTE PIPELINE STATE IS SET ONCE PER (PIPELINE, ENCODER), AND THE DISPATCH FLUSHES THE COMPUTE
        /// BINDS ALONE. Both halves are M-R8 and M-R4 arriving on the compute side, and the second is the arm
        /// most easily lost: the graphics and compute records are separate on this backend because they reach
        /// different encoders, so a dispatch that flushed the graphics records would write a draw's resources
        /// into a compute encoder's argument table AND mark them clean.
        ///
        /// <para><b>WHAT A RED RUN MEANS.</b> Either a redundant <c>-setComputePipelineState:</c> per dispatch
        /// (a cost nothing else can see), or none after a boundary (a dispatch running the previous kernel), or
        /// a graphics record cleaned by a flush that never wrote a render encoder, which is the corruption one
        /// frame removed from its cause.</para>
        /// </summary>
        [Fact]
        public void ADispatchSetsItsPipelineOncePerEncoderAndFlushesOnlyTheComputeBinds()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, FakeMetalComputeApi compute) = NewList();

            list.Begin();
            list.SetFramebuffer(new RecordedFramebuffer());
            list.SetPipeline(Pipeline());
            list.GraphicsBinds.Record(0, MetalBindProgram.Set(_harness), 0);
            list.SetVertexBuffer(0, _harness.NewBuffer(256, GpuBufferUsage.VertexBuffer));

            list.SetComputePipeline(ComputePipeline());
            list.ComputeBinds.Record(0, MetalBindProgram.Set(_harness), 0);

            list.Dispatch(1, 1, 1);
            list.Dispatch(1, 1, 1);

            // ONCE, for two dispatches into one encoder.
            Assert.Single(compute.States);
            Assert.Equal(list.Encoders.Current, compute.States[0].Encoder);
            Assert.Equal(2, calls.Dispatches.Count);

            // ONLY THE COMPUTE STAGE WAS WRITTEN, and the graphics records still owe every bind they did.
            Assert.NotEmpty(calls.ArrayWrites);
            Assert.All(calls.ArrayWrites, write => Assert.Equal(MetalShaderStage.Compute, write.Stage));
            Assert.False(list.ComputeBinds.IsDirty(0));
            Assert.True(list.GraphicsBinds.IsDirty(0));
            Assert.True(list.VertexStreams.IsDirty(0));

            // AND A BOUNDARY PUTS THE PIPELINE STATE BACK, because it is encoder state exactly as the graphics
            // block is. The boundary here is a draw, since opening the render encoder ends the compute one.
            list.Draw(3);
            list.Dispatch(1, 1, 1);

            Assert.Equal(2, compute.States.Count);
            Assert.NotEqual(compute.States[0].Encoder, compute.States[1].Encoder);

            list.End();
        }

        /// <summary>
        /// THE THREADGROUP SIZE COMES OFF THE COMPILED KERNEL AND NOT OFF THE CALLER. Metal is the one backend
        /// that needs the number at the dispatch, where Direct3D 11 and Vulkan read it out of the compiled
        /// module, so row 9's <c>SpirvLocalSize</c> travels here through <c>MetalComputePipeline.Shader</c>. The
        /// incumbent takes it from <c>ComputePipelineDescription.ThreadGroupSize*</c> instead, which validates
        /// nothing against the shader at all.
        ///
        /// <para><b>THE GROUP COUNTS ARE DELIBERATELY DIFFERENT NUMBERS FROM THE GROUP SIZE, AND ALL THREE AXES
        /// DIFFER FROM EACH OTHER</b>, so this cannot pass on a backend that hands the caller's counts through
        /// twice or that transposes the axes. A wrong group size dispatches the wrong number of threads, which
        /// leaves a compute result partly written rather than failing.</para>
        /// </summary>
        [Fact]
        public void ADispatchCarriesTheKernelsOwnThreadgroupSize()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, _) = NewList();

            // The premise, read off the module rather than assumed: the GLSL declares 8 by 4 by 2.
            Assert.Equal((8u, 4u, 2u), (Kernel.X, Kernel.Y, Kernel.Z));

            list.Begin();
            list.SetComputePipeline(ComputePipeline());
            list.Dispatch(2, 3, 5);

            Assert.Equal(new FakeMetalDispatchCall(2, 3, 5, 8, 4, 2), calls.Dispatches[0].Call);

            list.End();
        }

        /// <summary>
        /// A DISPATCH WITH NO COMPUTE PIPELINE BOUND IS REFUSED BY NAME. Metal takes the threadgroup size as an
        /// ARGUMENT rather than reading it out of the bound kernel the way the other two backends do, so without
        /// a pipeline this backend does not know how many threads a group has, quite apart from having no kernel
        /// to run.
        /// </summary>
        [Fact]
        public void ADispatchWithNoComputePipelineIsRefusedByName()
        {
            (MetalCommandList list, FakeMetalEncoderCalls calls, _, FakeMetalComputeApi compute) = NewList();

            list.Begin();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => list.Dispatch(1, 1, 1));
            Assert.Contains("no compute pipeline bound", thrown.Message, StringComparison.Ordinal);

            // AND NOTHING WAS SPENT ON IT: the refusal comes before the encoder, so a dispatch that cannot happen
            // does not also cost a boundary and leave a compute encoder open behind the throw.
            Assert.Equal(0, calls.EncoderBoundaries);
            Assert.Equal(MetalEncoderKind.None, list.Encoders.Open);
            Assert.Empty(compute.States);

            list.End();
        }

        // ---- fixtures --------------------------------------------------------------------------------------

        // THE POSITION OF A LOG LINE, so a row can assert an ORDER rather than a count. Answers -1 for a line
        // that is not there, which fails the comparisons above rather than passing them.
        static int At(IReadOnlyList<string> log, string prefix)
        {
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].StartsWith(prefix, StringComparison.Ordinal)) return i;
            }

            return -1;
        }

        (MetalCommandList List, FakeMetalEncoderCalls Calls, FakeMetalRenderCalls Render,
            FakeMetalComputeApi Compute) NewList()
        {
            FakeMetalEncoderCalls calls = new();
            FakeMetalRenderCalls render = new();
            FakeMetalComputeApi compute = new();

            MetalCommandList list = _harness.NewList(
                new object(), calls: calls, render: render, compute: compute);

            _lists.Add(list);
            return (list, calls, render, compute);
        }

        // The compute pipeline, over the real kernel table and the real workgroup size read out of its module.
        MetalComputePipeline ComputePipeline()
            => new(_harness.Liveness,
                new MetalComputeShader(
                    _harness.Liveness, default, Kernel.Program.Table, Kernel.X, Kernel.Y, Kernel.Z),
                [],
                default);

        // A graphics pipeline with nil handles, only ever bound here to put a render encoder in a dispatch's way.
        // Deliberately a second copy of MetalDrawPathTests' own rather than a shared helper: the two files assert
        // different subjects, and a fixture shared between them would be a reason to keep them in step.
        MetalGraphicsPipeline Pipeline()
        {
            MetalShaderSet shaders = new(
                _harness.Liveness,
                [
                    new MetalCompiledStage(MetalShaderStage.Vertex, default, default),
                    new MetalCompiledStage(MetalShaderStage.Fragment, default, default),
                ],
                GraphicsTable);

            var layouts = new IGpuResourceLayout[GraphicsTable.Layouts.Count];
            for (int i = 0; i < layouts.Length; i++)
            {
                layouts[i] = new MetalResourceLayout(_harness.Liveness, GraphicsTable.Layouts[i]);
            }

            var description = new GpuPipelineDescription
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

            return new MetalGraphicsPipeline(
                _harness.Liveness, MetalGraphicsPipelinePlan.Build(_harness.Liveness, description),
                default, default);
        }
    }
}
