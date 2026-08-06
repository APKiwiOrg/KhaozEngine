using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE NATIVE VULKAN RING'S OWN ROWS: the two of section 9.4's ten whose owner is "this backend" and whose
    /// subject is arithmetic rather than sequencing, plus the creation-time invariants of V-M7.
    ///
    /// <para><b>WHAT IS NOT HERE.</b> The seven SHARED rows run against both backends' rings in
    /// <see cref="GpuUniformRingSharedTests"/>, through the one test-only interface of V-P5. This file is the
    /// STRIDE row, which is each backend's own because the arithmetic differs where the invariant does not (a
    /// 16-constant count on Direct3D 11, a <c>minUniformBufferOffsetAlignment</c> floor and a descriptor range
    /// here) and because the Vulkan half additionally answers to a VUID. Lock legality, the other backend-own
    /// runtime row, is in <see cref="VulkanRingSemanticsTests"/>. Ordering is a BUILD-ORDER fact with nothing a
    /// test can observe: what enforces it is this row depending on row 5, which is a dependency edge rather than an
    /// assertion.</para>
    ///
    /// <para><b>ALL OF IT IS ARITHMETIC</b>, so every test here is an ordinary <c>[Fact]</c> that runs on macOS and
    /// Linux as well as Windows. There is no native seam to fake because there are no native calls to make: a ring
    /// on this backend is a persistently mapped pointer plus offsets.</para>
    /// </summary>
    public sealed class VulkanUniformRingTests
    {
        // ---- V-M7: which buffers are ring-backed, and the divergent creation failure ----------------------

        /// <summary>A uniform buffer is ring-backed. Nothing else is, which is the first half of V-M7: a storage
        /// buffer's descriptor names the whole allocation, so it would address the first segment forever.</summary>
        [Fact]
        public void OnlyUniformBuffers_AreRingBacked()
        {
            Assert.True(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.UniformBuffer));

            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.VertexBuffer));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.IndexBuffer));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.IndirectBuffer));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadOnly));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.StructuredBufferReadWrite));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.Staging));
            Assert.False(VulkanBufferRingPolicy.ForBuffer(GpuBufferUsage.None));
        }

        /// <summary>
        /// THE BACKEND-DIVERGENT CREATION FAILURE (V-M7). A uniform buffer combined with any other way of binding
        /// the same bytes throws at creation rather than rendering one frame's data as another's: only the dynamic
        /// uniform descriptor carries the ring's per-frame base, so the other bind would read segment zero while
        /// the uniform read read segment N.
        /// <para>
        /// This combination is ACCEPTED by <see cref="GpuBackendKind.Vulkan"/>, the Veldrid leg, which is what
        /// makes it a DIVERGENCE rather than a bug fix, and the message has to name that: a consumer meeting this
        /// has working code on the other Vulkan backend and no other way to find out why this one refuses it.
        /// </para>
        /// </summary>
        [Theory]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.StructuredBufferReadOnly)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.StructuredBufferReadWrite)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.VertexBuffer)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.IndexBuffer)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.IndirectBuffer)]
        public void AUniformBufferCombinedWithAnotherBinding_IsRefusedAtCreation(GpuBufferUsage usage)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => VulkanBufferRingPolicy.ForBuffer(usage));

            Assert.Contains("GpuBackendKind.Vulkan", ex.Message, StringComparison.Ordinal);
            Assert.Contains("divergence", ex.Message, StringComparison.Ordinal);

            Assert.True(VulkanBufferRingPolicy.IsRefusedCombination(usage));
            Assert.False(VulkanBufferRingPolicy.IsRingBacked(usage));
        }

        /// <summary>The dynamic and staging bits describe where memory LIVES rather than how the bytes are bound,
        /// so neither collides with the ring. Nothing in the engine passes them on a uniform buffer, and refusing
        /// them would be a divergence bought for no invariant at all.</summary>
        [Theory]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.Dynamic)]
        [InlineData(GpuBufferUsage.UniformBuffer | GpuBufferUsage.Staging)]
        public void AUniformBufferMayAlsoBeDynamicOrStaging(GpuBufferUsage usage)
        {
            Assert.True(VulkanBufferRingPolicy.ForBuffer(usage));
            Assert.True(VulkanBufferRingPolicy.IsRingBacked(usage));
        }

        // ---- 9.4's Stride row: the segment geometry (V-M5) -----------------------------------------------

        /// <summary>
        /// THE STRIDE IS THE BUFFER ROUNDED UP TO <c>max(256, minUniformBufferOffsetAlignment)</c>, and the 256 is
        /// derived on this side rather than borrowed: it is the spec's required MAXIMUM for that limit, so
        /// flooring there makes the stride device-independent instead of leaving a device-shaped number under a
        /// golden-bearing path.
        /// </summary>
        [Theory]
        [InlineData(256u, 256u)]
        [InlineData(768u, 768u)]
        [InlineData(8448u, 8448u)]     // ShadowMapRenderer.SkinnedDepthSlotBytes
        [InlineData(9472u, 9472u)]     // ModelRenderer.SkinnedMainSlotBytes
        [InlineData(16u, 256u)]
        [InlineData(272u, 512u)]
        public void ASegmentStride_IsTheBufferRoundedToTheDynamicOffsetAlignment(ulong size, ulong expected)
        {
            Assert.Equal(expected, VulkanRingStride.SegmentStrideFor(size, 0));
            Assert.Equal(expected, VulkanRingStride.SegmentStrideFor(size, 256));
            Assert.Equal(expected * 3, VulkanRingStride.TotalBytesFor(size, 3, 256));
        }

        /// <summary>
        /// A DEVICE REPORTING A SMALLER ALIGNMENT CHANGES NOTHING, which is the whole reason the floor is the
        /// load-bearing term. 256 is the spec's required maximum for the limit, so a conformant device can only
        /// ever report at or below it, and a facts value built without the read produces exactly the stride a real
        /// read produces.
        /// </summary>
        [Theory]
        [InlineData(0ul)]
        [InlineData(1ul)]
        [InlineData(16ul)]
        [InlineData(64ul)]
        [InlineData(256ul)]
        public void ADeviceAlignmentAtOrUnderTheFloor_LeavesTheStrideAlone(ulong reported)
        {
            Assert.Equal(VulkanRingStride.OffsetAlignmentFloor, VulkanRingStride.AlignmentFor(reported));
            Assert.Equal(512ul, VulkanRingStride.SegmentStrideFor(272, reported));
        }

        /// <summary>A device reporting MORE than the floor is not conformant, and the stride follows it anyway
        /// rather than producing an offset that device would reject. The floor is a floor, not a pin.</summary>
        [Fact]
        public void ADeviceAlignmentAboveTheFloor_RaisesTheStride()
        {
            Assert.Equal(1024ul, VulkanRingStride.AlignmentFor(1024));
            Assert.Equal(1024ul, VulkanRingStride.SegmentStrideFor(272, 1024));
        }

        /// <summary>An alignment that is not a power of two was never read off a device, and a zero-byte uniform
        /// buffer has no segment base that means anything.</summary>
        [Fact]
        public void AnImpossibleAlignmentOrSize_IsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanRingStride.AlignmentFor(96));
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanRingStride.SegmentStrideFor(0, 256));
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanRingStride.TotalBytesFor(256, 0, 256));
        }

        /// <summary>The default <see cref="VulkanMemoryFacts"/> carries the floor rather than zero, so a device
        /// read that never happens degrades to the arithmetic every conformant device produces.</summary>
        [Fact]
        public void TheMemoryFactsDefault_IsTheFloorRatherThanZero()
        {
            Assert.Equal(VulkanRingStride.OffsetAlignmentFloor,
                VulkanMemoryFacts.Empty.MinUniformBufferOffsetAlignment);
        }

        // ---- 9.4's Stride row: the BIND WINDOW invariant and the VUID (V-M6) ------------------------------

        /// <summary>
        /// THE INVARIANT THE STRIDE CARRIES: <c>rangeOffset + callerDynamicOffset + range &lt;= stride</c>, so a
        /// frame's window lands inside its own segment and never in a neighbour's. At the LAST frame slot that is
        /// also the difference between a legal bind and one that runs past the end of the buffer, which is
        /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c>.
        /// </summary>
        [Fact]
        public void TheBindWindow_MustFitInsideOneSegment()
        {
            // A window that exactly fills the segment is the tight legal case, and the shipped slot-array shapes
            // below all land on it.
            Assert.True(VulkanRingStride.BindWindowFits(0, 768, 256, 1024));

            // One byte past it is the shape that overruns the buffer at the last frame slot.
            Assert.False(VulkanRingStride.BindWindowFits(0, 768, 257, 1024));
            Assert.False(VulkanRingStride.BindWindowFits(1, 768, 256, 1024));

            ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanRingStride.RequireBindWindowFits(0, 768, 257, 1024));
            Assert.Contains("VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979", ex.Message,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// A RANGE OF THE STRIDE IS THE SHAPE THAT LOOKS SAFE AND IS NOT. It fits only while the caller's own
        /// offset is zero, and it is non-zero in five shipped renderers, so the failure would be a validation
        /// error on ONE frame slot in three rather than on every frame.
        /// </summary>
        [Fact]
        public void ARangeOfTheWholeStride_FitsOnlyWhileTheCallerOffsetIsZero()
        {
            const ulong stride = 1024;

            Assert.True(VulkanRingStride.BindWindowFits(0, 0, stride, stride));
            Assert.False(VulkanRingStride.BindWindowFits(0, 256, stride, stride));
        }

        /// <summary>
        /// EVERY SHIPPED RESOURCE-SET SHAPE, against the invariant, device-free. The engine builds eight
        /// <c>new GpuBufferRange(...)</c> resource sets over a uniform buffer, which are SEVEN distinct shapes
        /// (<c>SpriteBatch</c> builds the same one at construction and again after a grow), and every one of them
        /// is a slot array addressed by a per-draw dynamic offset. Each is swept across the capacities the renderer
        /// actually grows through, because the buffer's size and the largest offset both scale with the capacity
        /// and a single sample would pin one of them by accident.
        /// <para>
        /// Sizes are referenced by their own constant wherever one is reachable, and the private literals are
        /// hardcoded against the line that owns them, which is the same convention
        /// <c>D3D11ResourceModelTests.EveryShippedUniformWindow_BindsACountDirect3D11WillAccept</c> uses.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryShippedResourceSetShape_KeepsItsBindWindowInsideItsSegment()
        {
            (string Site, uint SlotBytes, uint RangeBytes, uint[] Capacities)[] sets =
            {
                // SpriteBatch.cs:198 and :839, GpuBufferRange(_vpUbo, 0, VpPayloadBytes). VpSlotBytes = 256 and
                // VpPayloadBytes = 64 are private consts at SpriteBatch.cs:157-158; capacity starts at 8 and
                // doubles.
                ("SpriteBatch view-projection", 256u, 64u, new uint[] { 8, 16, 32, 64 }),

                // OverlayMeshRenderer.cs:144, GpuBufferRange(_ubo, 0, PayloadBytes). SlotBytes = 256 and
                // PayloadBytes = 128 are private consts at OverlayMeshRenderer.cs:35-36; capacity starts at 8.
                ("OverlayMeshRenderer per-draw", 256u, 128u, new uint[] { 8, 16, 32, 64 }),

                // ShadowMapRenderer.cs:129, GpuBufferRange(_lightUbo, 0, 64). CascadeSlotBytes = 256 is a private
                // const at ShadowMapRenderer.cs:41; the capacity is fixed at MaxCascades.
                ("ShadowMapRenderer cascade", 256u, 64u, new uint[] { (uint)ShadowMapRenderer.MaxCascades }),

                // ShadowMapRenderer.cs:130, GpuBufferRange(_lightUbo, 0, CascadeSlotBytes): the dissolve set binds
                // the FULL slot, which is the tight case where the window exactly fills the segment.
                ("ShadowMapRenderer dissolve", 256u, 256u, new uint[] { (uint)ShadowMapRenderer.MaxCascades }),

                // ShadowMapRenderer.cs:417, GpuBufferRange(_skinnedUbo, 0, SkinnedDepthSlotBytes). Slots start at
                // 8 and double.
                ("ShadowMapRenderer skinned depth", ShadowMapRenderer.SkinnedDepthSlotBytes,
                    ShadowMapRenderer.SkinnedDepthSlotBytes, new uint[] { 8, 16, 32, 64 }),

                // ModelRenderer.cs:664, GpuBufferRange(_skinnedMainUbo, 0, SkinnedMainSlotBytes).
                ("ModelRenderer skinned main", ModelRenderer.SkinnedMainSlotBytes,
                    ModelRenderer.SkinnedMainSlotBytes, new uint[] { 8, 16, 32, 64 }),

                // WaterRenderer.cs:276, GpuBufferRange(_ubo, 0, SlotBytes). Capacity starts at 4 and doubles.
                ("WaterRenderer per-plane", WaterRenderer.SlotBytes, WaterRenderer.SlotBytes,
                    new uint[] { 4, 8, 16, 32 }),
            };

            Assert.Equal(7, sets.Length);

            foreach ((string site, uint slotBytes, uint rangeBytes, uint[] capacities) in sets)
            {
                foreach (uint capacity in capacities)
                {
                    ulong bufferBytes = (ulong)slotBytes * capacity;
                    ulong stride = VulkanRingStride.SegmentStrideFor(bufferBytes, 0);
                    ulong largestDynamicOffset = (ulong)slotBytes * (capacity - 1);

                    Assert.True(VulkanRingStride.BindWindowFits(0, largestDynamicOffset, rangeBytes, stride),
                        $"{site} at capacity {capacity}: a {rangeBytes}-byte window at dynamic offset "
                        + $"{largestDynamicOffset} leaves its own {stride}-byte segment, so on the LAST frame slot "
                        + "the effective offset plus the range runs past the end of the buffer. That is "
                        + "VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979, and it fires on one frame in three "
                        + "rather than on every frame.");

                    // The same statement written the way the VUID states it, over the whole allocation rather than
                    // over one segment, at every frame slot including the last.
                    for (int segment = 0; segment < 3; segment++)
                    {
                        ulong effective = (stride * (ulong)segment) + largestDynamicOffset;
                        Assert.True(effective + rangeBytes <= stride * 3,
                            $"{site} at capacity {capacity}, frame slot {segment}: effective offset {effective} "
                            + $"plus range {rangeBytes} runs past the {stride * 3}-byte buffer.");
                    }
                }
            }
        }

        /// <summary>
        /// A SET CREATED FROM A BARE BUFFER takes the buffer's own logical size as its range and passes no dynamic
        /// offset, which is the "otherwise" half of V-M6. It fits by construction, because the stride is the size
        /// rounded UP, and asserting it is what keeps the two halves of the rule one rule.
        /// </summary>
        [Theory]
        [InlineData(16u)]
        [InlineData(64u)]
        [InlineData(176u)]
        [InlineData(256u)]
        [InlineData(272u)]
        [InlineData(768u)]
        [InlineData(9472u)]
        public void ABareBufferSet_TakesTheWholeLogicalSizeAndStillFits(ulong sizeBytes)
        {
            ulong stride = VulkanRingStride.SegmentStrideFor(sizeBytes, 0);

            Assert.True(VulkanRingStride.BindWindowFits(0, 0, sizeBytes, stride));
        }

        // ---- the segment geometry as the ring itself exposes it ------------------------------------------

        /// <summary>Every segment starts at its own base, and a segment that does not exist is refused rather than
        /// answering an offset into whatever follows the allocation.</summary>
        [Fact]
        public void EverySegment_StartsAtItsOwnBase()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 768, framesInFlight: 3);

            Assert.Equal(0ul, harness.Ring.FrameBaseBytes(0));
            Assert.Equal(768ul, harness.Ring.FrameBaseBytes(1));
            Assert.Equal(1536ul, harness.Ring.FrameBaseBytes(2));
            Assert.Equal(2304ul, harness.Ring.TotalBytes);

            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.FrameBaseBytes(3));
            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.FrameBaseBytes(-1));
        }

        /// <summary>A buffer whose own size is NOT aligned still lands every segment on the dynamic-offset
        /// boundary, which is what the round-up exists for and what makes each base a legal
        /// <c>pDynamicOffsets</c> entry.</summary>
        [Fact]
        public void EveryFrameBase_LandsOnTheDynamicOffsetBoundary()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 272, framesInFlight: 4);

            for (int segment = 0; segment < 4; segment++)
            {
                Assert.Equal(0ul, harness.Ring.FrameBaseBytes(segment) % VulkanRingStride.OffsetAlignmentFloor);
            }
        }

        /// <summary>A write that would leave the LOGICAL buffer is refused, because it would spill into the next
        /// frame's segment, which the GPU may be reading right now. That would present as another frame's uniforms
        /// being subtly wrong rather than as an error at the call.</summary>
        [Fact]
        public void AWritePastTheLogicalEnd_IsRefused()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.Write(250, new byte[8]));
            Assert.Throws<ArgumentOutOfRangeException>(() => harness.Ring.Write(257, new byte[1]));

            // The boundary itself is legal.
            harness.Ring.Write(248, new byte[8]);
        }

        /// <summary>A ring built over a null pointer is refused by name: staging and uniform memory is host-visible
        /// and mapped once at chunk creation, so a zero pointer means the allocation came from a device-local chunk
        /// that was never mapped.</summary>
        [Fact]
        public void ARingOverUnmappedMemory_IsRefused()
        {
            using var harness = new VulkanRingHarness(sizeInBytes: 256, framesInFlight: 3);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => new VulkanUniformRing(harness.Allocator, 0, 256));
        }
    }
}
