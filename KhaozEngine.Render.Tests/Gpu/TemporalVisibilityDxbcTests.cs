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

        [Fact]
        public void DeclarationsReadTheTempsAndEveryIndexableTemp()
        {
            (int temps, string[] indexable) = TemporalVisibilityDxbc.Declarations(DChain + "dcl_indexableTemp x0[9], 4\n");
            Assert.Equal(12, temps);
            Assert.Equal(["dcl_indexableTemp x0[9], 4"], indexable);
        }
    }
}
