using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// MV3's knob, <c>KE_VULKAN_FRAMES_IN_FLIGHT</c>: the parse, the bounds, and the two message bodies. Pure and
    /// device-free, so the lever that settles a measurement is itself settled on any operating system.
    /// <para>
    /// THE UNRECOGNIZED CASE IS THE POINT OF MOST OF THESE ROWS. This variable exists to take a capture at a
    /// specific depth, so a mistyped value that silently left three frames in place would produce a session that
    /// reads as evidence about four and was taken on three. Every rejected form has to come back through the out
    /// parameter so the caller WARNs.
    /// </para>
    /// </summary>
    public sealed class VulkanFramesInFlightTests
    {
        /// <summary>Unset is the default, with nothing to warn about. The overwhelmingly common case, and the one
        /// the exit criterion has to be met at.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Unset_IsTheDefaultAndWarnsAboutNothing(string? value)
        {
            Assert.Equal(VulkanFramesInFlight.Default, VulkanFramesInFlight.Resolve(value, out string? bad));
            Assert.Null(bad);
        }

        /// <summary>Every whole number inside the range is taken as asked, whitespace trimmed.</summary>
        [Theory]
        [InlineData("2", 2)]
        [InlineData("3", 3)]
        [InlineData(" 4 ", 4)]
        [InlineData("16", 16)]
        public void AWholeNumberInRange_IsTakenAsAsked(string value, int expected)
        {
            Assert.Equal(expected, VulkanFramesInFlight.Resolve(value, out string? bad));
            Assert.Null(bad);
        }

        /// <summary>
        /// 1 IS REJECTED HERE AND ACCEPTED ON THE OTHER BACKEND, which is the row that pins the difference rather
        /// than letting it read as a copied constant that drifted. On Direct3D 11 the number sizes constant-buffer
        /// rings only and 1 is an honest degenerate case. Here it would give every list ONE pool, so every Begin
        /// would wait for its own previous record to finish on the GPU: a synchronous round trip per RECORD, which
        /// makes a capture measure the drain rather than the pipeline.
        /// </summary>
        [Fact]
        public void One_IsRejectedBecauseAListWouldWaitOnItsOwnPreviousRecord()
        {
            Assert.Equal(2, VulkanFramesInFlight.Minimum);
            Assert.Equal(VulkanFramesInFlight.Default, VulkanFramesInFlight.Resolve("1", out string? bad));
            Assert.Equal("1", bad);
        }

        /// <summary>Out of range in either direction, and unparseable in any form, comes back verbatim so the
        /// caller can quote what was actually set.</summary>
        [Theory]
        [InlineData("0")]
        [InlineData("-3")]
        [InlineData("17")]
        [InlineData("3.5")]
        [InlineData("three")]
        [InlineData("3x")]
        public void AnythingElse_KeepsTheDefaultAndComesBackVerbatim(string value)
        {
            Assert.Equal(VulkanFramesInFlight.Default, VulkanFramesInFlight.Resolve(value, out string? bad));
            Assert.Equal(value, bad);
        }

        /// <summary>The warning quotes the value and names the range, because the reader of that line is deciding
        /// what to type instead.</summary>
        [Fact]
        public void TheWarning_QuotesTheValueAndNamesTheRange()
        {
            string warning = VulkanFramesInFlight.UnrecognizedWarning("nope");

            Assert.Contains("nope", warning, System.StringComparison.Ordinal);
            Assert.Contains("2", warning, System.StringComparison.Ordinal);
            Assert.Contains("16", warning, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// THE INFO LINE SAYS WHICH DEPTH THIS RUN GOT, AND SAYS IT DIFFERENTLY WHEN IT IS NOT THE DEFAULT. MV3's
        /// exit criterion is a stall count of zero AT THE DEFAULT, so a session log that could not tell the two
        /// apart would let a capture taken at four be read as evidence that three is enough.
        /// </summary>
        [Fact]
        public void TheActiveLine_DistinguishesTheDefaultFromAnOverride()
        {
            string standard = VulkanFramesInFlight.ActiveDescription(VulkanFramesInFlight.Default);
            string overridden = VulkanFramesInFlight.ActiveDescription(5);

            Assert.Contains("the default", standard, System.StringComparison.Ordinal);
            Assert.Contains(VulkanFramesInFlight.EnvVarName, standard, System.StringComparison.Ordinal);

            Assert.Contains("5", overridden, System.StringComparison.Ordinal);
            Assert.Contains("rather than the default", overridden, System.StringComparison.Ordinal);
        }

        /// <summary>The env var follows the engine's <c>KE_</c> convention and is the name the design, the package
        /// README and the usage doc all quote. A rename here without those is a knob nobody can find.</summary>
        [Fact]
        public void TheVariableIsNamedWhatEveryDocumentSaysItIs()
            => Assert.Equal("KE_VULKAN_FRAMES_IN_FLIGHT", VulkanFramesInFlight.EnvVarName);
    }
}
