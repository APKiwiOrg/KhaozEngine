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
    /// THE TRANSFER FAMILY, DEVICE-FREE: the four staging cases, every region's arithmetic, the mip chain's
    /// per-level extents and layout dance, the multisample resolve, and the buffer copy's ordering barriers.
    /// Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525), decisions V-A4, V-C6 and
    /// V-C7.
    ///
    /// <para><b>THIS IS THE HIGHEST-RISK PARITY SURFACE IN THE BACKEND AND THE REGIONS ARE WHY.</b> Every golden
    /// in the suite reads back through a copy into a staging texture and consumes <c>MappedData.RowPitch</c>, so a
    /// buffer offset, a row length or an extent that is subtly wrong garbles all 36 at once, silently: the
    /// readback succeeds, the pointer is valid, and the pixels are in the wrong places. The OFFSETS themselves are
    /// <see cref="VulkanStagingLayout"/>'s, pinned against a checked-in table taken from the incumbent's own
    /// arithmetic by <c>VulkanStagingLayoutTableTests</c>. What is pinned here is that the copy path uses them,
    /// on the right side of the copy, with the image's own level and layer in the subresource.</para>
    /// </summary>
    public sealed class VulkanTransferPathTests
    {
        // ---- Buffer copies ----

        /// <summary>
        /// A BUFFER COPY IS A BARRIER, THE COPY, AND A BARRIER, in that order. A <c>VkBuffer</c> has no layout, so
        /// nothing the layout tracker does orders this copy against the dispatch that wrote its source or the
        /// draw that reads its destination. The incumbent emitted ONE barrier, after the copy, naming
        /// <c>VERTEX_INPUT</c> and <c>VERTEX_ATTRIBUTE_READ</c> and nothing else, which orders one consumer and
        /// nothing on the source side at all.
        /// </summary>
        [Fact]
        public void ABufferCopy_IsBarrieredOnBothSides()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                fixture.Trace.Clear();

                IGpuBuffer source = Buffer(fixture, owned, GpuBufferUsage.StructuredBufferReadWrite);
                IGpuBuffer destination = Buffer(fixture, owned, GpuBufferUsage.Staging);

                list.CopyBuffer(source, 0, destination, 0, 128);

                Assert.Equal(
                    ["MemoryBarrier(toTransfer)", "CopyBuffer(128)", "MemoryBarrier(fromTransfer)"],
                    fixture.Trace.ToArray());
                Assert.Equal(128ul, fixture.TransferSink.BufferCopies[0].Region.Size);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>Both barrier shapes name both stage masks and both access masks explicitly (V-F6), and the
        /// source side of the FIRST is what the incumbent left out entirely.</summary>
        [Fact]
        public void TheBufferCopyBarriers_NameEveryMaskInBothDirections()
        {
            MemoryBarrier2 before = VulkanTransferBarrier.ToTransfer;
            MemoryBarrier2 after = VulkanTransferBarrier.FromTransfer;

            Assert.Equal(PipelineStageFlags2.AllCommandsBit, before.SrcStageMask);
            Assert.Equal(AccessFlags2.MemoryWriteBit, before.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.AllTransferBit, before.DstStageMask);

            Assert.Equal(PipelineStageFlags2.AllTransferBit, after.SrcStageMask);
            Assert.Equal(AccessFlags2.TransferWriteBit, after.SrcAccessMask);
            Assert.Equal(PipelineStageFlags2.AllCommandsBit, after.DstStageMask);
        }

        /// <summary>A window that leaves either buffer is refused rather than clipped, because
        /// <c>vkCmdCopyBuffer</c> reads and writes exactly the region it is given.</summary>
        [Fact]
        public void ACopyWindowThatLeavesTheBuffer_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                IGpuBuffer source = Buffer(fixture, owned, GpuBufferUsage.StructuredBufferReadWrite);
                IGpuBuffer destination = Buffer(fixture, owned, GpuBufferUsage.Staging);

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => list.CopyBuffer(source, 200, destination, 0, 128));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND A ZERO-SIZE COPY IS REFUSED BY ITS OWN NAME rather than through the window message. A
        /// <c>VkBufferCopy</c> region's size must be positive, so an empty copy is not a no-op at this level and
        /// not a window that leaves the buffer either. The out-of-range refusal used to answer both, so a caller
        /// who passed a legitimately empty length was told its region left an allocation it sits comfortably
        /// inside.
        /// </summary>
        [Fact]
        public void AZeroSizeBufferCopy_IsRefusedByItsOwnName()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                IGpuBuffer source = Buffer(fixture, owned, GpuBufferUsage.StructuredBufferReadWrite);
                IGpuBuffer destination = Buffer(fixture, owned, GpuBufferUsage.Staging);

                ArgumentOutOfRangeException refused = Assert.Throws<ArgumentOutOfRangeException>(
                    () => list.CopyBuffer(source, 0, destination, 0, 0));

                Assert.Contains("size must be positive", refused.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("leaves the buffer", refused.Message, StringComparison.Ordinal);
                Assert.Empty(fixture.TransferSink.BufferCopies);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- Texture copies ----

        /// <summary>
        /// THE READBACK DIRECTION, WHICH IS THE ONE EVERY GOLDEN TAKES: a render target into a staging texture is
        /// <c>vkCmdCopyImageToBuffer</c>, with the source transitioned to <c>TRANSFER_SRC_OPTIMAL</c> first and
        /// with the STAGING side's software subresource layout supplying the buffer offset and the row terms.
        /// </summary>
        [Fact]
        public void ARenderTargetIntoAStagingTexture_IsAnImageToBufferCopyWithTheSoftwareLayout()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var source = (VulkanTexture)fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 4, GpuTextureUsage.RenderTarget));
                owned.Add(source);
                var destination = (VulkanTexture)fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 4, GpuTextureUsage.Staging));
                owned.Add(destination);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();
                fixture.Trace.Clear();

                list.CopyTexture(source, destination);

                Assert.Single(fixture.TransferSink.BufferImageCopies);
                VulkanRecordedBufferImageCopy copy = fixture.TransferSink.BufferImageCopies[0];

                Assert.True(copy.ToBuffer);
                Assert.Equal(source.Image, copy.Image);
                Assert.Equal(destination.StagingBuffer, copy.Buffer);
                Assert.Equal(8u, copy.Region.BufferRowLength);
                Assert.Equal(4u, copy.Region.BufferImageHeight);
                Assert.Equal(8u, copy.Region.ImageExtent.Width);
                Assert.Equal(4u, copy.Region.ImageExtent.Height);
                Assert.Equal(ImageAspectFlags.ColorBit, copy.Region.ImageSubresource.AspectMask);

                // AND THE SOURCE WENT TO TRANSFER_SRC BEFORE THE COPY, which is the half a region assertion
                // cannot see. A staging texture is a VkBuffer and is never transitioned.
                Assert.Single(fixture.Barriers.Barriers);
                Assert.Equal(ImageLayout.TransferSrcOptimal, fixture.Barriers.Barriers[0].NewLayout);
                Assert.True(
                    fixture.Trace.FindIndex(t => t.StartsWith("PipelineBarrier2", StringComparison.Ordinal))
                    < fixture.Trace.FindIndex(t => t.StartsWith("CopyImageToBuffer", StringComparison.Ordinal)));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// A WHOLE-TEXTURE COPY NAMES EVERY MIP LEVEL AND EVERY ARRAY LAYER, one region each, and each region's
        /// buffer offset is the staging layout's own for that subresource. A copy that only did mip 0 would read
        /// back a chain's base and leave the rest as whatever the staging buffer held.
        /// </summary>
        [Fact]
        public void AWholeTextureCopy_NamesEverySubresourceAtItsOwnStagingOffset()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var source = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.RenderTarget, mipLevels: 3, arrayLayers: 2));
                owned.Add(source);
                var destination = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Staging, mipLevels: 3, arrayLayers: 2));
                owned.Add(destination);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.CopyTexture(source, destination);

                Assert.Equal(6, fixture.TransferSink.BufferImageCopies.Count);

                foreach (VulkanRecordedBufferImageCopy copy in fixture.TransferSink.BufferImageCopies)
                {
                    ulong expected = VulkanStagingLayout
                        .For(destination.StagingShape, copy.Region.ImageSubresource.MipLevel,
                            copy.Region.ImageSubresource.BaseArrayLayer)
                        .Offset;

                    Assert.Equal(expected, copy.Region.BufferOffset);
                }

                // ONE BATCHED TRANSITION over the WHOLE range rather than one per subresource, which is what keeps
                // MV5's bound per texture rather than per subresource.
                Assert.Equal(1, fixture.Barriers.CallCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>Two non-staging textures are <c>vkCmdCopyImage</c>, with both sides transitioned.</summary>
        [Fact]
        public void TwoRealTextures_AreAnImageToImageCopy()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var source = (VulkanTexture)fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Storage));
                owned.Add(source);
                var destination = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 4));
                owned.Add(destination);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.CopyTextureSubresource(source, 0, 0, destination, 0, 0, 8, 8);

                Assert.Single(fixture.TransferSink.ImageCopies);
                VulkanRecordedImageCopy copy = fixture.TransferSink.ImageCopies[0];

                Assert.Equal(source.Image, copy.Source);
                Assert.Equal(destination.Image, copy.Destination);
                Assert.Equal(8u, copy.Region.Extent.Width);
                Assert.Equal(1u, copy.Region.Extent.Depth);

                ImageLayout[] layouts = fixture.Barriers.Barriers.Select(b => b.NewLayout).ToArray();
                Assert.Contains(ImageLayout.TransferSrcOptimal, layouts);
                Assert.Contains(ImageLayout.TransferDstOptimal, layouts);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>Two staging textures have no image at all, so the copy is a plain byte copy between two
        /// software layouts and nothing is transitioned.</summary>
        [Fact]
        public void TwoStagingTextures_AreAPlainBufferCopyWithNoTransition()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var source = (VulkanTexture)fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Staging));
                owned.Add(source);
                var destination = (VulkanTexture)fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Staging));
                owned.Add(destination);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.CopyTexture(source, destination);

                Assert.Single(fixture.TransferSink.BufferCopies);
                Assert.Equal(0, fixture.Barriers.CallCount);
                Assert.Empty(fixture.TransferSink.MemoryBarriers);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>The four cases are decided by the two staging flags and by nothing else.</summary>
        [Fact]
        public void TheFourCases_AreDecidedByTheStagingFlagsAlone()
        {
            Assert.Equal(VulkanTransferCase.ImageToImage, VulkanTransferPlan.CaseFor(false, false));
            Assert.Equal(VulkanTransferCase.BufferToImage, VulkanTransferPlan.CaseFor(true, false));
            Assert.Equal(VulkanTransferCase.ImageToBuffer, VulkanTransferPlan.CaseFor(false, true));
            Assert.Equal(VulkanTransferCase.BufferToBuffer, VulkanTransferPlan.CaseFor(true, true));
        }

        /// <summary>A whole-texture copy between two shapes that do not agree is refused rather than clipped: it
        /// names every subresource on both sides, so a mismatch is a copy that would run off an end.</summary>
        [Fact]
        public void AWholeCopyBetweenMismatchedShapes_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture source = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget));
                owned.Add(source);
                IGpuTexture destination = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(4, 4, GpuTextureUsage.Staging));
                owned.Add(destination);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                Assert.Throws<ArgumentException>(() => list.CopyTexture(source, destination));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- The mip chain ----

        /// <summary>
        /// THE MIP CHAIN IS ONE BLIT PER LEVEL, EACH HALVING THE LAST, and each is preceded by the pair of
        /// transitions that puts level N-1 in <c>TRANSFER_SRC_OPTIMAL</c> and level N in
        /// <c>TRANSFER_DST_OPTIMAL</c>. The two ranges are DISJOINT at every step, which is exactly the shape the
        /// layout tracker answers per level, and the whole-chain sampled bind that follows then CONTAINS every one
        /// of those entries.
        /// </summary>
        [Fact]
        public void TheMipChain_IsOneHalvingBlitPerLevel()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                var texture = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 4, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 4));
                owned.Add(texture);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.GenerateMipmaps(texture);

                Assert.Equal(3, fixture.TransferSink.Blits.Count);

                (uint SourceWidth, uint SourceHeight, uint DestinationWidth, uint DestinationHeight)[] expected =
                [
                    (8, 4, 4, 2),
                    (4, 2, 2, 1),
                    (2, 1, 1, 1),
                ];

                for (int i = 0; i < expected.Length; i++)
                {
                    ImageBlit region = fixture.TransferSink.Blits[i].Region;

                    Assert.Equal((uint)i, region.SrcSubresource.MipLevel);
                    Assert.Equal((uint)i + 1, region.DstSubresource.MipLevel);
                    Assert.Equal((int)expected[i].SourceWidth, region.SrcOffsets.Element1.X);
                    Assert.Equal((int)expected[i].SourceHeight, region.SrcOffsets.Element1.Y);
                    Assert.Equal((int)expected[i].DestinationWidth, region.DstOffsets.Element1.X);
                    Assert.Equal((int)expected[i].DestinationHeight, region.DstOffsets.Element1.Y);
                    Assert.True(fixture.TransferSink.Blits[i].Linear);
                }

                // TWO TRANSITIONS PER LEVEL, one per side, and both before that level's blit.
                Assert.Equal(6, fixture.Barriers.CallCount);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>The halving floors at one, so a 1024 by 1 texture's chain ends at 1 by 1 rather than at 1 by
        /// 0, which is an extent the driver refuses.</summary>
        [Fact]
        public void TheHalving_FloorsAtOne()
        {
            Assert.Equal(1u, VulkanTransferPlan.NextMip(1));
            Assert.Equal(1u, VulkanTransferPlan.NextMip(2));
            Assert.Equal(512u, VulkanTransferPlan.NextMip(1024));
        }

        /// <summary>A texture with one mip level, or a staging texture, has no chain to generate and is refused by
        /// name.</summary>
        [Fact]
        public void AMipGenerationWithNoChain_IsRefused()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture flat = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
                owned.Add(flat);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                Assert.Throws<ArgumentException>(() => list.GenerateMipmaps(flat));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- The streaming path, end to end ----

        /// <summary>
        /// THE WHOLE STREAMING PATH THROUGH THE SHIPPED MEMBERS: a copy seeds mip 0, <c>GenerateMipmaps</c> walks
        /// the chain a level at a time, and a draw samples the WHOLE texture. That composition is asserted
        /// SYNTHETICALLY in <c>VulkanLayoutTrackerTests</c>, by driving the tracker directly with the ranges the
        /// path is believed to produce, and this is the same statement made by the members a renderer actually
        /// calls, which is what proves the believed ranges are the real ones.
        ///
        /// <para><b>WHAT IT PINS.</b> The whole-chain sampled bind is ONE
        /// <c>vkCmdPipelineBarrier2</c> carrying one barrier PER LEVEL, each from that level's own layout
        /// (<c>TRANSFER_SRC_OPTIMAL</c> up to the last, <c>TRANSFER_DST_OPTIMAL</c> on it), and the pieces then
        /// COLLAPSE, so the chain is back at rest and <c>End</c> owes it nothing.</para>
        /// </summary>
        [Fact]
        public void SeedThenGenerateThenSample_IsOneBarrierPerLevelThatCollapsesToNothingAtEnd()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture seed = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
                owned.Add(seed);
                IGpuTexture chain = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 3));
                owned.Add(chain);

                using VulkanCommandList list = Drawing(fixture, owned);

                list.CopyTextureSubresource(seed, 0, 0, chain, 0, 0, 8, 8);
                list.GenerateMipmaps(chain);

                IGpuResourceSet material = SampledSet(fixture, owned, chain);
                AdoptLayout(fixture, list.GraphicsBinds, material);
                list.SetGraphicsResourceSet(0, material);

                int callsBefore = fixture.Barriers.CallCount;
                int barriersBefore = fixture.Barriers.BarrierCount;
                list.Draw(3);

                // ONE CALL, THREE BARRIERS, one per tracked level and each from its own layout.
                Assert.Equal(callsBefore + 1, fixture.Barriers.CallCount);
                Assert.Equal(barriersBefore + 3, fixture.Barriers.BarrierCount);

                ImageMemoryBarrier2[] widening = fixture.Barriers.Barriers.Skip(barriersBefore).ToArray();
                Assert.All(widening, b => Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, b.NewLayout));
                Assert.Equal(2, widening.Count(b => b.OldLayout == ImageLayout.TransferSrcOptimal));
                Assert.Single(widening, b => b.OldLayout == ImageLayout.TransferDstOptimal);

                // AND THE PIECES COLLAPSED, so End restores the seed alone and the chain owes nothing: it is
                // already back in the layout it rests in.
                int callsAfterDraw = fixture.Barriers.CallCount;
                list.End();

                Assert.Equal(callsAfterDraw + 1, fixture.Barriers.CallCount);
                ImageMemoryBarrier2 restored = Assert.Single(
                    fixture.Barriers.Barriers.Skip(barriersBefore + 3));
                Assert.Equal(ImageLayout.TransferSrcOptimal, restored.OldLayout);
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, restored.NewLayout);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>
        /// AND THE PARTIAL-LAYER VARIANT OF THAT PATH IS THE NAMED REFUSAL, NOT THE COMPOSITION, which is worth
        /// pinning because it is the case the shipped shape does not produce and the one a reader would guess
        /// wrong. Seeding ONE array layer and then generating mips over ALL of them asks the tracker to widen a
        /// per-layer entry to an all-layer range whose target is a TRANSFER layout rather than the resting one,
        /// so the untouched layers of that range are still at rest and the tracker would have to name a range it
        /// cannot express without subtracting rectangles.
        ///
        /// <para>The whole-chain sampled bind never happens, because <c>GenerateMipmaps</c> throws first. The
        /// composition above is the shipped shape: <c>Scene3D</c>'s upload seeds every layer it is going to
        /// generate over.</para>
        /// </summary>
        [Fact]
        public void APartialLayerSeedThenAnAllLayerMipGeneration_IsRefusedByName()
        {
            var fixture = new VulkanResourceFixture();
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture seed = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Sampled));
                owned.Add(seed);
                IGpuTexture array = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 3,
                    arrayLayers: 2));
                owned.Add(array);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.CopyTextureSubresource(seed, 0, 0, array, 0, 0, 8, 8);

                InvalidOperationException refused =
                    Assert.Throws<InvalidOperationException>(() => list.GenerateMipmaps(array));

                Assert.Contains("WIDER than the ranges it has tracked", refused.Message, StringComparison.Ordinal);
                Assert.Contains("still at rest", refused.Message, StringComparison.Ordinal);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- The resolve ----

        /// <summary>
        /// <c>vkCmdResolveImage</c> AT MIP 0 LAYER 0 (V-C6), outside a render pass instance, with both images
        /// transitioned to the transfer layouts.
        /// </summary>
        [Fact]
        public void AResolve_IsOneRegionAtMipZeroOutsideThePass()
        {
            var fixture = new VulkanResourceFixture(maxMsaaSampleCount: 4);
            var owned = new List<IDisposable>();

            try
            {
                var multisampled = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    16, 8, GpuTextureUsage.RenderTarget, sampleCount: 4));
                owned.Add(multisampled);
                var resolved = (VulkanTexture)fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                    16, 8, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
                owned.Add(resolved);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                list.ResolveTexture(multisampled, resolved);

                Assert.Single(fixture.TransferSink.Resolves);
                VulkanRecordedResolve resolve = fixture.TransferSink.Resolves[0];

                Assert.Equal(multisampled.Image, resolve.Source);
                Assert.Equal(resolved.Image, resolve.Destination);
                Assert.Equal(0u, resolve.Region.SrcSubresource.MipLevel);
                Assert.Equal(0u, resolve.Region.SrcSubresource.BaseArrayLayer);
                Assert.Equal(16u, resolve.Region.Extent.Width);
                Assert.Equal(8u, resolve.Region.Extent.Height);
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        /// <summary>A resolve whose source is not multisampled, or whose destination is, is refused by name rather
        /// than emitted as a call the driver would reject with a handle in it.</summary>
        [Fact]
        public void AResolveOfASingleSampleSource_IsRefused()
        {
            var fixture = new VulkanResourceFixture(maxMsaaSampleCount: 4);
            var owned = new List<IDisposable>();

            try
            {
                IGpuTexture flat = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 8, GpuTextureUsage.RenderTarget));
                owned.Add(flat);
                IGpuTexture other = fixture.Factory.CreateTexture(
                    VulkanResourceFixture.Texture(16, 8, GpuTextureUsage.RenderTarget));
                owned.Add(other);

                using VulkanCommandList list = fixture.CreateList();
                list.Begin();

                Assert.Throws<ArgumentException>(() => list.ResolveTexture(flat, other));
            }
            finally
            {
                DisposeAll(owned);
            }
        }

        // ---- The pass-ending invariant ----

        /// <summary>
        /// EVERY TRANSFER MEMBER ENDS THE PENDING RENDER PASS INSTANCE FIRST (V-A4), through the ONE helper every
        /// such command calls. A copy inside an instance is a call the driver refuses, and the invariant is worth
        /// asserting over the WHOLE family rather than at one member, because it is the kind of step a fifth
        /// implementation forgets.
        /// </summary>
        [Fact]
        public void EveryTransferMember_EndsThePendingRenderPassFirst()
        {
            foreach (string member in new[]
                { "CopyBuffer", "CopyTexture", "CopyTextureSubresource", "GenerateMipmaps", "ResolveTexture" })
            {
                var fixture = new VulkanResourceFixture(maxMsaaSampleCount: 4);
                var owned = new List<IDisposable>();

                try
                {
                    using VulkanCommandList list = Drawing(fixture, owned);
                    Assert.True(list.Rendering.IsRendering, member);

                    Invoke(fixture, owned, list, member);

                    Assert.False(list.Rendering.IsRendering, member);
                }
                finally
                {
                    DisposeAll(owned);
                }
            }
        }

        // ---- Fixtures ----

        static IGpuBuffer Buffer(VulkanResourceFixture fixture, List<IDisposable> owned, GpuBufferUsage usage)
        {
            IGpuBuffer buffer = fixture.Factory.CreateBuffer(VulkanResourceFixture.Buffer(256, usage));
            owned.Add(buffer);
            return buffer;
        }

        // A one-element set that SAMPLES a texture, which is the bind the whole-chain widening happens at.
        static IGpuResourceSet SampledSet(VulkanResourceFixture fixture, List<IDisposable> owned,
            IGpuTexture texture)
        {
            IGpuResourceLayout layout = fixture.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(
                    new GpuResourceLayoutElement("T", GpuResourceKind.TextureReadOnly,
                        GpuShaderStages.Fragment)));
            owned.Add(layout);

            IGpuResourceSet set = fixture.Factory.CreateResourceSet(
                new GpuResourceSetDescription(layout, texture));
            owned.Add(set);
            return set;
        }

        // THE PIPELINE LAYOUT A FLUSH BINDS UNDER, adopted directly rather than through a whole VkPipeline: a
        // flush with none is refused by name, so every test that reaches a bind has to supply one.
        static void AdoptLayout(VulkanResourceFixture fixture, VulkanBindRecords records, IGpuResourceSet set)
        {
            VulkanResourceLayout layout = ((VulkanResourceSet)set).Layout;
            ulong[] handles = [layout.SetLayout];

            records.SetPipelineLayout(
                fixture.Descriptors.PipelineLayouts.GetOrCreate(handles, layout.DynamicUniformCount), handles);
        }

        // A recording with an OPEN render pass instance, which is the state the pass-ending invariant is about.
        static VulkanCommandList Drawing(VulkanResourceFixture fixture, List<IDisposable> owned)
        {
            IGpuTexture colour = fixture.Factory.CreateTexture(
                VulkanResourceFixture.Texture(64, 64, GpuTextureUsage.RenderTarget));
            owned.Add(colour);

            IGpuFramebuffer framebuffer = fixture.Factory.CreateFramebuffer(null, colour);
            owned.Add(framebuffer);

            VulkanCommandList list = fixture.CreateList();
            list.Begin();
            list.SetFramebuffer(framebuffer);
            list.Draw(3);
            return list;
        }

        static void Invoke(VulkanResourceFixture fixture, List<IDisposable> owned, VulkanCommandList list,
            string member)
        {
            switch (member)
            {
                case "CopyBuffer":
                    list.CopyBuffer(Buffer(fixture, owned, GpuBufferUsage.StructuredBufferReadWrite), 0,
                        Buffer(fixture, owned, GpuBufferUsage.Staging), 0, 128);
                    return;

                case "CopyTexture":
                case "CopyTextureSubresource":
                {
                    IGpuTexture source = fixture.Factory.CreateTexture(
                        VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget));
                    owned.Add(source);
                    IGpuTexture destination = fixture.Factory.CreateTexture(
                        VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.Staging));
                    owned.Add(destination);

                    if (member == "CopyTexture") list.CopyTexture(source, destination);
                    else list.CopyTextureSubresource(source, 0, 0, destination, 8, 8);
                    return;
                }

                case "GenerateMipmaps":
                {
                    IGpuTexture chain = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                        8, 8, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, mipLevels: 3));
                    owned.Add(chain);
                    list.GenerateMipmaps(chain);
                    return;
                }

                default:
                {
                    IGpuTexture multisampled = fixture.Factory.CreateTexture(VulkanResourceFixture.Texture(
                        8, 8, GpuTextureUsage.RenderTarget, sampleCount: 4));
                    owned.Add(multisampled);
                    IGpuTexture resolved = fixture.Factory.CreateTexture(
                        VulkanResourceFixture.Texture(8, 8, GpuTextureUsage.RenderTarget));
                    owned.Add(resolved);
                    list.ResolveTexture(multisampled, resolved);
                    return;
                }
            }
        }

        static void DisposeAll(List<IDisposable> owned)
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
        }
    }
}
