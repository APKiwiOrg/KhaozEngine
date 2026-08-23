using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// M-A2's SUBSTITUTION, DEVICE-FREE. Row 12 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, section 7.2.
    /// <para>
    /// THE KNOB IS GONE AND THE DECISION IS NOT. <c>KE_METAL_CLEAR</c> was retired at rollout gate 1 with the
    /// parse, the once-per-process memo and the typo WARN, so every row about the environment went with it. What
    /// is left is the one expression the whole decision reduces to, kept as its own row because the two
    /// positions it selects between are what row 12's readback test compares on hardware.
    /// </para>
    /// </summary>
    public sealed class MetalClearPolicyTests
    {
        /// <summary>
        /// Under the shipped position a clear lands where the caller asked. Under the incumbent's it landed on
        /// slot 0 whatever was asked, which is what made a framebuffer with three colour targets clear only
        /// its first.
        /// </summary>
        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(2u)]
        public void TheTargetIndexIsTheCallersUnlessTheIncumbentPositionIsSelected(uint requested)
        {
            Assert.Equal(requested, MetalClearPolicy.TargetIndex(MetalClearMode.PerAttachment, requested));
            Assert.Equal(0u, MetalClearPolicy.TargetIndex(MetalClearMode.Attachment0, requested));
        }
    }
}
