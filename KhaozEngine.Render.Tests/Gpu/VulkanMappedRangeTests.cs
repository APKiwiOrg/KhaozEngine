using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The <c>nonCoherentAtomSize</c> widening a <c>VkMappedMemoryRange</c> requires (section 9.1, V-M4): the
    /// offset rounds DOWN, the end rounds UP, and the end clamps to the memory object's own size, which is the
    /// spec's "or the remainder to the end of the memory object" clause rather than a safety net.
    /// <para>
    /// The incumbent emitted neither <c>vkFlushMappedMemoryRanges</c> nor <c>vkInvalidateMappedMemoryRanges</c>
    /// anywhere, so there is no prior art in this engine to compare against and the rules come straight off
    /// <c>VUID-VkMappedMemoryRange-offset-00687</c>, <c>-size-01389</c> and <c>-size-01390</c>. That is exactly the
    /// kind of arithmetic that is written once, believed forever and wrong.
    /// </para>
    /// </summary>
    public sealed class VulkanMappedRangeTests
    {
        /// <summary>Identity cases include atom size one, aligned ranges, the whole object and zero length.</summary>
        [Theory]
        [InlineData(37ul, 91ul, 4096ul, 1ul)]
        [InlineData(128ul, 256ul, 4096ul, 64ul)]
        [InlineData(0ul, 4096ul, 4096ul, 256ul)]
        [InlineData(128ul, 0ul, 4096ul, 64ul)]
        public void AnIdentityRange_IsUnchanged(ulong requestedOffset, ulong requestedSize, ulong memorySize, ulong atom)
        {
            VulkanMappedRange.Align(requestedOffset, requestedSize, memorySize, atom, out ulong offset, out ulong size);

            Assert.Equal(requestedOffset, offset);
            Assert.Equal(requestedSize, size);
        }

        /// <summary>The offset rounds DOWN and the size grows to compensate, so the requested bytes stay covered.
        /// Rounding the offset up would leave the first bytes of the range unflushed, which is the failure that
        /// looks like a partially written buffer.</summary>
        [Fact]
        public void TheOffsetRoundsDown_AndTheRangeStillCoversWhatWasAsked()
        {
            VulkanMappedRange.Align(100, 50, 4096, 64, out ulong offset, out ulong size);

            Assert.Equal(64ul, offset);
            Assert.Equal(0ul, offset % 64);
            Assert.Equal(128ul, size);
            Assert.Equal(0ul, size % 64);

            // The whole requested range is inside the widened one, which is the property that matters.
            Assert.True(offset <= 100);
            Assert.True(offset + size >= 150);
        }

        /// <summary>
        /// THE END CLAMPS TO THE MEMORY OBJECT rather than rounding past it. A memory object whose own size is not
        /// a multiple of the atom would otherwise produce a range that ends past it, which is the invalid form,
        /// and the spec permits the remainder exactly for this case.
        /// </summary>
        [Fact]
        public void ARangeReachingTheEnd_ClampsInsteadOfRoundingPastIt()
        {
            // A 200-byte object with a 64-byte atom: 200 is not a multiple of 64.
            VulkanMappedRange.Align(190, 10, 200, 64, out ulong offset, out ulong size);

            Assert.Equal(128ul, offset);
            Assert.Equal(72ul, size);
            Assert.Equal(200ul, offset + size);
        }

        /// <summary>
        /// THE WIDENING IS ALWAYS OUTWARDS, over every combination of a coarse atom and an awkward range. This is
        /// the property the whole type exists for: a widened range that ever narrowed would leave bytes unflushed
        /// or uninvalidated, which is a data-visibility defect that reproduces on one driver and not another.
        /// </summary>
        [Theory]
        [InlineData(0ul, 1ul, 64ul)]
        [InlineData(1ul, 1ul, 64ul)]
        [InlineData(63ul, 2ul, 64ul)]
        [InlineData(65ul, 300ul, 64ul)]
        [InlineData(255ul, 1ul, 256ul)]
        [InlineData(4095ul, 1ul, 4096ul)]
        [InlineData(1000ul, 3000ul, 128ul)]
        public void WideningAlwaysCoversTheRequestedRange(ulong offset, ulong size, ulong atom)
        {
            const ulong memorySize = 1 << 16;

            VulkanMappedRange.Align(offset, size, memorySize, atom, out ulong widened, out ulong widenedSize);

            Assert.Equal(0ul, widened % atom);
            Assert.True(widened <= offset, "the widened offset moved forwards");
            Assert.True(widened + widenedSize >= offset + size, "the widened end moved backwards");
            Assert.True(widened + widenedSize <= memorySize, "the widened end ran past the memory object");
            Assert.True(widenedSize % atom == 0 || widened + widenedSize == memorySize,
                "the widened size is neither a multiple of the atom nor the remainder to the end");
        }

        /// <summary>An atom size that is zero or not a power of two did not come off a device limit, and the spec
        /// requires <c>nonCoherentAtomSize</c> to be a power of two.</summary>
        [Theory]
        [InlineData(0ul)]
        [InlineData(3ul)]
        [InlineData(100ul)]
        public void ANonPowerOfTwoAtomSize_Throws(ulong atom)
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanMappedRange.Align(0, 16, 4096, atom, out _, out _));

        /// <summary>A range that starts or ends outside the memory object is engine-internal misuse, and it
        /// throws rather than being clamped: clamping would silently flush less than the caller asked for.</summary>
        [Fact]
        public void ARangeOutsideTheMemoryObject_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanMappedRange.Align(4097, 1, 4096, 64, out _, out _));

            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanMappedRange.Align(4000, 200, 4096, 64, out _, out _));
        }
    }
}
