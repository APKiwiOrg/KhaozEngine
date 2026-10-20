using System.Collections.Generic;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The DXBC reader behind the hosted Windows check (<c>D3D11FxcValidationTests</c>, the visibility partial), held to
    /// disassembly written in FXC's form. D's reassociated chain sits in a loop on one register's <c>.xy</c>, a fast
    /// path copy sits on another register's <c>.zw</c>, and both must read as the same signature, while a copy whose
    /// order differs from D's must not. Device-free on every leg, so the Windows-only check never meets a reader that
    /// only ever ran there.
    /// </summary>
    public sealed class TemporalVisibilityDxbcTests
    {
        // D's chain as the probe's DXBC showed it, inside its loop, and a fast path copy of it on .zw.
        const string DChain = @"
ps_5_0
dcl_globalFlags refactoringAllowed
dcl_temps 12
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
                "itof > add -cb0[4].x > add +l(0.500000) > div +cb0[3].x > add +r > add -r > add +r > mad +cb0[3].x l(-0.500000)",
                "itof > add -cb0[4].y > add +l(0.500000) > div +cb0[3].y > add +r > add -r > add +r > mad +cb0[3].y l(-0.500000)",
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
        public void ACopyInAnotherOrderReadsDifferently(string copy)
        {
            List<string> d = TemporalVisibilityDxbc.Chains(DChain);
            List<string> copies = TemporalVisibilityDxbc.Chains(copy);
            Assert.Equal(2, copies.Count);
            Assert.DoesNotContain(copies[0], d);
            Assert.DoesNotContain(copies[1], d);
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
