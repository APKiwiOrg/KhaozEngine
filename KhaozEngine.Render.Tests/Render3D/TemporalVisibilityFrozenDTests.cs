using System;
using System.Linq;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The frozen D oracle cannot drift. Its texts hash to the pins taken from production at 583a3ae7b, and the D
    /// rewrite turns every program the resolve renderer and its split compile into the program D shipped, the first
    /// pass and the depth store included. Device-free.
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

        /// <summary>Permanent. The first pass, surface and fused entry pieces are the text D shipped.</summary>
        [Fact]
        public void FrozenDMaskPiecesHashToTheShippedDPins()
        {
            Assert.Equal(7, TemporalVisibilityFrozenDMask.All.Length);
            foreach (TemporalVisibilityFrozenDMask.Hunk hunk in TemporalVisibilityFrozenDMask.All)
            {
                Assert.True(hunk.Sha256 == TemporalVisibilityFrozenD.Sha256(hunk.D), $"{hunk.Name} is not D's text");
                Assert.StartsWith(hunk.FirstLine, hunk.D, StringComparison.Ordinal);
                Assert.EndsWith(hunk.LastLine, hunk.D, StringComparison.Ordinal);
            }
        }

        /// <summary>Every program the D rig compiles is the program D shipped at 583a3ae7b, byte for byte. The split's
        /// first pass is the one the current mask moved into, and it is D's again.</summary>
        [Theory]
        [MemberData(nameof(RigPrograms))]
        public void TheDRewriteIsTheProgramDShipped(string program, string sha256)
            => Assert.Equal(sha256, TemporalVisibilityFrozenD.Sha256(TemporalVisibilityFrozenD.ToD(Program(program))));

        [Fact]
        public void TheDPrepareProgramIsTheOneDShipped()
        {
            string d = TemporalVisibilityFrozenD.ToD(ShaderSources.TemporalPrepareFrag);
            Assert.Equal(TemporalVisibilityFrozenDMask.ProgramSha256.Single(p => p.Program
                == nameof(ShaderSources.TemporalPrepareFrag)).Sha256, TemporalVisibilityFrozenD.Sha256(d));
            Assert.DoesNotContain("temporalVisibilityCurrent", d, StringComparison.Ordinal);
            Assert.Contains(TemporalVisibilityFrozenDMask.PassOne.D, d, StringComparison.Ordinal);
            Assert.Equal(ShaderSources.TemporalPrepareFrag, d);
        }

        public static TheoryData<string, string> RigPrograms()
        {
            var data = new TheoryData<string, string>();
            foreach ((string program, string sha256) in TemporalVisibilityFrozenDMask.ProgramSha256) data.Add(program, sha256);
            return data;
        }

        static string Program(string name) => name switch
        {
            nameof(ShaderSources.TemporalPrepareFrag) => ShaderSources.TemporalPrepareFrag,
            nameof(ShaderSources.TemporalResolveFrag) => ShaderSources.TemporalResolveFrag,
            nameof(ShaderSources.TemporalResolveHalfFrag) => ShaderSources.TemporalResolveHalfFrag,
            nameof(ShaderSources.TemporalAccumulateFrag) => ShaderSources.TemporalAccumulateFrag,
            nameof(ShaderSources.TemporalAccumulateHalfFrag) => ShaderSources.TemporalAccumulateHalfFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag) => ShaderSources.TemporalAccumulateAtDisplaySizeFrag,
            nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag)
                => ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag,
            nameof(ShaderSources.TemporalDepthStoreFrag) => ShaderSources.TemporalDepthStoreFrag,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a program the rig compiles"),
        };

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
            string lostSurface = ShaderSources.TemporalAccumulateFrag.Replace(TemporalVisibilityFrozenDMask.Stored.FirstLine,
                "    s.edgeMotion = edgeMotion + 0.0;", StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => TemporalVisibilityFrozenD.ToD(lostSurface));

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
