using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// HOW FAR THE PER-COMMAND BOUND-IMAGE TRANSITION WALK REACHES
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/626), device-free. The sibling half of the bind flush's
    /// own reach (https://github.com/APKiwiOrg/KhaozEngine/issues/625), which
    /// <c>VulkanLayoutCompatibilityTests</c> pins: both walks stop at the bound pipeline layout's declared set
    /// count, and for the same reason read from opposite ends. The flush stops there because a bind past it is an
    /// INVALID call. This walk stops there because a transition past it is work for an image no command in flight
    /// can read.
    ///
    /// <para><b>WHAT THE OVER-BROAD WALK COST, IN THE TWO SHAPES IT COST IT IN.</b> A slot the current layout
    /// dropped still records a set, and its images were still asked for their binding's layout. Where that image
    /// was already resting there the tracker emitted nothing, which is why the shipped post chain showed no extra
    /// barrier and why this was never a wrong picture. Where it was NOT, the draw paid a barrier for an image it
    /// could not read, and, if the image was one the open pass had moved, an end and a begin as well, at EVERY
    /// draw of that pass rather than once.</para>
    ///
    /// <para><b>AND WHAT MUST NOT MOVE WITH IT.</b> The walk is over DECLARED slots, not dirty ones, which is a
    /// different bound entirely: a set bound before a dispatch that then moved one of its images is still bound at
    /// the next draw and still owes the rule 1 transition. The last test here is that property, and it is the one
    /// a bound written as "dirty" rather than "declared" would break silently.</para>
    /// </summary>
    public sealed class VulkanTransitionWalkTests
    {
        /// <summary>
        /// A SET THE BOUND LAYOUT DOES NOT DECLARE IS NOT TRANSITIONED FOR, which is the whole of #626. The shape
        /// is the shipped one #625 fixed the bind half of: a two-set pipeline records a material set at slot 1, a
        /// one-set post pipeline replaces it, and slot 1 keeps that record on purpose so the trip back rebinds it.
        /// A dispatch in between leaves the material set's storage texture in <c>GENERAL</c>, and the post draw
        /// then owed a <c>GENERAL</c> to <c>SHADER_READ_ONLY_OPTIMAL</c> barrier for an image its one declared set
        /// does not name and its shaders cannot read.
        ///
        /// <para><b>THE ROUND TRIP IS THE COST, NOT THE ONE BARRIER.</b> The image was moved out of the layout its
        /// real consumer wants, so the next dispatch that binds it as storage paid a second barrier to move it
        /// back. Both halves are asserted, because a fix that only stopped the first would leave the second.</para>
        /// </summary>
        [Fact]
        public void ADrawUnderAShorterLayout_TransitionsNothingForTheSetThatLayoutDropped()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture map = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    16, 16, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
                owned.Add(map);
                IGpuTexture albedo = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Sampled));
                owned.Add(albedo);
                IGpuTexture scene = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Sampled));
                owned.Add(scene);

                using VulkanCommandList list = Recording(fixture, owned);

                // THE TWO-SET PASS: a draw whose second set really does name the map, so the record at slot 1 is
                // the ordinary state a switch leaves behind rather than one this test wrote by hand.
                IGpuResourceSet vertex = SampledSet(fixture, owned, albedo);
                IGpuResourceSet material = SampledSet(fixture, owned, map);
                Adopt(fixture, list.GraphicsBinds, vertex, material);
                list.SetGraphicsResourceSet(0, vertex);
                list.SetGraphicsResourceSet(1, material);
                list.Draw(3);

                // THE PRODUCER: a dispatch leaves the map in GENERAL, and ends the pass on its way past (V-A4).
                IGpuResourceSet storage = StorageSet(fixture, owned, map);
                Adopt(fixture, list.ComputeBinds, storage);
                list.SetComputeResourceSet(0, storage);
                list.Dispatch(1, 1, 1);

                // THE POST PASS: one set, and slot 1 still records the material set at an index this layout has no
                // entry for at all.
                IGpuResourceSet post = SampledSet(fixture, owned, scene);
                Adopt(fixture, list.GraphicsBinds, post);
                list.SetGraphicsResourceSet(0, post);

                int before = fixture.Barriers.CallCount;
                fixture.Trace.Clear();
                list.Draw(3);

                Assert.Equal(before, fixture.Barriers.CallCount);
                Assert.DoesNotContain(fixture.Trace,
                    t => t.StartsWith("PipelineBarrier2", StringComparison.Ordinal));

                // AND THE MAP IS STILL IN GENERAL, so the dispatch that binds it next pays nothing to get it back.
                list.SetComputeResourceSet(0, StorageSet(fixture, owned, map));
                list.Dispatch(1, 1, 1);

                Assert.Equal(before, fixture.Barriers.CallCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND THE SHARP EDGE, WHICH IS PER DRAW RATHER THAN ONCE: a dropped set naming an image the pass BEGIN
        /// itself moves. A <c>RenderTarget | Sampled</c> texture rests in <c>SHADER_READ_ONLY_OPTIMAL</c> and the
        /// begin puts it in <c>COLOR_ATTACHMENT_OPTIMAL</c>, so a stale slot asking for the sampled layout is owed
        /// a transition again the moment the pass reopens. The walk answered yes, the draw ended the pass to emit
        /// it outside the instance, the begin moved the attachment straight back, and the next draw found exactly
        /// the same state. Two barriers, an end and a begin, at every draw of the pass, for a set no shader on the
        /// bound pipeline can read.
        /// </summary>
        [Fact]
        public void ADroppedSetNamingTheAttachment_DoesNotReopenThePassAtEveryDraw()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture target = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    64, 64, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                owned.Add(target);
                IGpuTexture scene = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 16, GpuTextureUsage.Sampled));
                owned.Add(scene);

                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, target);
                owned.Add(framebuffer);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                list.SetFramebuffer(framebuffer);

                IGpuResourceSet vertex = SampledSet(fixture, owned, scene);
                IGpuResourceSet material = SampledSet(fixture, owned, target);
                Adopt(fixture, list.GraphicsBinds, vertex, material);
                list.SetGraphicsResourceSet(0, vertex);
                list.SetGraphicsResourceSet(1, material);

                IGpuResourceSet post = SampledSet(fixture, owned, scene);
                Adopt(fixture, list.GraphicsBinds, post);
                list.SetGraphicsResourceSet(0, post);

                // THE FIRST DRAW OPENS THE PASS, and its begin moves the target into the attachment layout. That
                // barrier is the pass's own and is owed either way.
                list.Draw(3);
                int barriers = fixture.Barriers.CallCount;

                fixture.Trace.Clear();
                list.Draw(3);
                list.Draw(3);

                Assert.Equal(["Draw(3,1)", "Draw(3,1)"], fixture.Trace.ToArray());
                Assert.Equal(barriers, fixture.Barriers.CallCount);
                Assert.Equal(0, fixture.RenderApi.EndCount);
                Assert.Single(fixture.RenderApi.Begins);
                Assert.True(list.Rendering.IsRendering);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND A SLOT THE LAYOUT STILL DECLARES IS WALKED EVEN WHEN IT IS CLEAN, which is the property the bound
        /// must not be written in terms of dirty slots to get. The first draw leaves slot 0 clean, a dispatch then
        /// moves the texture that slot samples to <c>GENERAL</c>, and the second draw re-records nothing at all.
        /// It still owes the rule 1 transition, because what the draw reads is what the slot RECORDS and not what
        /// it owes a bind for.
        /// </summary>
        [Fact]
        public void ADeclaredSlotThatIsCleanAndUnchanged_IsStillTransitionedFor()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture map = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    16, 16, GpuTextureUsage.Storage | GpuTextureUsage.Sampled));
                owned.Add(map);

                using VulkanCommandList list = Recording(fixture, owned);

                IGpuResourceSet sampled = SampledSet(fixture, owned, map);
                Adopt(fixture, list.GraphicsBinds, sampled);
                list.SetGraphicsResourceSet(0, sampled);
                list.Draw(3);

                Assert.False(list.GraphicsBinds.IsDirty(0));

                IGpuResourceSet storage = StorageSet(fixture, owned, map);
                Adopt(fixture, list.ComputeBinds, storage);
                list.SetComputeResourceSet(0, storage);
                list.Dispatch(1, 1, 1);

                fixture.Trace.Clear();
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

        // ---- Fixtures ----

        // A recording with an offscreen framebuffer bound, which is the state every draw member needs. The colour
        // target is deliberately NOT sampled by anything here, so the attachment's own transitions stay out of the
        // counts the first two tests make.
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

        // THE PIPELINE LAYOUT A DRAW RUNS UNDER, adopted directly rather than through a whole VkPipeline, exactly
        // as VulkanDrawPathTests does it. The set count is what these tests are about, so it takes as many sets as
        // the caller names.
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
