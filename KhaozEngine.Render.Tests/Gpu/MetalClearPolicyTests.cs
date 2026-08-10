using System;
using System.Collections.Generic;
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
    /// NOTHING HERE MUTATES THE PROCESS ENVIRONMENT, so no row races the memoized reading a real device takes.
    /// Every parse row drives an explicit value, and the one row that touches the memo READS it and compares the
    /// two entry points against each other rather than against a value it planted.
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

        /// <summary>
        /// THE TYPO REACHES A LOG LINE, which is the half a parse alone cannot promise. The first implementation
        /// named the unrecognized value to a caller that discarded it, so the whole protection was unreachable
        /// code and a mistyped run was indistinguishable from a default one.
        /// </summary>
        [Theory]
        [InlineData("attachment1")]
        [InlineData("'attachment0'")]
        public void AnUnrecognizedValueIsWARNED(string value)
        {
            List<string> warnings = new();

            MetalClearPolicy.Report(value, warnings.Add);

            string warning = Assert.Single(warnings);
            Assert.Equal(MetalClearPolicy.UnrecognizedDescription(value), warning);
            Assert.Contains(value, warning, StringComparison.Ordinal);
        }

        /// <summary>And a value that parsed says NOTHING, including the default, because a line on every session
        /// is a line nobody reads and this one has to be noticed.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("attachment0")]
        [InlineData("per-attachment")]
        public void ARecognizedValueIsSilent(string? value)
        {
            List<string> warnings = new();

            MetalClearPolicy.Report(value, warnings.Add);

            Assert.Empty(warnings);
        }

        /// <summary>
        /// AND THE PRODUCTION ENTRY POINT IS THE SAME DECISION over the memoized reading, which is what pins the
        /// wiring rather than the parse. <c>MetalGpuDevice</c> calls this overload at creation, and it cannot be
        /// driven against a mutated environment (the memo is per process and every list in the collection shares
        /// it), so what a device-free row can assert is that it agrees with the pure one for whatever this
        /// process's own value is. A refactor that dropped the name on the memoized path would fail here.
        /// </summary>
        [Fact]
        public void TheMemoizedEntryPointReportsWhatThePureOneDoes()
        {
            List<string> memoized = new();
            List<string> pure = new();

            MetalClearPolicy.Report(memoized.Add);
            MetalClearPolicy.Report(Environment.GetEnvironmentVariable(MetalClearPolicy.EnvVarName), pure.Add);

            Assert.Equal(pure, memoized);
        }
    }
}
