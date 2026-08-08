using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SEAM BETWEEN THE STAGED UPLOAD PATH AND THE DEFERRED BEGIN: a bulk <c>UpdateBuffer</c> records a
    /// <c>vkCmdCopyBuffer</c>, which is illegal inside a render pass instance, so it ends the pass first (V-A4).
    /// Row 9 (https://github.com/APKiwiOrg/KhaozEngine/issues/519) declared
    /// <see cref="IVulkanRenderingScope"/> and left it for row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) to implement, and this is that edge under test.
    ///
    /// <para><b>AN UNWIRED SCOPE FAILS SILENTLY, WHICH IS WHY IT IS ASSERTED HERE RATHER THAN READ.</b> The
    /// upload path calls <c>rendering?.EndActiveRendering()</c> as its first act, so an uploader whose scope
    /// stayed null does not throw, does not warn and records the copy anyway. What comes out is a
    /// <c>vkCmdCopyBuffer</c> plus a barrier inside an open <c>vkCmdBeginRendering</c> scope, which is
    /// undefined behaviour on a run with no validation layer and a VUID on one that has it. Nothing above this
    /// line can see the difference.</para>
    ///
    /// <para><b>THE UPLOADER IS A DOUBLE, AND THE ORDER IT REPRODUCES IS THE ONE THING IT COPIES.</b> The real
    /// <see cref="VulkanListUploads"/> needs a real <c>Vk</c>, which no device-free rig has, so what runs here is
    /// the list, its schedule and the fake render seam, with a double standing in for the arena.
    /// <see cref="VulkanBufferUpload.Record"/>'s own half of the order (end the pass, THEN copy, THEN barrier) is
    /// pinned against the real function in
    /// <c>VulkanStagingArenaTests.ARecordedUpload_EndsThePassThenCopiesThenBarriers</c>. What is pinned here is
    /// the SCOPE that function is handed.</para>
    /// </summary>
    public sealed class VulkanRenderingScopeTests
    {
        /// <summary>
        /// THE LIST HANDS ITSELF TO ITS OWN UPLOADER, from its constructor. This is the wiring the whole file is
        /// about: with it missing the scope is null, and null is indistinguishable at every call site from "there
        /// is no pass to end".
        /// </summary>
        [Fact]
        public void TheList_HandsItselfToItsUploaderAsTheRenderingScope()
        {
            var fixture = new VulkanResourceFixture();
            var uploads = new ScopedUploads(fixture.RenderApi);

            using VulkanCommandList list = fixture.CreateList(uploads);

            Assert.Same(list, uploads.Scope);
        }

        /// <summary>
        /// A BULK UPLOAD ENDS THE OPEN PASS BEFORE IT RECORDS THE COPY. A <c>vkCmdCopyBuffer</c> may not appear
        /// inside a render pass instance, and this is the split the uniform ring exists so a per-frame write never
        /// pays: a ring-backed write is a memcpy and reaches none of this.
        /// </summary>
        [Fact]
        public void ABulkUpload_EndsTheOpenPassBeforeTheCopy()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                owned.Add(framebuffer);

                var uploads = new ScopedUploads(fixture.RenderApi);
                using VulkanCommandList list = fixture.CreateList(uploads);

                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.PrepareDraw();
                Assert.True(list.Rendering.IsRendering);

                list.UpdateBuffer<byte>(Bulk, 0, new byte[] { 1, 2, 3, 4 });

                Assert.Equal(1, uploads.Copies);
                Assert.Equal(1, uploads.EndsBeforeTheCopy);
                Assert.Equal(1, fixture.RenderApi.EndCount);
                Assert.False(list.Rendering.IsRendering);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// AND IT TAKES THE CLEAR-ONLY FLUSH WITH IT, AGAINST THE CURRENT SLOT'S BUFFER. The upload path goes
        /// through the SAME helper <c>End</c> does rather than a second copy of the rule, so a pass that collected
        /// clears no draw consumed still clears, and the begin-and-end pair names the buffer the list is recording
        /// into right now rather than a stale slot.
        /// </summary>
        [Fact]
        public void ABulkUpload_FlushesAPendingClearIntoTheCurrentBuffer()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, Colour(fixture, owned));
                owned.Add(framebuffer);

                var uploads = new ScopedUploads(fixture.RenderApi);
                using VulkanCommandList list = fixture.CreateList(uploads);

                list.Begin();
                list.SetFramebuffer(framebuffer);
                list.ClearColorTarget(0, Color.White);

                list.UpdateBuffer<byte>(Bulk, 0, new byte[] { 1 });

                VulkanRecordedBegin begin = Assert.Single(fixture.RenderApi.Begins);
                Assert.Equal(VulkanLoadOp.Clear, begin.Colour[0].LoadOp);
                Assert.Equal(Color.White, begin.Colour[0].ClearValue);
                Assert.Equal(list.Ring.BufferAt(list.Ring.Slot), begin.CommandBuffer);
                Assert.Equal(1, fixture.RenderApi.EndCount);
                Assert.Equal(1, uploads.EndsBeforeTheCopy);
            }
            finally
            {
                for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            }
        }

        /// <summary>
        /// THE INVERSE: A BULK UPLOAD WITH NO PASS OPEN ENDS NOTHING. The helper is safe to call when nothing is
        /// open, which is what lets the upload path call it unconditionally rather than asking first, and a
        /// begin-and-end pair emitted per upload would be exactly the render-pass thrash the deferred begin
        /// exists to remove.
        /// </summary>
        [Fact]
        public void ABulkUploadWithNoPassOpen_EndsNothing()
        {
            var fixture = new VulkanResourceFixture();
            var uploads = new ScopedUploads(fixture.RenderApi);

            using VulkanCommandList list = fixture.CreateList(uploads);
            list.Begin();

            list.UpdateBuffer<byte>(Bulk, 0, new byte[] { 1 });

            Assert.Equal(1, uploads.Copies);
            Assert.Equal(0, uploads.EndsBeforeTheCopy);
            Assert.Equal(0, fixture.RenderApi.EndCount);
            Assert.Empty(fixture.RenderApi.Begins);
        }

        /// <summary>
        /// A BULK UPLOAD OUTSIDE A RECORDING IS REFUSED THROUGH THE SCOPE, because the pass end it owes is a
        /// <c>vkCmd*</c> against a buffer that <c>vkBeginCommandBuffer</c> has not seen. The ring still names a
        /// slot after <c>End</c>, so the sealed-record case reaches here rather than falling out of the slot
        /// check.
        /// </summary>
        [Fact]
        public void ABulkUploadOutsideARecording_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var uploads = new ScopedUploads(fixture.RenderApi);

            using VulkanCommandList list = fixture.CreateList(uploads);
            list.Begin();
            list.End();

            Assert.Throws<InvalidOperationException>(() => list.UpdateBuffer<byte>(Bulk, 0, new byte[] { 1 }));
            Assert.Equal(0, uploads.Copies);
        }

        /// <summary>A scope call on a disposed list is refused as a disposal error, which is the check every
        /// rendering member shares. The pools are gone, so the buffer the end would name no longer exists.
        /// </summary>
        [Fact]
        public void TheRenderingScopeOnADisposedList_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var uploads = new ScopedUploads(fixture.RenderApi);

            VulkanCommandList list = fixture.CreateList(uploads);
            list.Begin();
            list.Dispose();

            IVulkanRenderingScope scope = list;
            Assert.Throws<ObjectDisposedException>(scope.EndActiveRendering);
        }

        /// <summary>
        /// A LIST WITH NO RENDERING SEAM HANDS OVER NOTHING, and that null is correct rather than half-wired:
        /// with no schedule there is no pass to end, and a scope that answered anyway would refuse a legal bulk
        /// upload on a list a test built.
        /// </summary>
        [Fact]
        public void AListWithNoRenderingSeam_HandsOverNoScope()
        {
            using var fixture = new VulkanCommandListTests.Fixture();
            var uploads = new ScopedUploads(new FakeVulkanRenderApi());

            using var list = new VulkanCommandList(
                new VulkanCommandPoolRing(fixture.Api, 3, fixture.Timeline, fixture.Backpressure), fixture.Retired,
                uploads);

            list.Begin();
            list.UpdateBuffer<byte>(Bulk, 0, new byte[] { 1 });

            Assert.Null(uploads.Scope);
            Assert.Equal(1, uploads.Copies);
        }

        // ---- Fixtures ----

        // A destination that is not ring-backed, which is what routes the write to the arena leg rather than to
        // the memcpy one. The handle is a number: nothing device-free dereferences it.
        static readonly FakeVulkanUploadBuffer Bulk =
            new(0xBEEF, 256, GpuBufferUsage.VertexBuffer);

        static IGpuTexture Colour(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuTexture texture = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
            owned.Add(texture);
            return texture;
        }

        // The list's uploader, standing in for VulkanListUploads (which needs a real Vk). It reproduces the ONE
        // ordering VulkanBufferUpload.Record fixes, end the pass and THEN record the copy, and notes how many
        // vkCmdEndRendering calls had gone out by the time the copy was recorded. That number is the assertion:
        // zero means the copy would have landed inside an open render pass instance.
        sealed class ScopedUploads : IVulkanRecordUploads
        {
            readonly FakeVulkanRenderApi _api;

            internal ScopedUploads(FakeVulkanRenderApi api) => _api = api;

            /// <summary>The scope the list handed over, or null if it handed over none.</summary>
            internal IVulkanRenderingScope? Scope { get; private set; }

            /// <summary>How many copies were recorded.</summary>
            internal int Copies { get; private set; }

            /// <summary>The pass ends that had gone out when the last copy was recorded.</summary>
            internal int EndsBeforeTheCopy { get; private set; }

            public void Upload(IVulkanUploadDestination destination, ulong destinationOffsetBytes,
                ReadOnlySpan<byte> data)
            {
                Scope?.EndActiveRendering();

                EndsBeforeTheCopy = _api.EndCount;
                Copies++;
            }

            public void BeginSlot(int slot)
            {
            }

            public void UseRenderingScope(IVulkanRenderingScope scope) => Scope = scope;
        }
    }
}
