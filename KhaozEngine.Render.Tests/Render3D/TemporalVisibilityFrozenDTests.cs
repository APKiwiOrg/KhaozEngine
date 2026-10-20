using System;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The frozen D oracle cannot drift. Its three texts hash to the pins taken from production at 583a3ae7b, the
    /// production text still is D at this commit, and the D rewrite turns every program that runs the visibility
    /// release into D while leaving the others alone. Device-free.
    /// </summary>
    public sealed class TemporalVisibilityFrozenDTests
    {
        // Every program the renderer compiles that carries the visibility release: the fused resolve and the split's
        // second pass, at both precisions, upscaling and at the display's own size.
        static readonly string[] ReleasePrograms =
        [
            ShaderSources.TemporalResolveFrag, ShaderSources.TemporalResolveHalfFrag,
            ShaderSources.TemporalAccumulateFrag, ShaderSources.TemporalAccumulateHalfFrag,
            ShaderSources.TemporalAccumulateAtDisplaySizeFrag, ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
        ];

        /// <summary>Permanent. The fixture is the text D shipped, whatever production becomes.</summary>
        [Fact]
        public void FrozenDTextHashesToTheShippedDPin()
        {
            Assert.Equal(TemporalVisibilityFrozenD.VisibilitySha256,
                TemporalVisibilityFrozenD.Sha256(TemporalVisibilityFrozenD.VisibilityGlsl));
            Assert.Equal(TemporalVisibilityFrozenD.CallSiteSha256,
                TemporalVisibilityFrozenD.Sha256(TemporalVisibilityFrozenD.CallSiteGlsl));
            Assert.Equal(TemporalVisibilityFrozenD.HoldReleaseSha256,
                TemporalVisibilityFrozenD.Sha256(TemporalVisibilityFrozenD.HoldReleaseGlsl));
        }

        /// <summary>True until the cost cut changes production (plan Task 7f.2), which deletes this fact in the same
        /// commit. The pin above stays.</summary>
        [Fact]
        public void ProductionStillShipsFrozenD()
        {
            Assert.Equal(TemporalVisibilityFrozenD.VisibilityGlsl, ShaderSources.TemporalVisibilityGlsl);
            Assert.Equal(1, Count(ShaderSources.TemporalAccumulateGlsl, TemporalVisibilityFrozenD.CallSiteGlsl));
            Assert.Equal(1, Count(ShaderSources.TemporalAccumulateGlsl, TemporalVisibilityFrozenD.HoldReleaseGlsl));
            foreach (string program in ReleasePrograms)
                Assert.Equal(program, TemporalVisibilityFrozenD.ToD(program));
        }

        [Fact]
        public void TheDRewriteCarriesTheFrozenTextIntoEveryReleaseProgram()
        {
            foreach (string program in ReleasePrograms)
            {
                string d = TemporalVisibilityFrozenD.ToD(program);
                Assert.Equal(1, Count(d, TemporalVisibilityFrozenD.VisibilityGlsl));
                Assert.Equal(1, Count(d, TemporalVisibilityFrozenD.CallSiteGlsl));
                Assert.Equal(1, Count(d, TemporalVisibilityFrozenD.HoldReleaseGlsl));
            }
            Assert.Equal(ShaderSources.TemporalPrepareFrag, TemporalVisibilityFrozenD.ToD(ShaderSources.TemporalPrepareFrag));
            Assert.Equal(ShaderSources.TemporalDepthStoreFrag,
                TemporalVisibilityFrozenD.ToD(ShaderSources.TemporalDepthStoreFrag));
        }

        [Fact]
        public void TheDRewriteRefusesAProgramWithALostAnchor()
        {
            string lostHold = ShaderSources.TemporalAccumulateFrag.Replace(TemporalVisibilityFrozenD.ProductionHoldRelease,
                "    if (released) hold = 0.0;", StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(lostHold));
            string lostHelper = ShaderSources.TemporalAccumulateFrag.Replace(ShaderSources.TemporalVisibilityGlsl, "\n",
                StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(lostHelper));
        }

        [Fact]
        public void TheMutantRewriteChangesOnlyTheCallSite()
        {
            string mutant = TemporalVisibilityFrozenD.ToMutantD(ShaderSources.TemporalAccumulateFrag,
                callSite => callSite.Replace("> VisibilityReleaseThreshold", "> 2.0", StringComparison.Ordinal));
            Assert.NotEqual(TemporalVisibilityFrozenD.ToD(ShaderSources.TemporalAccumulateFrag), mutant);
            Assert.Contains("abs(signedVisibilityChange) > 2.0", mutant, StringComparison.Ordinal);
            Assert.Equal(1, Count(mutant, TemporalVisibilityFrozenD.VisibilityGlsl));
        }

        [Fact]
        public void TheRenamedCopyChangesOnlyTheFunctionNames()
        {
            string renamed = TemporalVisibilityFrozenD.Renamed("frozenD");
            Assert.DoesNotContain("temporalVisibility", renamed, StringComparison.Ordinal);
            Assert.Contains("bool frozenDVisibilityChange(", renamed, StringComparison.Ordinal);
            Assert.Equal(TemporalVisibilityFrozenD.VisibilityGlsl, renamed
                .Replace("frozenDVisibilityChange", "temporalVisibilityChange", StringComparison.Ordinal)
                .Replace("frozenDVisibilityFiniteDepth", "temporalVisibilityFiniteDepth", StringComparison.Ordinal));
        }

        static int Count(string text, string part)
        {
            int n = 0;
            for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
                 at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
                n++;
            return n;
        }
    }
}
