using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// M-N4's four reads as a DECISION, driven device-free from fabricated facts, one requirement at a time.
    /// <para>
    /// This is the half of the probe that runs everywhere. Reading the values needs macOS and a real Metal
    /// device, which two of the three <c>ci.yml</c>-adjacent legs do not have, so a probe written as one method
    /// would have every one of its requirements covered on exactly one leg. Split, every requirement is covered
    /// on every leg, including the ones no rig in this fleet could produce hardware to fail: nobody here owns a
    /// Mac that reports a 512-byte buffer alignment, and that is precisely the machine the alignment check
    /// exists for.
    /// </para>
    /// <para>
    /// Each row changes ONE field away from a passing snapshot, so a red test names the requirement rather than
    /// the struct. The passing snapshot is this developer machine's own reading (an Apple M2 Max under macOS
    /// 26.6), which keeps the fabricated values honest: an Apple silicon Mac answers Apple1 through Apple8 and
    /// Mac2, reports a 16-byte buffer-offset alignment, and supports sample count 1.
    /// </para>
    /// </summary>
    public sealed class MetalDeviceRequirementsTests
    {
        // The measured shape of a machine that passes, which every row below mutates exactly one field of.
        static MetalDeviceFacts Passing() => new(
            DeviceCreated: true,
            DeviceName: "Apple M2 Max",
            HighestAppleFamily: 8,
            SupportsMac2: true,
            SupportsCommon1: true,
            BufferOffsetAlignment: 16,
            BufferOffsetAlignmentSource: "-minimumLinearTextureAlignmentForPixelFormat: (BGRA8Unorm)",
            SupportsTextureSampleCount1: true);

        [Fact]
        public void AMachineThatMeetsEveryRequirement_IsAccepted()
            => Assert.Null(MetalDeviceRequirements.MissingRequirement(Passing()));

        /// <summary>
        /// The floor the incumbent's own support check stops at (M-N4), and the only one of the five that a Mac
        /// can fail without the device answering anything at all.
        /// </summary>
        [Fact]
        public void NoDevice_IsRefusedByNamingTheCreationCall()
        {
            MetalDeviceFacts facts = Passing() with { DeviceCreated = false };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains("MTLCreateSystemDefaultDevice", missing, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// A nameless device is refused rather than tolerated, because <c>GpuCapabilities.DeviceName</c> is
        /// compared field for field against the incumbent under M-G1's zero-permitted-difference bar. Whitespace
        /// counts as nameless: a device reporting a space would satisfy a null check and fail the comparison.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void ADeviceWithNoName_IsRefused(string name)
        {
            MetalDeviceFacts facts = Passing() with { DeviceName = name };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains("no name", missing, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// The family floor is an OR over two answers, not one, and both arms are asserted because a machine
        /// reaches it from either side: an Apple silicon Mac answers both, an Intel Mac on a supported macOS
        /// answers Mac2 alone, and a device answering neither is below anything this engine ships on.
        /// </summary>
        [Theory]
        [InlineData(8, true)]     // Apple silicon, which answers both
        [InlineData(0, true)]     // an Intel Mac, which answers Mac2 and no Apple family
        [InlineData(1, false)]    // the lowest Apple family on its own
        public void EitherArmOfTheFamilyFloor_IsEnough(int highestApple, bool mac2)
        {
            MetalDeviceFacts facts = Passing() with { HighestAppleFamily = highestApple, SupportsMac2 = mac2 };

            Assert.Null(MetalDeviceRequirements.MissingRequirement(facts));
        }

        [Fact]
        public void ADeviceBelowBothArmsOfTheFamilyFloor_IsRefused()
        {
            MetalDeviceFacts facts = Passing() with { HighestAppleFamily = 0, SupportsMac2 = false };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains("supportsFamily:", missing, System.StringComparison.Ordinal);
            // The diagnostic says whether the device answered the shared baseline, so a log line separates a Mac
            // that sits below the floor from one that answers nothing at all.
            Assert.Contains("Common1", missing, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ONE THAT WOULD OTHERWISE CORRUPT SILENTLY, which is why row 2's issue singles it out. Every
        /// uniform bind lands at a multiple of M-M3's 256-byte stride, so a device whose own minimum does not
        /// divide 256 would have every one of those binds at an offset it never agreed to. The requirement is
        /// stated as divisibility rather than as "at or below 256", which is the same check on every real
        /// device (they are all powers of two) and the correct one on a device that is not.
        /// </summary>
        [Theory]
        [InlineData(4u)]
        [InlineData(16u)]
        [InlineData(32u)]
        [InlineData(64u)]
        [InlineData(256u)]
        public void AnAlignmentThatDivides256_IsAccepted(uint alignment)
        {
            MetalDeviceFacts facts = Passing() with { BufferOffsetAlignment = alignment };

            Assert.Null(MetalDeviceRequirements.MissingRequirement(facts));
        }

        [Theory]
        [InlineData(512u)]    // coarser than the ring's stride
        [InlineData(96u)]     // finer than the stride, and not a divisor of it
        public void AnAlignmentTheRingStrideIsNotAMultipleOf_IsRefused(uint alignment)
        {
            MetalDeviceFacts facts = Passing() with { BufferOffsetAlignment = alignment };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains(alignment.ToString(System.Globalization.CultureInfo.InvariantCulture), missing,
                System.StringComparison.Ordinal);
            // The selector that produced the number, not just the number. A refusal a tester cannot trace back
            // to a read is a refusal they cannot check.
            Assert.Contains("minimumLinearTextureAlignmentForPixelFormat:", missing,
                System.StringComparison.Ordinal);
        }

        /// <summary>
        /// An unreadable alignment refuses in the CONSERVATIVE direction, and that is a deliberate choice rather
        /// than an oversight. Metal exposes no constant-buffer-specific query at all (measured, see
        /// <see cref="MetalDeviceFactsReader.ReadBufferOffsetAlignment"/>), so the probe reads the closest thing the
        /// API does answer, and a device answering none of them leaves the ring's one load-bearing number
        /// unchecked. Refusing there costs a Mac a fallback to the incumbent, and accepting there costs every
        /// ring bind.
        /// </summary>
        [Fact]
        public void AnUnreadableAlignment_IsRefused()
        {
            MetalDeviceFacts facts = Passing() with
            {
                BufferOffsetAlignment = 0,
                BufferOffsetAlignmentSource = "no buffer-offset alignment selector this device answers",
            };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains("would not report a buffer-offset alignment", missing, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// <c>supportsTextureSampleCount:</c> is the only sample-count query Metal has, and M-C3's
        /// <c>MaxMsaaSampleCount</c> walks upward from 1. A device refusing 1 has no sample count this backend
        /// can offer at all, which is a refusal rather than a capability difference.
        /// </summary>
        [Fact]
        public void ADeviceThatRefusesSampleCount1_IsRefused()
        {
            MetalDeviceFacts facts = Passing() with { SupportsTextureSampleCount1 = false };

            string? missing = MetalDeviceRequirements.MissingRequirement(facts);

            Assert.NotNull(missing);
            Assert.Contains("supportsTextureSampleCount:", missing, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// Every refusal is a SENTENCE, because of where it ends up: the provider logs it and the creation path
        /// puts it into the exception the fallback WARNs with. An empty or whitespace refusal reads in a session
        /// log as a machine turned away for no reason, so null is the only yes.
        /// </summary>
        [Fact]
        public void EveryRefusal_IsSomethingATesterCanRead()
        {
            MetalDeviceFacts[] failures =
            {
                Passing() with { DeviceCreated = false },
                Passing() with { DeviceName = "" },
                Passing() with { HighestAppleFamily = 0, SupportsMac2 = false },
                Passing() with { BufferOffsetAlignment = 0 },
                Passing() with { BufferOffsetAlignment = 512 },
                Passing() with { SupportsTextureSampleCount1 = false },
            };

            foreach (MetalDeviceFacts facts in failures)
            {
                string? missing = MetalDeviceRequirements.MissingRequirement(facts);
                Assert.NotNull(missing);
                Assert.False(string.IsNullOrWhiteSpace(missing));
            }
        }
    }
}
