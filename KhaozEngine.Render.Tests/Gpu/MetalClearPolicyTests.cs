using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// M-A2's KILL SWITCH, PARSED. Row 12 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, section 7.2.
    /// <para>
    /// THE KNOB HAS A DEADLINE (gate 1), so the value of pinning it is not that it will live long. It is that
    /// gate 1's A/B is the ONLY measurement of the one deliberate rendering change this phase spends on the
    /// reference golden family, and a typo that silently selected the fix while the tester believed they had
    /// selected the incumbent would make that measurement report the same position twice.
    /// </para>
    /// <para>
    /// EVERY ROW HERE IS PURE, so none of them touches the process environment and none of them races the
    /// memoized reading a real device takes.
    /// </para>
    /// </summary>
    public sealed class MetalClearPolicyTests
    {
        /// <summary>Unset is the FIX, which is the position this backend ships.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingSetMeansThePerAttachmentClear(string? value)
        {
            Assert.Equal(MetalClearMode.PerAttachment, MetalClearPolicy.Parse(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>The documented spelling and the two the reader will try, all selecting the incumbent's
        /// collapse. Case and surrounding space do not matter, because a shell export routinely carries
        /// both.</summary>
        [Theory]
        [InlineData("attachment0")]
        [InlineData("ATTACHMENT0")]
        [InlineData("  attachment0  ")]
        [InlineData("attachment-0")]
        [InlineData("incumbent")]
        public void TheIncumbentPositionIsSelectable(string value)
        {
            Assert.Equal(MetalClearMode.Attachment0, MetalClearPolicy.Parse(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>The fix can be asked for explicitly too, so a CI job can pin the position it means rather
        /// than relying on the variable being absent.</summary>
        [Theory]
        [InlineData("perattachment")]
        [InlineData("per-attachment")]
        [InlineData("default")]
        public void ThePerAttachmentPositionIsSelectableByName(string value)
        {
            Assert.Equal(MetalClearMode.PerAttachment, MetalClearPolicy.Parse(value, out string? unrecognized));
            Assert.Null(unrecognized);
        }

        /// <summary>
        /// A TYPO IS THE DEFAULT PLUS A NAME. It is reported verbatim, quotes and stray spaces included, because
        /// the value that did not parse is the only thing that tells the reader what they actually typed.
        /// </summary>
        [Theory]
        [InlineData("attachment1")]
        [InlineData("0")]
        [InlineData("'attachment0'")]
        [InlineData("yes")]
        public void AnUnrecognizedValueFallsBackAndIsReportedVerbatim(string value)
        {
            Assert.Equal(MetalClearMode.PerAttachment, MetalClearPolicy.Parse(value, out string? unrecognized));
            Assert.Equal(value, unrecognized);

            string warning = MetalClearPolicy.UnrecognizedDescription(value);
            Assert.Contains(value, warning, StringComparison.Ordinal);
            Assert.Contains(MetalClearPolicy.Attachment0Value, warning, StringComparison.Ordinal);
        }

        /// <summary>
        /// THE SUBSTITUTION ITSELF, WHICH IS THE ENTIRE OF THE SWITCH. Under the fix a clear lands where the
        /// caller asked. Under the incumbent it lands on slot 0 whatever was asked, which is what makes a
        /// framebuffer with three colour targets clear only its first.
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

        /// <summary>The variable's name is what the design, the warning and any CI job all have to agree on, so
        /// it is pinned rather than left to three copies.</summary>
        [Fact]
        public void TheKnobIsNamedWhatTheDesignSaysItIs()
        {
            Assert.Equal("KE_METAL_CLEAR", MetalClearPolicy.EnvVarName);
            Assert.Equal("attachment0", MetalClearPolicy.Attachment0Value);
        }
    }
}
