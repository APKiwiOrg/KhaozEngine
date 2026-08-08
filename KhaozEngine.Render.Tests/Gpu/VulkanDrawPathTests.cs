using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DRAW AND DISPATCH PATH, DEVICE-FREE: the pre-command ORDER, the vertex and index bind schedule, the
    /// compute rule 1 barrier and the dependent-dispatch barrier. Work-breakdown row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525), decisions V-A2, V-A4, V-C1 and V-C2.
    ///
    /// <para><b>THE ORDER IS THE THING THAT CAN BE WRONG, so most of what is asserted here is a SEQUENCE</b>
    /// rather than a count. Every fake the fixture hands out appends to one trace, so "the image transitions came
    /// before the render pass began" is a real assertion rather than two call logs that cannot be compared. That
    /// is not a nicety: a barrier recorded INSIDE a dynamic-rendering instance is a different and much narrower
    /// call than the one section 10.3's table describes, and nothing about getting it wrong is loud.</para>
    ///
    /// <para><b>WHAT IS DELIBERATELY NOT HERE.</b> The per-draw call BUDGET is
    /// <see cref="VulkanBindBudgetTests"/>'s (MV4), which drives the same members through the counting emitter.
    /// The layout tracker's own arithmetic is <c>VulkanLayoutTrackerTests</c>'s, and the copy regions are
    /// <see cref="VulkanTransferPathTests"/>'s.</para>
    /// </summary>
    public sealed class VulkanDrawPathTests
    {
        // ---- The pre-command order ----

        /// <summary>
        /// A DRAW OPENS THE RENDER PASS INSTANCE, EMITS ITS GEOMETRY BINDS AND ITS DESCRIPTOR BINDS, AND THEN
        /// DRAWS, in that order. This is the whole of <see cref="VulkanDrawRecorder"/>'s contract on the graphics
        /// arm, and every one of the four steps is a step a five-member copy-paste could have dropped.
        /// </summary>
        [Fact]
        public void ADraw_BeginsTheRenderPassThenBindsGeometryThenBindsSetsThenDraws()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);

                list.SetVertexBuffer(0, Vertices(fixture, owned));
                list.Draw(3);

                string[] trace = fixture.Trace.ToArray();
                Assert.Equal(
                    ["BeginRendering(64x64,colour=1,depth=none)", "SetViewport(y=64,height=-64)",
                        "SetScissor(0,0,64,64)", "BindVertexBuffers(first=0,count=1)", "Draw(3,1)"],
                    trace);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND A DISPATCH ENDS THE PENDING INSTANCE FIRST (V-A4), which is the one real difference between the two
        /// arms. Every command illegal inside a render pass instance goes through the same helper, so this is
        /// where the dispatch's use of it is pinned.
        /// </summary>
        [Fact]
        public void ADispatch_EndsThePendingRenderPassFirst()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);

                list.Draw(3);
                Assert.True(list.Rendering.IsRendering);

                list.Dispatch(2, 1, 1);

                Assert.False(list.Rendering.IsRendering);
                Assert.Equal(1, fixture.RenderApi.EndCount);
                Assert.Contains("Dispatch(2,1,1)", fixture.Trace);
                Assert.True(fixture.Trace.IndexOf("EndRendering") < fixture.Trace.IndexOf("Dispatch(2,1,1)"));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>EIGHT INSTANCES IS STILL ONE <c>vkCmdDraw</c> AND ONE OF EVERY OTHER CALL, which is the trace
        /// identity MV4 freezes: the instance count is an argument and influences nothing above it.</summary>
        [Fact]
        public void EightInstances_ChangeNothingButTheDrawsOwnArgument()
        {
            Assert.Equal(TraceOfOneDraw(1).Skip(0).ToArray(), TraceOfOneDraw(8).Select(Deinstance).ToArray());
        }

        // ---- The vertex and index bind schedule ----

        /// <summary>
        /// A REBIND OF WHAT IS ALREADY RECORDED EMITS NOTHING AT ALL, buffer and offset both. This is the guard
        /// the incumbent does not have: it issues <c>vkCmdBindVertexBuffers</c> inside its own
        /// <c>SetVertexBufferCore</c> with no comparison, so a renderer that rebinds one mesh's buffer before each
        /// of its draws pays a native call per draw for a state change that did not happen.
        /// </summary>
        [Fact]
        public void RebindingTheSameVertexBuffer_EmitsNothingAtAll()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);
                IGpuBuffer vertices = Vertices(fixture, owned);

                list.SetVertexBuffer(0, vertices);
                list.Draw(3);
                list.SetVertexBuffer(0, vertices);
                list.Draw(3);

                Assert.Single(fixture.DrawEmitter.VertexBinds);
                Assert.Equal(2, fixture.DrawEmitter.Draws.Count);

                // AND A MOVED OFFSET IS NOT A REDUNDANT BIND, which is the half a buffer-only compare would miss.
                list.SetVertexBuffer(0, vertices, 64);
                list.Draw(3);

                Assert.Equal(2, fixture.DrawEmitter.VertexBinds.Count);
                Assert.Equal(64ul, fixture.DrawEmitter.VertexBinds[1].Offsets[0]);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// TWO ADJACENT SLOTS ARE ONE CALL AND A GAP CUTS THE RUN, which is the descriptor flush's own law applied
        /// to the other bind class. <c>vkCmdBindVertexBuffers</c> takes a DENSE array from
        /// <c>firstBinding</c>, so a slot nothing bound cannot be skipped inside one call and has to end the run
        /// instead.
        /// </summary>
        [Fact]
        public void AdjacentSlotsAreOneCall_AndAGapCutsTheRun()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);

                list.SetVertexBuffer(0, Vertices(fixture, owned));
                list.SetVertexBuffer(1, Vertices(fixture, owned));
                list.SetVertexBuffer(3, Vertices(fixture, owned));
                list.Draw(3);

                Assert.Equal(2, fixture.DrawEmitter.VertexBinds.Count);
                Assert.Equal(0u, fixture.DrawEmitter.VertexBinds[0].FirstBinding);
                Assert.Equal(2, fixture.DrawEmitter.VertexBinds[0].Buffers.Length);
                Assert.Equal(3u, fixture.DrawEmitter.VertexBinds[1].FirstBinding);
                Assert.Single(fixture.DrawEmitter.VertexBinds[1].Buffers);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// THE INDEX BIND COMPARES THE ELEMENT WIDTH AS WELL AS THE BUFFER, because the same buffer read as
        /// 16-bit rather than 32-bit is a different bind and reading a 32-bit index buffer as 16-bit renders
        /// plausible garbage rather than throwing.
        /// </summary>
        [Fact]
        public void TheIndexBind_ComparesTheElementWidthToo()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);
                IGpuBuffer indices = fixture.Factory.CreateBuffer(
                    VulkanResourceFixture.Buffer(256, GpuBufferUsage.IndexBuffer));
                owned.Add(indices);

                list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
                list.DrawIndexed(3, 1, 0, 0, 0);
                list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
                list.DrawIndexed(3, 1, 0, 0, 0);

                Assert.Single(fixture.DrawEmitter.IndexBinds);
                Assert.True(fixture.DrawEmitter.IndexBinds[0].SixteenBit);

                list.SetIndexBuffer(indices, GpuIndexFormat.UInt32);
                list.DrawIndexed(3, 1, 0, 0, 0);

                Assert.Equal(2, fixture.DrawEmitter.IndexBinds.Count);
                Assert.False(fixture.DrawEmitter.IndexBinds[1].SixteenBit);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A BUFFER CREATED WITHOUT THE USAGE IS REFUSED BY NAME. The native <c>VkBuffer</c> carries only the
        /// usage bits its description asked for, so binding a uniform buffer as vertex data is a validation error
        /// on a machine with the layer and undefined behaviour on one without.
        /// </summary>
        [Fact]
        public void AVertexBindOfABufferWithoutTheUsage_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);
                IGpuBuffer uniform = fixture.Factory.CreateBuffer(
                    VulkanResourceFixture.Buffer(256, GpuBufferUsage.UniformBuffer));
                owned.Add(uniform);

                ArgumentException refused = Assert.Throws<ArgumentException>(
                    () => list.SetVertexBuffer(0, uniform));

                Assert.Contains("GpuBufferUsage.VertexBuffer", refused.Message, StringComparison.Ordinal);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A BEGIN FORGETS EVERY GEOMETRY BIND, because a fresh <c>VkCommandBuffer</c> has no vertex buffer at any
        /// binding. A retained record would let the next recording's first bind take the identity guard's
        /// redundant path and draw out of whatever the driver's own state held.
        /// </summary>
        [Fact]
        public void ABegin_ForgetsEveryGeometryBind()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out IGpuFramebuffer framebuffer);
                IGpuBuffer vertices = Vertices(fixture, owned);

                list.SetVertexBuffer(0, vertices);
                list.Draw(3);
                list.End();

                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.SetVertexBuffer(0, vertices);
                list.Draw(3);

                Assert.Equal(2, fixture.DrawEmitter.VertexBinds.Count);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Compute rule 1 ----

        /// <summary>
        /// DECISION V-C1, WHOLE: a dispatch that binds a <c>Storage | Sampled</c> texture moves it to
        /// <c>GENERAL</c>, and the next draw whose set SAMPLES it moves it back to
        /// <c>SHADER_READ_ONLY_OPTIMAL</c>. That second barrier IS the rule 1 handoff, and it is a REAL image
        /// barrier at the sampled bind rather than the incumbent's queued layout restore armed by a usage flag.
        ///
        /// <para><b>AND IT IS EMITTED BEFORE THE RENDER PASS BEGINS</b>, which the trace pins. A barrier inside a
        /// dynamic-rendering instance is a different call, and the incumbent drains its own queued restores before
        /// <c>EnsureRenderPassActive</c> for exactly this reason.</para>
        /// </summary>
        [Fact]
        public void AStorageTextureWrittenByCompute_IsBarrieredBackAtTheSampledBindBeforeThePassOpens()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture map = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    16, 16, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
                owned.Add(map);

                using VulkanCommandList list = Recording(fixture, owned, out _);
                IGpuResourceSet storage = StorageSet(fixture, owned, map);
                Adopt(fixture, list.ComputeBinds, storage);
                list.SetComputeResourceSet(0, storage);
                list.Dispatch(1, 1, 1);

                ImageMemoryBarrier2[] toGeneral = fixture.Barriers.Barriers.ToArray();
                Assert.Single(toGeneral);
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, toGeneral[0].OldLayout);
                Assert.Equal(ImageLayout.General, toGeneral[0].NewLayout);

                fixture.Trace.Clear();
                IGpuResourceSet sampled = SampledSet(fixture, owned, map);
                Adopt(fixture, list.GraphicsBinds, sampled);
                list.SetGraphicsResourceSet(0, sampled);
                list.Draw(3);

                ImageMemoryBarrier2 back = fixture.Barriers.Barriers[^1];
                Assert.Equal(ImageLayout.General, back.OldLayout);
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, back.NewLayout);
                Assert.True(
                    fixture.Trace.FindIndex(t => t.StartsWith("PipelineBarrier2", StringComparison.Ordinal))
                    < fixture.Trace.FindIndex(t => t.StartsWith("BeginRendering", StringComparison.Ordinal)));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND TWO DRAWS THAT TOUCH NO NEW TEXTURE EMIT NO BARRIER BETWEEN THEM, which is V-T2's gated invariant
        /// and the reason the per-draw transition walk is affordable at all: the tracker emits nothing for an
        /// image already in the layout it is asked for, which every plain sampled texture is.
        /// </summary>
        [Fact]
        public void TwoDrawsTouchingNoNewTexture_EmitNoBarrierBetweenThem()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture albedo = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Sampled));
                owned.Add(albedo);

                using VulkanCommandList list = Recording(fixture, owned, out _);
                IGpuResourceSet material = SampledSet(fixture, owned, albedo);
                Adopt(fixture, list.GraphicsBinds, material);
                list.SetGraphicsResourceSet(0, material);
                list.Draw(3);

                int after = fixture.Barriers.CallCount;
                list.Draw(3);
                list.Draw(3);

                Assert.Equal(after, fixture.Barriers.CallCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Compute rule 2's evidence ----

        /// <summary>
        /// DECISION V-C2: a dispatch that BINDS what an earlier dispatch WROTE gets ONE read-after-write barrier
        /// before it, and two independent dispatches get none. The set of written resources is what makes that
        /// distinction, and a barrier per dispatch would serialise a run of unrelated ones.
        ///
        /// <para><b>THIS IS EVIDENCE FOR THE AUTOMATIC-HAZARD SEAM CAPABILITY</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/461) AND NOT A CONTRACT CHANGE. The seam's compute
        /// rule 2 is unchanged: a portable consumer still separates dependent dispatches with <c>End</c>,
        /// <c>Submit</c> and <c>WaitForIdle</c>, because the Veldrid legs need the drain.</para>
        /// </summary>
        [Fact]
        public void ADependentDispatch_GetsOneBarrierAndAnIndependentOneGetsNone()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture shared = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Storage));
                owned.Add(shared);
                IGpuTexture other = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Storage));
                owned.Add(other);

                using VulkanCommandList list = Recording(fixture, owned, out _);

                IGpuResourceSet first = StorageSet(fixture, owned, shared);
                Adopt(fixture, list.ComputeBinds, first);
                list.SetComputeResourceSet(0, first);
                list.Dispatch(1, 1, 1);
                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);

                // THE PING-PONG: the same resource bound again, so the second dispatch reads or overwrites what
                // the first wrote.
                list.SetComputeResourceSet(0, StorageSet(fixture, owned, shared));
                list.Dispatch(1, 1, 1);
                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);

                // AND AN UNRELATED ONE AFTERWARDS OWES NOTHING, because the barrier cleared the set and this
                // dispatch binds a resource nothing has written since.
                list.SetComputeResourceSet(0, StorageSet(fixture, owned, other));
                list.Dispatch(1, 1, 1);
                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// THE BARRIER NAMES BOTH STAGE MASKS AND BOTH ACCESS MASKS EXPLICITLY (V-F6), and its destination side
        /// carries the WRITE as well as the read: the ping-pong's second stage writes its own output, and a
        /// write-after-write on one resource is the same hazard with the same answer.
        /// </summary>
        [Fact]
        public void TheDependentDispatchBarrier_NamesEveryMaskAndOrdersWritesToo()
        {
            MemoryBarrier2 barrier = VulkanDispatchBarrier.ReadAfterWrite;

            Assert.Equal(PipelineStageFlags2.ComputeShaderBit, barrier.SrcStageMask);
            Assert.Equal(AccessFlags2.ShaderWriteBit, barrier.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.ComputeShaderBit, barrier.DstStageMask);
            Assert.Equal(AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit, barrier.DstAccessMask);
        }

        /// <summary>A <c>Begin</c> forgets the written set too, because those writes belonged to a recording
        /// nobody submitted.</summary>
        [Fact]
        public void ABegin_ForgetsTheWrittenResourceSet()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture shared = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Storage));
                owned.Add(shared);

                using VulkanCommandList list = Recording(fixture, owned, out IGpuFramebuffer framebuffer);
                IGpuResourceSet set = StorageSet(fixture, owned, shared);
                Adopt(fixture, list.ComputeBinds, set);
                list.SetComputeResourceSet(0, set);
                list.Dispatch(1, 1, 1);
                Assert.Equal(1, list.Draws.Hazards.WrittenCount);

                list.End();
                list.Begin();
                list.SetFramebuffer(framebuffer);

                Assert.Equal(0, list.Draws.Hazards.WrittenCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Fixtures ----

        // A recording with a framebuffer bound, which is the state every draw member needs.
        static VulkanCommandList Recording(VulkanResourceFixture fixture, List<IDisposable> owned,
            out IGpuFramebuffer framebuffer)
        {
            IGpuTexture colour = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
            owned.Add(colour);

            framebuffer = fixture.Factory.CreateFramebuffer(null, colour);
            owned.Add(framebuffer);

            VulkanCommandList list = fixture.CreateList();
            list.Begin();
            list.SetFramebuffer(framebuffer);

            // The framebuffer bind itself records nothing native, so the trace starts empty for the assertions
            // above.
            fixture.Trace.Clear();
            return list;
        }

        static IGpuBuffer Vertices(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.VertexBuffer));
            owned.Add(buffer);
            return buffer;
        }

        static IGpuResourceSet SampledSet(VulkanResourceFixture fixture, List<IDisposable> owned,
            IGpuTexture texture)
            => Set(fixture, owned, texture, GpuResourceKind.TextureReadOnly);

        static IGpuResourceSet StorageSet(VulkanResourceFixture fixture, List<IDisposable> owned,
            IGpuTexture texture)
            => Set(fixture, owned, texture, GpuResourceKind.TextureReadWrite);

        static IGpuResourceSet Set(VulkanResourceFixture fixture, List<IDisposable> owned, IGpuTexture texture,
            GpuResourceKind kind)
        {
            IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("T", kind, GpuShaderStages.Fragment)));
            owned.Add(layout);

            IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, texture));
            owned.Add(set);
            return set;
        }

        // THE PIPELINE LAYOUT A FLUSH BINDS UNDER, adopted directly rather than through a whole VkPipeline. Row
        // 13's SetPipeline is what does this on a real recording, and it is not what these tests are about: a
        // flush with no layout bound is refused by name, so every test that reaches a bind has to supply one.
        static void Adopt(VulkanResourceFixture fixture, VulkanBindRecords records, IGpuResourceSet set)
        {
            VulkanResourceLayout layout = ((VulkanResourceSet)set).Layout;
            ulong[] handles = [layout.SetLayout];

            records.SetPipelineLayout(
                fixture.Descriptors.PipelineLayouts.GetOrCreate(handles, layout.DynamicUniformCount), handles);
        }

        // One draw's whole trace at a given instance count.
        static string[] TraceOfOneDraw(uint instances)
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = Recording(fixture, owned, out _);
                list.SetVertexBuffer(0, Vertices(fixture, owned));
                list.Draw(3, instances, 0, 0);

                return fixture.Trace.ToArray();
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // The instance count is the ONE thing a draw's trace may differ by.
        static string Deinstance(string entry) => entry.StartsWith("Draw(", StringComparison.Ordinal)
            ? "Draw(3,1)"
            : entry;

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }
}
