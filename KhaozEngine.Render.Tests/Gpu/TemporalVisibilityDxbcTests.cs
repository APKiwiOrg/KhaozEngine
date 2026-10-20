using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The DXBC reader behind the hosted Windows check (<c>D3D11FxcValidationTests</c>, the visibility partial), held to
    /// disassembly written in FXC's form. D's reassociated chain sits in a loop on one register's <c>.xy</c>, a fast
    /// path copy sits on another register's <c>.zw</c>, and both must read as the same signature, while a copy whose
    /// order differs from D's must not, including one that only swaps which register operand it adds where. The three
    /// registers the chain adds are defined first, as FXC defines them: <c>previousUv</c> computed from the input and
    /// the motion, <c>uv</c> moved from the input and <c>Jp / S</c> divided from the constant buffer. One copy pins the
    /// limit the reader states: another <c>add</c> result where D adds <c>previousUv</c> reads as D's chain. Device-free
    /// on every leg, so the Windows-only check never meets a reader that only ever ran there.
    /// </summary>
    public sealed class TemporalVisibilityDxbcTests
    {
        // D's chain as the probe's DXBC showed it, inside its loop, and a fast path copy of it on .zw.
        const string DChain = @"
ps_5_0
dcl_globalFlags refactoringAllowed
dcl_temps 12
div r4.xy, cb0[5].xyxx, cb0[3].xyxx
mov r3.xy, v1.xyxx
ld_indexable(texture2d)(float,float,float,float) r1.xy, r0.xyzw, t1.xyzw
add r2.xy, v1.xyxx, -r1.xyxx
loop
  ld_indexable(texture2d)(float,float,float,float) r8.x, r9.xyzw, t0.xyzw
  itof r10.xy, r9.xyxx
  add r10.xy, r10.xyxx, -cb0[4].xyxx
  add r10.xy, r10.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r10.xy, r10.xyxx, cb0[3].xyxx
  add r10.xy, r2.xyxx, r10.xyxx
  add r10.xy, r10.xyxx, -r3.xyxx
  add r10.xy, r10.xyxx, r4.xyxx
  mad r10.xy, r10.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
  round_ni r11.xy, r10.xyxx
endloop
";

        const string FastCopy = @"
  itof r5.zw, r6.xxxy
  add r5.zw, r5.zzzw, -cb0[4].xxxy
  add r5.zw, r5.zzzw, l(0.000000, 0.000000, 0.500000, 0.500000)
  div r5.zw, r5.zzzw, cb0[3].xxxy
  mov r7.xy, r5.zwzz
  add r7.xy, r7.xyxx, r2.xyxx
  add r7.xy, r7.xyxx, -r3.xyxx
  add r7.xy, r7.xyxx, r4.xyxx
  mad r7.xy, r7.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // The same chain with the jitter subtracted after the half instead of before it.
        const string OtherOrder = @"
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r5.xyxx, r2.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // D's order with Jp / S added where D adds previousUv and previousUv where D adds Jp / S. Without each
        // register's provenance this reads exactly as D's chain does.
        const string SwappedRegisters = @"
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r2.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // D's order with uv added where D adds previousUv and previousUv subtracted where D subtracts uv.
        const string SwappedUv = @"
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r5.xyxx, r3.xyxx
  add r5.xy, r5.xyxx, -r2.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // D's order with another add result, uv plus the motion rather than minus it, where D adds previousUv. The
        // provenance names the opcode alone, so this reads as D's chain: the limit the reader's summary states.
        const string OtherAddResult = @"
  add r12.xy, v1.xyxx, r1.xyxx
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r12.xyxx, r5.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // The loop-invariant part folded once, so a copy adds one register where D adds three.
        const string Hoisted = @"
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r5.xyxx, r12.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        [Fact]
        public void DsChainReadsAsItsReassociatedSignature()
        {
            List<string> chains = TemporalVisibilityDxbc.Chains(DChain);
            Assert.Equal(
            [
                "itof > add -cb0[4].x > add +l(0.500000) > div +cb0[3].x > add +r(add) > add -r(+v1.x) "
                    + "> add +r(div +cb0[5].x +cb0[3].x) > mad +cb0[3].x l(-0.500000)",
                "itof > add -cb0[4].y > add +l(0.500000) > div +cb0[3].y > add +r(add) > add -r(+v1.y) "
                    + "> add +r(div +cb0[5].y +cb0[3].y) > mad +cb0[3].y l(-0.500000)",
            ], chains);
        }

        [Fact]
        public void ACopyOnOtherRegistersAndComponentsReadsTheSame()
        {
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            List<string> both = TemporalVisibilityDxbc.Chains(DChain + FastCopy);
            Assert.Equal(4, both.Count);
            Assert.Equal(d, both.GetRange(0, 2));
            Assert.Equal(d, both.GetRange(2, 2));
        }

        [Theory]
        [InlineData(OtherOrder)]
        [InlineData(Hoisted)]
        [InlineData(SwappedRegisters)]
        [InlineData(SwappedUv)]
        public void ACopyInAnotherOrderReadsDifferently(string copy)
        {
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            List<string> both = TemporalVisibilityDxbc.Chains(DChain + copy);
            Assert.Equal(4, both.Count);
            List<string> copies = both.GetRange(2, 2);
            Assert.DoesNotContain(copies[0], d);
            Assert.DoesNotContain(copies[1], d);
        }

        /// <summary>The swapped copy differs from D in provenance alone: with it stripped, both read the same.</summary>
        [Fact]
        public void TheSwappedCopyIsToldApartByProvenanceAlone()
        {
            static string Shape(string chain) => Regex.Replace(chain, @"([+-])r\([^)]*\)", "$1r");
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            List<string> swapped = TemporalVisibilityDxbc.Chains(DChain + SwappedRegisters).GetRange(2, 2);
            Assert.Equal(d.Select(Shape), swapped.Select(Shape));
            Assert.NotEqual(d, swapped);
        }

        /// <summary>The stated limit: a register another <c>add</c> computed, beside the chain where D's
        /// <c>previousUv</c> is, reads as D's chain, since provenance names the opcode and not its operands.</summary>
        [Fact]
        public void AnotherAddResultWherePreviousUvIsReadsTheSame()
        {
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            List<string> both = TemporalVisibilityDxbc.Chains(DChain + OtherAddResult);
            Assert.Equal(4, both.Count);
            Assert.Equal(d, both.GetRange(2, 2));
        }

        // The written order: the half added first, then the jitter.
        const string WrittenOrder = @"
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r2.xyxx, r5.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // A third association: 0.5 - J computed once and added to every column, which the binade rule is not proved for.
        const string SharedHalfMinusJitter = @"
  add r12.xy, -cb0[4].xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, r12.xyxx
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r2.xyxx, r5.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // The jitter negated into a register first, as FXC's debug flags write it.
        const string MovedJitter = @"
  itof r5.xy, r6.xyxx
  mov r7.xy, -cb0[4].xyxx
  add r5.xy, r5.xyxx, r7.xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  add r5.xy, r2.xyxx, r5.xyxx
  add r5.xy, r5.xyxx, -r3.xyxx
  add r5.xy, r5.xyxx, r4.xyxx
  mad r5.xy, r5.xyxx, cb0[3].xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        // A position by a size held in a register, which the reader does not take for a chain and lists instead.
        const string RegisterScale = @"
  mov r13.xy, cb0[3].xyxx
  itof r5.xy, r6.xyxx
  add r5.xy, r5.xyxx, -cb0[4].xyxx
  add r5.xy, r5.xyxx, l(0.500000, 0.500000, 0.000000, 0.000000)
  div r5.xy, r5.xyxx, cb0[3].xyxx
  mad r5.xy, r5.xyxx, r13.xyxx, l(-0.500000, -0.500000, 0.000000, 0.000000)
";

        /// <summary>Real FXC text: the probe's DXBC at the shipped flags holds D's chain once, on <c>.zw</c>, in the
        /// reassociated order, beside constant-buffer UVs, and the reader misses no -0.5 position.</summary>
        [Fact]
        public void TheRealProbeAtTheShippedFlagsReadsAsReassociated()
        {
            List<string> chains = TemporalVisibilityDxbc.Chains(TemporalVisibilityDxbcSamples.ProbeShipped);
            Assert.Equal(
            [
                "itof > add -cb0[2].x > add +l(0.500000) > div +cb0[1].x > add +cb0[4].z > add -cb0[4].x "
                    + "> add +r(div +cb0[3].x +cb0[1].x) > mad +cb0[1].x l(-0.500000)",
                "itof > add -cb0[2].y > add +l(0.500000) > div +cb0[1].y > add +cb0[4].w > add -cb0[4].y "
                    + "> add +r(div +cb0[3].y +cb0[1].y) > mad +cb0[1].y l(-0.500000)",
            ], chains);
            Assert.All(chains, c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Reassociated, TemporalVisibilityDxbc.Form(c)));
            Assert.Equal(["-cb0[2].x", "-cb0[2].y"], chains.Select(TemporalVisibilityDxbc.Jitter));
            Assert.Empty(TemporalVisibilityDxbc.Unread(TemporalVisibilityDxbcSamples.ProbeShipped));
            Assert.Equal(12, TemporalVisibilityDxbc.Declarations(TemporalVisibilityDxbcSamples.ProbeShipped).Temps);
        }

        /// <summary>Real FXC text: under IEEE strictness the same probe keeps the written order, every instruction
        /// marked precise, and its remainder is the shipped one's with the markings.</summary>
        [Fact]
        public void TheRealProbeUnderStrictnessReadsAsWritten()
        {
            List<string> strict = TemporalVisibilityDxbc.Chains(TemporalVisibilityDxbcSamples.ProbeStrict);
            List<string> shipped = TemporalVisibilityDxbc.Chains(TemporalVisibilityDxbcSamples.ProbeShipped);
            Assert.Equal(2, strict.Count);
            Assert.All(strict, c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Written, TemporalVisibilityDxbc.Form(c)));
            Assert.Equal(shipped.Select(TemporalVisibilityDxbc.Jitter), strict.Select(TemporalVisibilityDxbc.Jitter));
            static string Unmarked(string chain) => Regex.Replace(chain, @" \[precise\([xyzw]*\)\]", "");
            Assert.Equal(shipped.Select(TemporalVisibilityDxbc.Remainder),
                strict.Select(c => Unmarked(TemporalVisibilityDxbc.Remainder(c))));
            Assert.Empty(TemporalVisibilityDxbc.Unread(TemporalVisibilityDxbcSamples.ProbeStrict));
        }

        /// <summary>The two analysed orders, a head that adds the jitter through a register, and a third association
        /// the binade rule does not cover, which still reads as a chain so the check can name it.</summary>
        [Fact]
        public void HeadsSortIntoTheTwoAnalysedOrdersOrNeither()
        {
            static List<string> Copies(string copy) => TemporalVisibilityDxbc.Chains(DChain + copy).GetRange(2, 2);
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            Assert.All(d, c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Reassociated, TemporalVisibilityDxbc.Form(c)));
            Assert.All(Copies(WrittenOrder), c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Written, TemporalVisibilityDxbc.Form(c)));
            Assert.All(Copies(MovedJitter),
                c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Reassociated, TemporalVisibilityDxbc.Form(c)));
            List<string> shared = Copies(SharedHalfMinusJitter);
            Assert.All(shared, c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Other, TemporalVisibilityDxbc.Form(c)));
            Assert.StartsWith("itof > add +r(add -cb0[4].x +l(0.500000)) > div", shared[0], System.StringComparison.Ordinal);
            Assert.Null(TemporalVisibilityDxbc.Jitter(shared[0]));
            // The order of the head leaves the remainder alone, so a written copy keeps D's remainder.
            Assert.Equal(d.Select(TemporalVisibilityDxbc.Remainder), Copies(WrittenOrder).Select(TemporalVisibilityDxbc.Remainder));
            Assert.Equal(d.Select(TemporalVisibilityDxbc.Jitter), Copies(WrittenOrder).Select(TemporalVisibilityDxbc.Jitter));
            Assert.NotEqual(d.Select(TemporalVisibilityDxbc.Remainder), Copies(Hoisted).Select(TemporalVisibilityDxbc.Remainder));
        }

        /// <summary>A -0.5 position the reader cannot read as a chain is listed with the instructions behind it.</summary>
        [Fact]
        public void APositionTheReaderMissesIsListed()
        {
            Assert.Equal(2, TemporalVisibilityDxbc.Chains(DChain + RegisterScale).Count);
            List<string> unread = TemporalVisibilityDxbc.Unread(DChain + RegisterScale);
            Assert.Equal(2, unread.Count);
            Assert.StartsWith("component x: mad r5.xy, r5.xyxx, r13.xyxx", unread[0], System.StringComparison.Ordinal);
            Assert.Contains("< div r5.xy, r5.xyxx, cb0[3].xyxx < add r5.xy", unread[0], System.StringComparison.Ordinal);
            Assert.Contains("< itof r5.xy, r6.xyxx", unread[0], System.StringComparison.Ordinal);
            Assert.Empty(TemporalVisibilityDxbc.Unread(DChain + FastCopy));
        }

        [Fact]
        public void DeclarationsReadTheTempsAndEveryIndexableTemp()
        {
            (int temps, string[] indexable) = TemporalVisibilityDxbc.Declarations(DChain + "dcl_indexableTemp x0[9], 4\n");
            Assert.Equal(12, temps);
            Assert.Equal(["dcl_indexableTemp x0[9], 4"], indexable);
        }
    }
}
