using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// <c>KE_VULKAN_ACQUIRE</c>, MV2'S KILL SWITCH, parsed. Pure, so the whole decision runs on any operating
    /// system with no Vulkan loader, matching the frames-in-flight and validation knobs beside it.
    /// <para>
    /// The unrecognised case earns its own assertions because this variable exists to settle a MEASUREMENT: a
    /// mistyped value that silently left the default in place would produce a capture that reads as evidence about
    /// the stall path and was taken on the semaphore path.
    /// </para>
    /// </summary>
    public sealed class VulkanAcquireModeTests
    {
        /// <summary>Unset, empty and whitespace all leave the shipped path, which is the semaphore acquire.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingSetLeavesTheSemaphoreAcquire(string? value)
        {
            Assert.Equal(VulkanAcquireMode.Semaphore, VulkanAcquire.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>The one value that changes anything, case-insensitively and with surrounding whitespace
        /// trimmed, because a value pasted out of a shell script carries both.</summary>
        [Theory]
        [InlineData("stall")]
        [InlineData("STALL")]
        [InlineData("  Stall  ")]
        public void TheStallValueSelectsTheIncumbentsShape(string value)
        {
            Assert.Equal(VulkanAcquireMode.Stall, VulkanAcquire.Resolve(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>The default is selectable explicitly, so a session can pin the shipped path in a script rather
        /// than relying on the variable being unset.</summary>
        [Fact]
        public void TheSemaphoreValueSelectsTheDefaultExplicitly()
        {
            Assert.Equal(VulkanAcquireMode.Semaphore, VulkanAcquire.Resolve("semaphore", out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>A value that was set and understood as nothing comes back VERBATIM, so the caller can warn
        /// with what the tester actually typed rather than with a paraphrase.</summary>
        [Fact]
        public void AnUnrecognisedValueComesBackVerbatim()
        {
            Assert.Equal(VulkanAcquireMode.Semaphore, VulkanAcquire.Resolve("stal", out string? unrecognized));
            Assert.Equal("stal", unrecognized);
            Assert.Contains("stal", VulkanAcquire.UnrecognizedWarning("stal"), System.StringComparison.Ordinal);
        }

        /// <summary>
        /// THE TWO DESCRIPTIONS ARE DIFFERENT SENTENCES, so a capture proves the position its acquire-wait
        /// counters were measured in. MV2's exit criterion compares two captures, and a capture that cannot say
        /// which side it is from settles nothing.
        /// </summary>
        [Fact]
        public void EachModeDescribesItself()
        {
            string semaphore = VulkanAcquire.ActiveDescription(VulkanAcquireMode.Semaphore);
            string stall = VulkanAcquire.ActiveDescription(VulkanAcquireMode.Stall);

            Assert.NotEqual(semaphore, stall);
            Assert.Contains("BLOCKS THE CPU", stall, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// THE ONE COMBINATION THAT CANNOT WORK SAYS SO. The stall mode presents with no wait semaphore, which is
        /// what a validation layer rejects, so a run with both on reports that on every present and buries
        /// whatever else the layer found. It is a documented limitation of the A/B switch rather than a defect.
        /// </summary>
        [Fact]
        public void TheValidationConflictNamesBothVariables()
        {
            string warning = VulkanAcquire.ValidationConflictWarning(VulkanValidationMode.Strict);

            Assert.Contains(VulkanAcquire.EnvVarName, warning, System.StringComparison.Ordinal);
            Assert.Contains(VulkanValidation.EnvVarName, warning, System.StringComparison.Ordinal);
        }
    }
}
