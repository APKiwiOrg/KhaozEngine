using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <c>KE_METAL_FRAMES_IN_FLIGHT</c>, MM4's lever, parsed. Row 7 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c> owns the constant and row 8's uniform ring
    /// reads it.
    /// <para>
    /// DEVICE-FREE AND DELIBERATELY SO. Everything here is a parse and a bound, so it runs on the Linux and
    /// Windows legs where the number still governs a ring that will be built there too.
    /// </para>
    /// </summary>
    public sealed class MetalFramesInFlightTests
    {
        [Fact]
        public void UnsetTakesTheDefault()
        {
            Assert.Equal(MetalFramesInFlight.Default, MetalFramesInFlight.Resolve(null, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankTakesTheDefaultAndIsNotUnrecognized(string value)
        {
            Assert.Equal(MetalFramesInFlight.Default, MetalFramesInFlight.Resolve(value, out string? unrecognized));

            // A blank value is somebody clearing the variable, not somebody mistyping it, so it must not warn.
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData("1", 1)]
        [InlineData("2", 2)]
        [InlineData("3", 3)]
        [InlineData("  4  ", 4)]
        [InlineData("16", 16)]
        public void AWholeNumberInRangeIsTaken(string value, int expected)
        {
            Assert.Equal(expected, MetalFramesInFlight.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// THE FLOOR IS 1 HERE AND 2 ON VULKAN, and that difference is the point of this row rather than a
        /// copied constant drifting. The Vulkan floor is 2 because at 1 every list owns ONE command pool and
        /// every Begin waits for its own previous record: a synchronous round trip per RECORD. This backend has
        /// no pool at all (M-R2), so 1 is the honest degenerate case rather than a trap.
        /// </summary>
        [Fact]
        public void OneIsLegalHereBecauseThereIsNoCommandBufferPool()
        {
            Assert.Equal(1, MetalFramesInFlight.Minimum);
            Assert.Equal(1, MetalFramesInFlight.Resolve("1", out string? unrecognized));
            Assert.Null(unrecognized);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("-1")]
        [InlineData("17")]
        [InlineData("three")]
        [InlineData("3.5")]
        public void OutOfRangeAndUnparseableBothWarnAndKeepTheDefault(string value)
        {
            Assert.Equal(MetalFramesInFlight.Default, MetalFramesInFlight.Resolve(value, out string? unrecognized));

            // Verbatim, because the warning quotes what was actually set: a capture taken at the default after a
            // mistype reads as evidence about the value the tester THOUGHT they set otherwise.
            Assert.Equal(value, unrecognized);
        }

        [Fact]
        public void TheWarningNamesTheVariableTheRangeAndWhyTheFloorIsNotTheVulkanOne()
        {
            string warning = MetalFramesInFlight.UnrecognizedWarning("nope");

            Assert.Contains(MetalFramesInFlight.EnvVarName, warning, System.StringComparison.Ordinal);
            Assert.Contains("nope", warning, System.StringComparison.Ordinal);
            Assert.Contains("1", warning, System.StringComparison.Ordinal);
            Assert.Contains("16", warning, System.StringComparison.Ordinal);
            Assert.Contains("command-buffer pool", warning, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The INFO line has to say the depth this run got, because MM4's exit criterion is a backpressure count
        /// taken AT a depth, so the number and the count belong in one session log.
        /// </summary>
        [Fact]
        public void TheActiveLineDistinguishesTheDefaultFromAnOverride()
        {
            string atDefault = MetalFramesInFlight.ActiveDescription(MetalFramesInFlight.Default);
            Assert.Contains("the default", atDefault, System.StringComparison.Ordinal);

            string overridden = MetalFramesInFlight.ActiveDescription(5);
            Assert.Contains("5", overridden, System.StringComparison.Ordinal);
            Assert.Contains(MetalFramesInFlight.EnvVarName, overridden, System.StringComparison.Ordinal);
            Assert.DoesNotContain("the default: ", overridden, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// AND IT DESCRIBES WHAT EXISTS, WHICH IS THE HALF THAT KEEPS MOVING. Row 7 wrote this line when NOTHING
        /// consumed the depth and pinned that wording, precisely so a later row could not leave a false claim in
        /// the one session log MM4's measurement is read out of. Row 15 landed the last of the three consumers, so
        /// the line names all three and no longer defers one, and this asserts that rather than the sentence.
        /// </summary>
        [Theory]
        [InlineData(MetalFramesInFlight.Default)]
        [InlineData(5)]
        public void TheActiveLineNamesEveryConsumerOfTheDepth(int frames)
        {
            string line = MetalFramesInFlight.ActiveDescription(frames);

            Assert.DoesNotContain("Nothing consumes that depth", line, System.StringComparison.Ordinal);
            Assert.Contains("ring", line, System.StringComparison.Ordinal);
            Assert.Contains("staging arena", line, System.StringComparison.Ordinal);
            Assert.Contains("maximumDrawableCount", line, System.StringComparison.Ordinal);

            // AND IT NO LONGER DEFERS ANY OF THEM. The two shapes the line used to carry were "the drawable queue
            // is not sized yet" and "row 15's", and both are false the moment a windowed device exists. A line
            // that still names a row as pending is the exact failure row 7 pinned this wording against, arriving
            // from the other direction.
            Assert.DoesNotContain("not sized yet", line, System.StringComparison.Ordinal);
            Assert.DoesNotContain("when the swapchain lands", line, System.StringComparison.Ordinal);

            // AND IT SAYS RECORDINGS. Both things the depth sizes rotate at a command list's Begin and a frame
            // opens several lists, so a session log that offered the number as frames of headroom would be
            // overstating it by exactly the number of lists the frame opens. This is the line MM4's exit criterion
            // is read out of, which is why the vocabulary is pinned rather than left to prose.
            Assert.Contains("RECORDING", line, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// THE UNCOMMITTED BOUND (section 6.1) is the depth PLUS ONE, and the one is M-W6's present command
        /// buffer. Pinned here rather than left as arithmetic at the call site, because it is the number a
        /// device-free test asserts a whole frame's recording against.
        /// </summary>
        [Theory]
        [InlineData(1, 2)]
        [InlineData(3, 4)]
        [InlineData(16, 17)]
        public void TheUncommittedBoundIsTheDepthPlusThePresentBuffer(int frames, int expected)
            => Assert.Equal(expected, MetalFramesInFlight.UncommittedBufferBound(frames));
    }
}
