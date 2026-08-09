using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// DECISION M-N1's policy, driven device-free. <c>KE_METAL_DEVICE</c> is parsed, applied to a list of
    /// candidates a test writes by hand, and every warning and log line it produces is asserted, so the whole
    /// lever runs on the Linux and Windows legs where there is no Metal at all.
    /// <para>
    /// THE ONE SHAPE DIFFERENCE FROM THE VULKAN SIBLING IS THE DEFAULT, and it is worth stating here because it
    /// is what the rows below do NOT test. An unset variable takes <c>MTLCreateSystemDefaultDevice()</c> rather
    /// than element zero of the enumeration, so <see cref="MetalDeviceSelection.Choose"/> is only ever reached on
    /// the enumerated path. That is not an optimisation: the incumbent calls that function, section 14 compares
    /// <c>DeviceName</c> under a zero-permitted-difference bar, and taking the array's first element instead
    /// would swap the GPU underneath the one gate that has to isolate the backend swap.
    /// </para>
    /// </summary>
    public sealed class MetalDeviceSelectionTests
    {
        static MetalDeviceCandidate Eligible(string name, bool lowPower = false, bool removable = false,
            bool headless = false, ulong registryId = 1)
            => new(name, lowPower, removable, headless, registryId, MeetsRequirements: true, RejectionReason: null);

        static MetalDeviceCandidate Ineligible(string name, string reason, bool lowPower = false)
            => new(name, lowPower, IsRemovable: false, IsHeadless: false, RegistryId: 9,
                MeetsRequirements: false, RejectionReason: reason);

        /// <summary>
        /// Every form the variable takes, by NAME rather than by enum member, because the enum is internal to the
        /// package and an xUnit theory parameter has to be as public as the test class.
        /// </summary>
        [Theory]
        [InlineData(null, "Default")]
        [InlineData("", "Default")]
        [InlineData("   ", "Default")]
        [InlineData("discrete", "Discrete")]
        [InlineData("DISCRETE", "Discrete")]
        [InlineData("integrated", "Integrated")]
        [InlineData("low-power", "LowPower")]
        [InlineData("lowPower", "LowPower")]
        [InlineData("2", "Index")]
        [InlineData(" 2 ", "Index")]
        [InlineData("-1", "Index")]
        [InlineData("Radeon", "NameSubstring")]
        public void Parse_ReadsEveryFormTheVariableTakes(string? value, string expected)
            => Assert.Equal(expected, MetalDeviceSelection.Parse(value).Kind.ToString());

        /// <summary>
        /// There is deliberately no unrecognized case. Anything that is neither one of the three names nor an
        /// integer is a NAME SUBSTRING, because that is the only reading under which somebody typing their GPU's
        /// name gets what they meant. Whether it can be satisfied is <see cref="MetalDeviceSelection.Choose"/>'s
        /// question, which is where the warning lives.
        /// </summary>
        [Fact]
        public void Parse_KeepsTheRawValueVerbatimForTheWarningToQuote()
        {
            MetalDeviceRequest request = MetalDeviceSelection.Parse("  Apple M2 Max  ");

            Assert.Equal("NameSubstring", request.Kind.ToString());
            Assert.Equal("Apple M2 Max", request.Name);
            // A stray space is exactly what a warning has to make visible, so the raw value is not trimmed.
            Assert.Equal("  Apple M2 Max  ", request.RawValue);
        }

        [Fact]
        public void AnIndexIsHonoured_WhenItNamesAnEligibleDevice()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max"), Eligible("AMD Radeon Pro")];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("1"), devices, out string? warning);

            Assert.Equal(1, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// AN INELIGIBLE DEVICE IS NEVER CHOSEN, on any path including an explicit index, and the warning says
        /// why in the device's own words. Honouring the pin would trade a warning now for a crash on frame one,
        /// which is the trade the whole functional probe exists to refuse.
        /// </summary>
        [Fact]
        public void AnIndexOntoAnIneligibleDevice_WarnsAndFallsBack()
        {
            MetalDeviceCandidate[] devices =
            [
                Eligible("Apple M2 Max"),
                Ineligible("Ancient GPU", "the Metal device answers supportsFamily: for neither"),
            ];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("1"), devices, out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("supportsFamily:", warning, StringComparison.Ordinal);
            Assert.Contains("crash on frame one", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void AnIndexOutOfRange_WarnsWithTheCountAndTheList()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max")];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("7"), devices, out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("enumerates 1", warning, StringComparison.Ordinal);
            Assert.Contains("Apple M2 Max", warning, StringComparison.Ordinal);
        }

        [Fact]
        public void ANameSubstring_MatchesCaseInsensitively()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max"), Eligible("AMD Radeon Pro W6800X")];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("radeon"), devices,
                out string? warning);

            Assert.Equal(1, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// A name substring is machine-specific by nature, so a value that is right on the machine it was written
        /// on is wrong on the next one. That must WARN and fall back rather than fail, or a diagnostic lever
        /// becomes a way of bricking a session, and the warning lists what was really enumerated because "nothing
        /// matched" on its own sends the reader to check their spelling.
        /// </summary>
        [Fact]
        public void ANameThatMatchesNothing_WarnsWithTheEnumerationAndFallsBack()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max")];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("Radeon"), devices,
                out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("Radeon", warning, StringComparison.Ordinal);
            Assert.Contains("enumerated: 0='Apple M2 Max'", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// METAL HAS EXACTLY ONE CLASSIFICATION FLAG, <c>-isLowPower</c>, and no <c>isDiscrete</c> and no device
        /// type enumeration of the kind Vulkan has. So <c>discrete</c> is the NEGATION of low-power, and
        /// <c>integrated</c> and <c>low-power</c> are the same predicate under two names. These rows pin that
        /// reading, because it is the one place a reader coming from the Vulkan sibling would expect a richer
        /// taxonomy that the API cannot support.
        /// </summary>
        [Theory]
        [InlineData("discrete", 1)]
        [InlineData("integrated", 0)]
        [InlineData("low-power", 0)]
        public void ThePowerTokens_SelectOnTheOneFlagMetalHas(string value, int expected)
        {
            MetalDeviceCandidate[] devices =
            [
                Eligible("Intel Iris Pro", lowPower: true),
                Eligible("AMD Radeon Pro"),
            ];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse(value), devices,
                out string? warning);

            Assert.Equal(expected, chosen);
            Assert.Null(warning);
        }

        [Fact]
        public void AskingForDiscreteOnAMachineWithOnlyLowPower_WarnsAndFallsBack()
        {
            MetalDeviceCandidate[] devices = [Eligible("Intel Iris Pro", lowPower: true)];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("discrete"), devices,
                out string? warning);

            Assert.Equal(0, chosen);
            Assert.NotNull(warning);
            Assert.Contains("Metal has no discrete flag", warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// Nothing qualifying is NOT a warning case, deliberately. There is nothing to warn ABOUT: the caller's
        /// failure names every device and its own rejection reason, which is strictly more than a warning about a
        /// variable could say.
        /// </summary>
        [Fact]
        public void NoEligibleDevice_ReportsNoDeviceRatherThanIndexZero()
        {
            MetalDeviceCandidate[] devices =
            [
                Ineligible("Ancient GPU", "below the family floor"),
                Ineligible("Odd GPU", "reports no name"),
            ];

            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("0"), devices, out string? warning);

            Assert.Equal(MetalDeviceSelection.NoDevice, chosen);
            Assert.Null(warning);
        }

        [Fact]
        public void AnEmptyEnumeration_ReportsNoDevice()
        {
            int chosen = MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("discrete"),
                Array.Empty<MetalDeviceCandidate>(), out string? warning);

            Assert.Equal(MetalDeviceSelection.NoDevice, chosen);
            Assert.Null(warning);
        }

        /// <summary>
        /// A SELECTION AND A SUBSTITUTION READ DIFFERENTLY, which is the whole reason M-N1 asks for the line at
        /// all. A soak session comparing this backend against the incumbent has to tell "this run chose device 1"
        /// from "the request could not be honoured and device 0 was used", because those are different machines
        /// from the measurement's point of view.
        /// </summary>
        [Fact]
        public void Describe_SaysSelectionWhenTheRequestWasHonoured()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max", registryId: 4294967377)];

            string line = MetalDeviceSelection.Describe(0, MetalDeviceSelection.Parse("Apple"), devices,
                requestHonoured: true);

            Assert.Contains("SELECTION", line, StringComparison.Ordinal);
            Assert.Contains("Apple M2 Max", line, StringComparison.Ordinal);
            Assert.Contains("4294967377", line, StringComparison.Ordinal);
            Assert.DoesNotContain("SUBSTITUTED", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Describe_SaysSubstitutedWhenItWasNot()
        {
            MetalDeviceCandidate[] devices = [Eligible("Apple M2 Max")];

            string line = MetalDeviceSelection.Describe(0, MetalDeviceSelection.Parse("Radeon"), devices,
                requestHonoured: false);

            Assert.Contains("SUBSTITUTED", line, StringComparison.Ordinal);
            Assert.Contains("not comparable", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// The default path names the lever, because a session log that never mentions a variable is a session
        /// log in which nobody discovers it exists. It also says the incumbent uses the same device, which is the
        /// sentence that makes a capability comparison meaningful.
        /// </summary>
        [Fact]
        public void DescribeSystemDefault_NamesTheVariableAndTheIncumbent()
        {
            string line = MetalDeviceSelection.DescribeSystemDefault("Apple M2 Max");

            Assert.Contains(MetalDeviceSelection.EnvVarName, line, StringComparison.Ordinal);
            Assert.Contains("incumbent", line, StringComparison.Ordinal);
            Assert.Contains("Apple M2 Max", line, StringComparison.Ordinal);
        }

        /// <summary>
        /// The three read-only traits reach the enumeration list, so a reader of a warning can tell a low-power
        /// integrated GPU from an external one from a device driving no display. None of them is selected ON
        /// except low-power, and reporting the other two is what stops a tester guessing why a machine behaved
        /// differently.
        /// </summary>
        [Fact]
        public void TheEnumerationList_ReportsEveryTraitAndTheIneligibility()
        {
            MetalDeviceCandidate[] devices =
            [
                Eligible("Intel Iris Pro", lowPower: true),
                Ineligible("Old eGPU", "below the family floor"),
                Eligible("Apple M2 Max", headless: true),
            ];

            MetalDeviceSelection.Choose(MetalDeviceSelection.Parse("Nothing"), devices, out string? warning);

            Assert.NotNull(warning);
            Assert.Contains("0='Intel Iris Pro' (low-power)", warning, StringComparison.Ordinal);
            Assert.Contains("1='Old eGPU' INELIGIBLE", warning, StringComparison.Ordinal);
            Assert.Contains("2='Apple M2 Max' (headless)", warning, StringComparison.Ordinal);
        }
    }
}
