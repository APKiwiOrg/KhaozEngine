using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// HOW FAR THE DEPENDENT-DISPATCH HAZARD WALK REACHES
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/632), device-free. The third and last walk over
    /// <c>VulkanBindRecords</c>' per-slot array to be bounded by the bound pipeline layout: the bind flush was
    /// first (https://github.com/APKiwiOrg/KhaozEngine/issues/625), the draw recorder's image walk second
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626, pinned by <see cref="VulkanTransitionWalkTests"/>),
    /// and this one was left until the argument for it could be made on its own terms, because it decides whether
    /// a BARRIER is emitted rather than whether work is wasted.
    ///
    /// <para><b>THE SHAPE IS THE SAME ONE BOTH SIBLINGS WERE FIXED IN.</b> A compute pipeline switch to a layout
    /// declaring fewer sets leaves the dropped slots recording their sets on purpose, so the trip back rebinds
    /// them. Those slots named storage resources here: <c>NoteWrites</c> recorded them as written by a dispatch
    /// that cannot reach them, and <c>NeedsBarrier</c> then answered yes for a later dispatch that merely binds
    /// one.</para>
    ///
    /// <para><b>WHAT IT COST, AND WHY IT WAS NEVER A WRONG PICTURE.</b> Both errors ran the safe way: an extra
    /// <c>VulkanDispatchBarrier.ReadAfterWrite</c>, which is a global memory barrier and really did order every
    /// earlier write, and never a missing one. What it costs is the serialisation V-C2 exists to avoid, put back
    /// by a slot the dispatch cannot read or write. So both tests here assert an ABSENT barrier, and each one
    /// asserts a real dependency still gets its own in the same recording, which is the half a bound that reached
    /// too little would break.</para>
    /// </summary>
    public sealed class VulkanComputeHazardWalkTests
    {
        /// <summary>
        /// AN INDEPENDENT DISPATCH UNDER A SHORTER LAYOUT OWES NOTHING, which is the read half. A two-set compute
        /// pipeline writes a texture at each slot, a one-set pipeline replaces it, and slot 1 keeps its record so
        /// the trip back rebinds it. The next dispatch binds a texture nothing has written, and the stale slot is
        /// the only reason it was ever answered yes.
        /// </summary>
        [Fact]
        public void AnIndependentDispatchUnderAShorterLayout_GetsNoBarrier()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture first = Storage(fixture, owned);
                IGpuTexture dropped = Storage(fixture, owned);
                IGpuTexture unrelated = Storage(fixture, owned);

                using VulkanCommandList list = Recording(fixture, owned);

                // THE TWO-SET DISPATCH: both textures are written, and the second one only through the slot the
                // switch below drops.
                IGpuResourceSet firstSet = StorageSet(fixture, owned, first);
                IGpuResourceSet droppedSet = StorageSet(fixture, owned, dropped);
                Adopt(fixture, list.ComputeBinds, firstSet, droppedSet);
                list.SetComputeResourceSet(0, firstSet);
                list.SetComputeResourceSet(1, droppedSet);
                list.Dispatch(1, 1, 1);

                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);

                // THE ONE-SET DISPATCH: slot 1 still records the dropped set, and this dispatch cannot reach it.
                IGpuResourceSet unrelatedSet = StorageSet(fixture, owned, unrelated);
                Adopt(fixture, list.ComputeBinds, unrelatedSet);
                list.SetComputeResourceSet(0, unrelatedSet);
                list.Dispatch(1, 1, 1);

                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);

                // AND THE REAL DEPENDENCY IS STILL SEEN, through the slot this layout does declare.
                list.SetComputeResourceSet(0, StorageSet(fixture, owned, first));
                list.Dispatch(1, 1, 1);

                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND A DISPATCH UNDER A SHORTER LAYOUT WRITES ONLY WHAT IT CAN REACH, which is the write half and the
        /// one that outlives the barrier. The barrier CLEARS the written set, so a <c>NoteWrites</c> that walked
        /// past the limit put the dropped slot's resource straight back into a set the barrier had just emptied,
        /// and the next dispatch to bind that resource paid for a write no dispatch in this recording made.
        /// </summary>
        [Fact]
        public void ADispatchUnderAShorterLayout_RecordsNoWriteForTheSlotThatLayoutDropped()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture shared = Storage(fixture, owned);
                IGpuTexture dropped = Storage(fixture, owned);

                using VulkanCommandList list = Recording(fixture, owned);

                IGpuResourceSet sharedSet = StorageSet(fixture, owned, shared);
                IGpuResourceSet droppedSet = StorageSet(fixture, owned, dropped);
                Adopt(fixture, list.ComputeBinds, sharedSet, droppedSet);
                list.SetComputeResourceSet(0, sharedSet);
                list.SetComputeResourceSet(1, droppedSet);
                list.Dispatch(1, 1, 1);

                // THE SWITCH, AND THE ONE BARRIER THIS RECORDING REALLY OWES: the shorter layout binds what the
                // dispatch above wrote at slot 0, so the dependency is genuine and the barrier clears the set.
                IGpuResourceSet again = StorageSet(fixture, owned, shared);
                Adopt(fixture, list.ComputeBinds, again);
                list.SetComputeResourceSet(0, again);
                list.Dispatch(1, 1, 1);

                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
                Assert.Equal(1, list.Draws.Hazards.WrittenCount);

                // AND THE DROPPED SLOT'S TEXTURE IS NOT IN IT, so binding it next is an independent dispatch.
                list.SetComputeResourceSet(0, StorageSet(fixture, owned, dropped));
                list.Dispatch(1, 1, 1);

                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Storage buffers across dispatches and draws (in-list buffer visibility) ----

        /// <summary>
        /// A GRAPHICS READER OF A BUFFER AN EARLIER DISPATCH WROTE GETS ONE DEPENDENCY BARRIER BEFORE RENDERING
        /// REOPENS, even with an unrelated dispatch between the writer and the reader and with the reader's set
        /// bound before either dispatch. The roles are the brief's handles: <c>written</c> is 41, written by the
        /// first dispatch, <c>unrelated</c> is 42, written by the second, and <c>independent</c> is 43, which no
        /// dispatch writes. The fake factory hands out its own handle values, so the roles are named rather than
        /// numbered.
        /// <para>
        /// THE READER IS NOT DIRTY AT THE DRAW. Its set was bound and drawn with before the dispatches, so a walk
        /// over dirty slots would skip it. The walk covers every slot the bound layout declares.
        /// </para>
        /// </summary>
        [Fact]
        public void GraphicsReadAfterUnrelatedDispatchPublishesTheEarlierBuffer()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuBuffer written = StorageBuffer(fixture, owned);
                IGpuBuffer unrelated = StorageBuffer(fixture, owned);
                IGpuBuffer independent = StorageBuffer(fixture, owned);

                using VulkanCommandList list = Recording(fixture, owned);

                // THE ALREADY-BOUND GRAPHICS READ-ONLY VIEW, and the open rendering scope its first draw opens.
                IGpuResourceSet reader = ReaderSet(fixture, owned, written);
                Adopt(fixture, list.GraphicsBinds, reader);
                list.SetGraphicsResourceSet(0, reader);
                list.Draw(3);
                Assert.True(list.Rendering.IsRendering);

                IGpuResourceSet writer = WriterSet(fixture, owned, written);
                Adopt(fixture, list.ComputeBinds, writer);
                list.SetComputeResourceSet(0, writer);
                list.Dispatch(1, 1, 1);
                list.SetComputeResourceSet(0, WriterSet(fixture, owned, unrelated));
                list.Dispatch(1, 1, 1);
                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);

                fixture.Trace.Clear();
                list.Draw(3);

                int dependencyBarrierCount = fixture.DrawEmitter.DependencyBarrierCount;
                int barrier = fixture.Trace.IndexOf("DependencyBarrier()");
                int begin = fixture.Trace.FindIndex(t => t.StartsWith("BeginRendering", StringComparison.Ordinal));
                int draw = fixture.Trace.IndexOf("Draw(3,1)");
                bool barrierBeforeRenderingBegin = barrier >= 0 && begin > barrier && draw > begin;

                // AN INDEPENDENT READER AFTER A FRESH UNRELATED WRITE OWES NOTHING.
                list.SetComputeResourceSet(0, WriterSet(fixture, owned, unrelated));
                list.Dispatch(1, 1, 1);
                list.SetGraphicsResourceSet(0, ReaderSet(fixture, owned, independent));
                list.Draw(3);
                int independentReaderBarrierCount = fixture.DrawEmitter.DependencyBarrierCount - dependencyBarrierCount;

                MemoryBarrier2 dependency = VulkanDispatchBarrier.ReadAfterWrite;
                const PipelineStageFlags2 Stages = PipelineStageFlags2.ComputeShaderBit
                    | PipelineStageFlags2.AllGraphicsBit;
                const AccessFlags2 Accesses = AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit;

                Assert.Equal(1, dependencyBarrierCount);
                Assert.True(barrierBeforeRenderingBegin, string.Join(" | ", fixture.Trace));
                Assert.Equal(0, independentReaderBarrierCount);
                Assert.Equal(Stages, dependency.DstStageMask & Stages);
                Assert.Equal(Accesses, dependency.DstAccessMask & Accesses);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A PIPELINE THAT DROPS A SET AND A LATER ONE THAT RESTORES IT. The set reading the written buffer sits
        /// at slot 1, a one-set pipeline draws while it is out of reach and owes nothing, and the restored
        /// two-set pipeline's draw finds rendering open, ends it, emits the dependency, reopens, and only then
        /// binds the restored set and draws. That order is what makes the restored reader observe the written
        /// value: the bind of the restored set and the draw reading it both land after the barrier.
        /// </summary>
        [Fact]
        public void APipelineDroppingAndRestoringASetPublishesTheWriteToTheRestoredReader()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuBuffer written = StorageBuffer(fixture, owned);
                IGpuBuffer independent = StorageBuffer(fixture, owned);

                using VulkanCommandList list = Recording(fixture, owned);

                IGpuResourceSet kept = ReaderSet(fixture, owned, independent);
                IGpuResourceSet restored = ReaderSet(fixture, owned, written);
                Adopt(fixture, list.GraphicsBinds, kept, restored);
                list.SetGraphicsResourceSet(0, kept);
                list.SetGraphicsResourceSet(1, restored);

                IGpuResourceSet writer = WriterSet(fixture, owned, written);
                Adopt(fixture, list.ComputeBinds, writer);
                list.SetComputeResourceSet(0, writer);
                list.Dispatch(1, 1, 1);

                // DROPPED: the one-set layout cannot reach slot 1, so this draw owes nothing and opens rendering.
                Adopt(fixture, list.GraphicsBinds, kept);
                list.Draw(3);
                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);
                Assert.True(list.Rendering.IsRendering);

                // RESTORED: slot 1 is back in reach without a rebind from the caller.
                Adopt(fixture, list.GraphicsBinds, kept, restored);
                fixture.Trace.Clear();
                int bindsBefore = fixture.DrawEmitter.DescriptorBinds.Count;
                list.Draw(3);

                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
                int end = fixture.Trace.IndexOf("EndRendering");
                int barrier = fixture.Trace.IndexOf("DependencyBarrier()");
                int begin = fixture.Trace.FindIndex(t => t.StartsWith("BeginRendering", StringComparison.Ordinal));
                int draw = fixture.Trace.IndexOf("Draw(3,1)");
                Assert.True(end >= 0 && barrier > end && begin > barrier && draw > begin,
                    string.Join(" | ", fixture.Trace));

                ulong restoredHandle = ((VulkanResourceSet)restored).DescriptorSet;
                bool restoredBound = false;
                for (int i = bindsBefore; i < fixture.DrawEmitter.DescriptorBinds.Count; i++)
                {
                    VulkanRecordedBind bind = fixture.DrawEmitter.DescriptorBinds[i];
                    for (int s = 0; s < bind.Sets.Length; s++)
                        restoredBound |= bind.FirstSet + s == 1 && bind.Sets[s] == restoredHandle;
                }
                Assert.True(restoredBound, "the restored draw did not bind the restored set at slot 1");
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A READ-ONLY BINDING IS A READ, NOT A WRITE, so a later reader of the same buffer owes nothing. A
        /// later WRITER still owes the barrier, because the earlier dispatch's read has to finish before the
        /// write lands. Counting read-only bindings as writes was the old conservative answer, and dropping it
        /// must not drop that write-after-read edge.
        /// </summary>
        [Fact]
        public void AReadOnlyDispatchBindingOrdersALaterWriterButNotALaterReader()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuBuffer shared = StorageBuffer(fixture, owned);

                using VulkanCommandList list = Recording(fixture, owned);

                IGpuResourceSet computeReader = ReaderSet(fixture, owned, shared, GpuShaderStages.Compute);
                Adopt(fixture, list.ComputeBinds, computeReader);
                list.SetComputeResourceSet(0, computeReader);
                list.Dispatch(1, 1, 1);
                Assert.Equal(0, list.Draws.Hazards.WrittenCount);

                IGpuResourceSet graphicsReader = ReaderSet(fixture, owned, shared);
                Adopt(fixture, list.GraphicsBinds, graphicsReader);
                list.SetGraphicsResourceSet(0, graphicsReader);
                list.Draw(3);
                Assert.Equal(0, fixture.DrawEmitter.DependencyBarrierCount);

                IGpuResourceSet writer = WriterSet(fixture, owned, shared);
                Adopt(fixture, list.ComputeBinds, writer);
                list.SetComputeResourceSet(0, writer);
                list.Dispatch(1, 1, 1);
                Assert.Equal(1, fixture.DrawEmitter.DependencyBarrierCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Fixtures, the same ones VulkanDrawPathTests and VulkanTransitionWalkTests use ----

        // A recording with an offscreen framebuffer bound. The colour target is sampled by nothing here, so its
        // own transitions stay out of everything these tests count.
        static VulkanCommandList Recording(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuTexture colour = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
            owned.Add(colour);

            IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, colour);
            owned.Add(framebuffer);

            VulkanCommandList list = fixture.CreateList();
            list.Begin();
            list.SetFramebuffer(framebuffer);
            fixture.Trace.Clear();
            return list;
        }

        static IGpuTexture Storage(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Storage));
            owned.Add(texture);
            return texture;
        }

        static IGpuResourceSet StorageSet(VulkanResourceFixture fixture, List<IDisposable> owned,
            IGpuTexture texture)
        {
            IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("T", GpuResourceKind.TextureReadWrite, GpuShaderStages.Compute)));
            owned.Add(layout);

            IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, texture));
            owned.Add(set);
            return set;
        }

        static IGpuBuffer StorageBuffer(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuBuffer buffer = fixture.Factory.CreateBuffer(
                VulkanResourceFixture.Buffer(256, GpuBufferUsage.StructuredBufferReadWrite));
            owned.Add(buffer);
            return buffer;
        }

        static IGpuResourceSet WriterSet(VulkanResourceFixture fixture, List<IDisposable> owned, IGpuBuffer buffer)
            => BufferSet(fixture, owned, buffer, GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute);

        static IGpuResourceSet ReaderSet(VulkanResourceFixture fixture, List<IDisposable> owned, IGpuBuffer buffer,
            GpuShaderStages stages = GpuShaderStages.Vertex)
            => BufferSet(fixture, owned, buffer, GpuResourceKind.StructuredBufferReadOnly, stages);

        static IGpuResourceSet BufferSet(VulkanResourceFixture fixture, List<IDisposable> owned, IGpuBuffer buffer,
            GpuResourceKind kind, GpuShaderStages stages)
        {
            IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(new GpuResourceLayoutElement("B", kind, stages)));
            owned.Add(layout);

            IGpuResourceSet set = fixture.Factory.CreateResourceSet(new GpuResourceSetDescription(layout, buffer));
            owned.Add(set);
            return set;
        }

        // THE PIPELINE LAYOUT A DISPATCH RUNS UNDER, adopted directly rather than through a whole VkPipeline,
        // exactly as the two sibling suites do it. The set count is what these tests are about, so it takes as
        // many sets as the caller names.
        static void Adopt(VulkanResourceFixture fixture, VulkanBindRecords records, params IGpuResourceSet[] sets)
        {
            var handles = new ulong[sets.Length];
            int dynamicUniforms = 0;

            for (int i = 0; i < sets.Length; i++)
            {
                VulkanResourceLayout layout = ((VulkanResourceSet)sets[i]).Layout;
                handles[i] = layout.SetLayout;
                dynamicUniforms += layout.DynamicUniformCount;
            }

            records.SetPipelineLayout(
                fixture.Descriptors.PipelineLayouts.GetOrCreate(handles, dynamicUniforms), handles);
        }

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }
}
