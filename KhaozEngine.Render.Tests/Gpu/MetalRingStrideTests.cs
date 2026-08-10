using System;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// SECTION 9.4's STRIDE ROW, which is this backend's OWN rather than shared, device-free. Decisions M-M3 and
    /// M-M4 of <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>.
    ///
    /// <para><b>WHY IT IS NOT IN THE SHARED FILE.</b> The three backends run the same ring POLICY and different
    /// stride ARITHMETIC. Direct3D 11 rounds because <c>*SetConstantBuffers1</c> counts in 16-byte constants,
    /// Vulkan takes <c>max(256, minUniformBufferOffsetAlignment)</c> and additionally answers to a VUID, and this
    /// backend floors flat at 256 against a device limit that is 16 on macOS and could have been used. Each owns
    /// its own row and asserts it beside its own reasoning.</para>
    ///
    /// <para><b>WHAT A RED RUN MEANS.</b> Metal's <c>setBufferOffset:atIndex:</c> takes an offset and NO length,
    /// so nothing at runtime would report a bind that reads past its segment: the shader would read the next
    /// frame's uniforms, on the frame slots where there is a next one, silently. There is no validation layer
    /// arm for it and no golden that reliably catches it, which is why the invariant is asserted here over every
    /// shipped set shape rather than left to the device.</para>
    /// </summary>
    public sealed class MetalRingStrideTests
    {
        /// <summary>
        /// THE TWO PLACES 256 IS WRITTEN DOWN AGREE, which is the one thing neither of them can assert alone.
        /// <see cref="MetalDeviceRequirements.UniformRingStride"/> states the constant so that a machine whose
        /// buffer-offset alignment does not divide it is refused at CREATION and never reaches the ring at all,
        /// and it deliberately does not import the number from here for that reason. This is what stops the pair
        /// drifting into a probe that checks one number and a ring that uses another.
        /// </summary>
        [Fact]
        public void TheStrideAndTheDeviceRequirementNameTheSameNumber()
            => Assert.Equal(MetalDeviceRequirements.UniformRingStride, MetalRingStride.SegmentAlignment);

        [Theory]
        [InlineData(1u, 256u)]
        [InlineData(64u, 256u)]
        [InlineData(255u, 256u)]
        [InlineData(256u, 256u)]
        [InlineData(257u, 512u)]
        [InlineData(768u, 768u)]
        [InlineData(8448u, 8448u)]     // ShadowMapRenderer.SkinnedDepthSlotBytes
        [InlineData(9472u, 9472u)]     // ModelRenderer.SkinnedMainSlotBytes
        public void TheStrideIsTheSizeRoundedUpTo256(uint sizeInBytes, uint expected)
            => Assert.Equal(expected, MetalRingStride.SegmentStrideFor(sizeInBytes));

        /// <summary>A FLAT FLOOR, not a maximum against the device (M-M3). The incumbent reports 16 on macOS and
        /// a device-derived stride would pack tighter, which is exactly the tradeoff the design declines: one
        /// number governing all three rings is what lets one shared policy test assert it, and a device-shaped
        /// number under a golden-bearing path is what it refuses to buy the memory with.</summary>
        [Fact]
        public void TheStrideDoesNotDependOnTheDevice()
        {
            // The whole surface takes no alignment argument at all, which is the assertion. The Vulkan sibling's
            // SegmentStrideFor has a second parameter for exactly this and this one does not.
            Assert.Equal(256u, MetalRingStride.SegmentStrideFor(16));
            Assert.Equal(256u, MetalRingStride.SegmentStrideFor(200));
        }

        [Fact]
        public void TheAllocationIsTheStrideTimesTheDepth()
        {
            Assert.Equal(768ul, MetalRingStride.TotalBytesFor(256, 3));
            Assert.Equal(256ul, MetalRingStride.TotalBytesFor(256, 1));
            Assert.Equal(4096ul, MetalRingStride.TotalBytesFor(256, 16));
            Assert.Equal(3ul * 9472, MetalRingStride.TotalBytesFor(ModelRenderer.SkinnedMainSlotBytes, 3));
        }

        [Fact]
        public void AZeroByteUniformBufferIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown =
                Assert.Throws<ArgumentOutOfRangeException>(() => MetalRingStride.SegmentStrideFor(0));

            Assert.Contains("cannot be ring-backed", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADepthBelowOneIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown =
                Assert.Throws<ArgumentOutOfRangeException>(() => MetalRingStride.TotalBytesFor(256, 0));

            Assert.Contains(MetalFramesInFlight.EnvVarName, thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A RANGE OF THE WHOLE STRIDE IS THE SHAPE THAT LOOKS SAFE AND IS NOT. It fits only while the caller's
        /// own offset is zero, and it is non-zero in five shipped renderers, so the failure would be a wrong read
        /// on the frame slots where there IS a next segment rather than on every frame.
        /// </summary>
        [Fact]
        public void ARangeOfTheWholeStrideFitsOnlyWhileTheCallerOffsetIsZero()
        {
            Assert.True(MetalRingStride.BindWindowFits(0, 0, 1024, 1024));
            Assert.False(MetalRingStride.BindWindowFits(0, 256, 1024, 1024));
        }

        [Fact]
        public void AWindowThatLeavesItsSegmentIsRefusedByName()
        {
            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => MetalRingStride.RequireBindWindowFits(0, 256, 1024, 1024));

            Assert.Contains("setBufferOffset: carries no length", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// EVERY SHIPPED RESOURCE-SET SHAPE, against M-M4's invariant, device-free. The engine builds eight
        /// <c>new GpuBufferRange(...)</c> resource sets over a uniform buffer, which are SEVEN distinct shapes
        /// (<c>SpriteBatch</c> builds the same one at construction and again after a grow), and every one of them
        /// is a slot array addressed by a per-draw dynamic offset. Each is swept across the capacities the
        /// renderer actually grows through, because the buffer's size and the largest offset both scale with the
        /// capacity and a single sample would pin one of them by accident.
        /// <para>
        /// The same seven shapes the Vulkan sibling's row asserts, and the sizes are referenced by their own
        /// constant wherever one is reachable, with the private literals hardcoded against the line that owns
        /// them.
        /// </para>
        /// </summary>
        [Fact]
        public void EveryShippedResourceSetShapeKeepsItsBindWindowInsideItsSegment()
        {
            (string Site, uint SlotBytes, uint RangeBytes, uint[] Capacities)[] sets =
            {
                // SpriteBatch.cs, GpuBufferRange(_vpUbo, 0, VpPayloadBytes). VpSlotBytes = 256 and
                // VpPayloadBytes = 64 are private consts there; capacity starts at 8 and doubles.
                ("SpriteBatch view-projection", 256u, 64u, new uint[] { 8, 16, 32, 64 }),

                // OverlayMeshRenderer.cs, GpuBufferRange(_ubo, 0, PayloadBytes). SlotBytes = 256 and
                // PayloadBytes = 128 are private consts there; capacity starts at 8.
                ("OverlayMeshRenderer per-draw", 256u, 128u, new uint[] { 8, 16, 32, 64 }),

                // ShadowMapRenderer.cs, GpuBufferRange(_lightUbo, 0, 64). CascadeSlotBytes = 256 is a private
                // const there; the capacity is fixed at MaxCascades.
                ("ShadowMapRenderer cascade", 256u, 64u, new uint[] { (uint)ShadowMapRenderer.MaxCascades }),

                // ShadowMapRenderer.cs, GpuBufferRange(_lightUbo, 0, CascadeSlotBytes): the dissolve set binds the
                // FULL slot, which is the tight case where the window exactly fills the segment.
                ("ShadowMapRenderer dissolve", 256u, 256u, new uint[] { (uint)ShadowMapRenderer.MaxCascades }),

                // ShadowMapRenderer.cs, GpuBufferRange(_skinnedUbo, 0, SkinnedDepthSlotBytes).
                ("ShadowMapRenderer skinned depth", ShadowMapRenderer.SkinnedDepthSlotBytes,
                    ShadowMapRenderer.SkinnedDepthSlotBytes, new uint[] { 8, 16, 32, 64 }),

                // ModelRenderer.cs, GpuBufferRange(_skinnedMainUbo, 0, SkinnedMainSlotBytes).
                ("ModelRenderer skinned main", ModelRenderer.SkinnedMainSlotBytes,
                    ModelRenderer.SkinnedMainSlotBytes, new uint[] { 8, 16, 32, 64 }),

                // WaterRenderer.cs, GpuBufferRange(_ubo, 0, SlotBytes). Capacity starts at 4 and doubles.
                ("WaterRenderer per-plane", WaterRenderer.SlotBytes, WaterRenderer.SlotBytes,
                    new uint[] { 4, 8, 16, 32 }),
            };

            Assert.Equal(7, sets.Length);

            foreach ((string site, uint slotBytes, uint rangeBytes, uint[] capacities) in sets)
            {
                foreach (uint capacity in capacities)
                {
                    uint bufferBytes = slotBytes * capacity;
                    uint stride = MetalRingStride.SegmentStrideFor(bufferBytes);
                    uint largestDynamicOffset = slotBytes * (capacity - 1);

                    Assert.True(MetalRingStride.BindWindowFits(0, largestDynamicOffset, rangeBytes, stride),
                        $"{site} at capacity {capacity}: a {rangeBytes}-byte window at dynamic offset "
                        + $"{largestDynamicOffset} leaves its own {stride}-byte segment, so a bind on any frame "
                        + "slot but the last would read into the NEXT frame's uniforms and the last would read "
                        + "past the buffer. Metal reports neither.");

                    // The same statement over the whole allocation rather than over one segment, at every frame
                    // slot including the last, which is where it becomes a read past the buffer.
                    for (int segment = 0; segment < MetalFramesInFlight.Default; segment++)
                    {
                        ulong effective = ((ulong)stride * (ulong)segment) + largestDynamicOffset;
                        Assert.True(effective + rangeBytes
                                <= MetalRingStride.TotalBytesFor(bufferBytes, MetalFramesInFlight.Default),
                            $"{site} at capacity {capacity}, frame slot {segment}: effective offset {effective} "
                            + $"plus range {rangeBytes} runs past the allocation.");
                    }
                }
            }
        }

        /// <summary>
        /// A SET CREATED FROM A BARE BUFFER takes the buffer's own logical size as its window and passes no
        /// dynamic offset, which is the "otherwise" half of M-M4. It fits by construction, because the stride is
        /// the size rounded UP, and asserting it is what keeps the two halves of the rule one rule.
        /// </summary>
        [Theory]
        [InlineData(16u)]
        [InlineData(64u)]
        [InlineData(176u)]
        [InlineData(256u)]
        [InlineData(272u)]
        [InlineData(768u)]
        [InlineData(9472u)]
        public void ABareBufferSetTakesTheWholeLogicalSizeAndStillFits(uint sizeBytes)
            => Assert.True(
                MetalRingStride.BindWindowFits(0, 0, sizeBytes, MetalRingStride.SegmentStrideFor(sizeBytes)));
    }
}
