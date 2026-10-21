using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The hosted Windows check, run on FXC's real disassembly of every visibility program (review 7f r3, I-1 and m-1). The
    /// dump test of diagnostic commits a00f69fd5 and a65622ae4 printed each program as shipped and as rewritten to D, at
    /// the shipped flags. Hosted run 39374926165 printed the four accumulate programs and the two fused resolve programs,
    /// and run 39376184230 printed those twelve listings again, identical, and the decision helper's two, on runner
    /// image windows-2025-vs2026 20261007.264.1. The pass-one mask was withdrawn and then restored. After the
    /// restoration the opt-in fact <see cref="D3D11FxcValidationTests.DumpVisibilityDisassembly"/> printed all fourteen
    /// listings again in hosted run 39526640607, job 118982787004, on the same runner image, byte for byte the
    /// fixtures from before the withdrawal, so the eight the withdrawal had replaced are those again. Every run names the
    /// compiler only by its banner, <c>Microsoft (R) HLSL Shader Compiler 10.1</c>, and no d3dcompiler build number. The
    /// fixtures hold that text, carriage returns removed, and beside each the SHA-256 of the GLSL it was compiled from
    /// (<see cref="EachFixtureWasCompiledFromTheCurrentGlsl"/>). The r3 reader found no chain in the six full programs,
    /// because FXC marks every instruction there precise and so ends each chain <c>mul S</c> then <c>add -0.5</c> rather
    /// than <c>mad S -0.5</c>. Here every program reads as the check requires: D's in-loop chains equal in both programs,
    /// and six straight-line chains for the fast path. Mutated copies of the real text, one per rule, must fail it on that
    /// rule alone (<see cref="TemporalVisibilityDxbcRuleMutationTests"/>). Device-free on every leg.
    /// </summary>
    public sealed class TemporalVisibilityDxbcRealProgramTests(ITestOutputHelper output)
    {
        public static TheoryData<string> Programs => new()
        {
            "DecisionHelper",
            "TemporalAccumulateFrag",
            "TemporalAccumulateHalfFrag",
            "TemporalAccumulateAtDisplaySizeFrag",
            "TemporalAccumulateAtDisplaySizeHalfFrag",
            "TemporalResolveFrag",
            "TemporalResolveHalfFrag",
        };

        const string Helper = "DecisionHelper";

        static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "taa-visibility-dxbc");

        internal static string Load(string program, bool d)
        {
            string path = Path.Combine(Fixtures, $"{program}.{(d ? "d" : "shipped")}.dxbc.gz");
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
        }

        /// <summary>The GLSL the dump compiled for a fixture: the decision helper and D's helper as the hosted fact
        /// builds them, or a visibility program and <see cref="TemporalVisibilityFrozenD.ToD"/> of it.</summary>
        internal static string Glsl(string program, bool d)
        {
            if (program == Helper)
                return d ? D3D11FxcValidationTests.DHelperProgram : TemporalVisibilityCompiledFormTests.DecisionHelperProgram;
            string source = D3D11FxcValidationTests.VisibilityPrograms.Single(p => p.Name == program).Source;
            return d ? TemporalVisibilityFrozenD.ToD(source) : source;
        }

        /// <summary>The programs here are the decision helper and the hosted fact's programs, in its order, and the
        /// fixture folder holds a listing and a source hash for each side of each, nothing more.</summary>
        [Fact]
        public void TheFixturesCoverEveryProgramTheHostedFactChecks()
        {
            string[] programs = ((IEnumerable<object[]>)Programs).Select(row => (string)row[0]).ToArray();
            string[] hosted = [Helper, .. D3D11FxcValidationTests.VisibilityPrograms.Select(p => p.Name)];
            Assert.Equal(hosted, programs);
            string[] expected = programs.SelectMany(p => new[] { "d", "shipped" }.SelectMany(side =>
                new[] { $"{p}.{side}.dxbc.gz", $"{p}.{side}.glsl.sha256" })).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, Directory.GetFiles(Fixtures).Select(f => Path.GetRelativePath(Fixtures, f))
                .Order(StringComparer.Ordinal));
        }

        /// <summary>Each fixture is FXC's compile of the GLSL the code builds today (review 7f r4, M-1): the SHA-256 of
        /// that text, LF line endings, UTF-8, equals the one stored beside the fixture, the hash of the text at
        /// 863bade06, which the restored mask builds again.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void EachFixtureWasCompiledFromTheCurrentGlsl(string program)
        {
            var stale = new List<string>();
            foreach (bool d in new[] { true, false })
            {
                string fixture = $"{program}.{(d ? "d" : "shipped")}";
                string stored = File.ReadAllText(Path.Combine(Fixtures, fixture + ".glsl.sha256")).Trim();
                string current = TemporalVisibilityFrozenD.Sha256(Glsl(program, d));
                if (stored != current)
                    stale.Add($"{fixture}.dxbc.gz was dumped from GLSL hashing {stored}, and the code now builds GLSL "
                        + $"hashing {current}.");
            }
            Assert.True(stale.Count == 0, string.Join("\n", stale) + "\nRecapture: on a diagnostic branch, set "
                + VisibilityDxbcDumpFactAttribute.EnvironmentVariable + "=1 on the Windows FXC step of cross-platform-gpu.yml "
                + "so D3D11FxcValidationTests.DumpVisibilityDisassembly runs, dispatch it there with "
                + "legs=direct3d11-native, take each stale fixture's DXBC|name|D or DXBC|name|S lines from the log, "
                + "carriage returns removed, gzip them over the fixture and store the new hash beside it.");
        }

        [Theory]
        [MemberData(nameof(Programs))]
        public void TheRealProgramPassesTheCheck(string program)
        {
            List<string> failures = TemporalVisibilityDxbcComparison.Compare(program, Load(program, d: true),
                Load(program, d: false), output.WriteLine);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        /// <summary>What FXC made of each program: the head forms of D's in-loop chains in D's program and in the
        /// shipped one, the fast path's straight-line forms, and the temps each declares. In the full programs every
        /// chain is precise and written. The decision helper, compiled without a precise output, reassociates D's chains
        /// in both programs and four of the fast path's six.</summary>
        [Theory]
        [InlineData("DecisionHelper", "RR", "RR", "RRRRWW", 12, 28, false)]
        [InlineData("TemporalAccumulateFrag", "WW", "WW", "WWWWWW", 30, 44, true)]
        [InlineData("TemporalAccumulateHalfFrag", "WW", "WW", "WWWWWW", 31, 45, true)]
        [InlineData("TemporalAccumulateAtDisplaySizeFrag", "WW", "WW", "WWWWWW", 27, 42, true)]
        [InlineData("TemporalAccumulateAtDisplaySizeHalfFrag", "WW", "WW", "WWWWWW", 29, 43, true)]
        [InlineData("TemporalResolveFrag", "WW", "WW", "WWWWWW", 41, 47, true)]
        [InlineData("TemporalResolveHalfFrag", "WW", "WW", "WWWWWW", 43, 49, true)]
        public void EachProgramCompilesItsChainsInTheseForms(string program, string dLoop, string loop, string straight,
            int dTemps, int temps, bool precise)
        {
            string d = Load(program, d: true), shipped = Load(program, d: false);
            List<TemporalVisibilityDxbc.Chain> dChains = TemporalVisibilityDxbc.Found(d), chains = TemporalVisibilityDxbc.Found(shipped);
            Assert.Equal(dLoop, Forms(dChains.Where(c => c.LoopDepth > 0)));
            Assert.DoesNotContain(dChains, c => c.LoopDepth == 0);
            Assert.Equal(loop, Forms(chains.Where(c => c.LoopDepth > 0)));
            Assert.Equal(straight, Forms(chains.Where(c => c.LoopDepth == 0)));
            Assert.Equal((dTemps, temps), (TemporalVisibilityDxbc.Declarations(d).Temps, TemporalVisibilityDxbc.Declarations(shipped).Temps));
            // A precise chain ends in a mul and an add of -0.5, which the r3 reader did not read. The helper's end in a mad.
            string end = precise ? " > add [precise] l(-0.500000)" : "l(-0.500000)";
            Assert.All(dChains.Concat(chains), c => Assert.EndsWith(end, c.Signature, StringComparison.Ordinal));
            Assert.All(dChains.Concat(chains), c => Assert.Equal(precise, c.Signature.StartsWith("itof [precise]", StringComparison.Ordinal)));
            // The -0.5 positions left unread have no itof behind them. In each full program six are a sample position,
            // uv times a constant-buffer size less 0.5, and two are a bicubic weight's mad by a register.
            foreach (string text in new[] { d, shipped })
            {
                List<string> unread = TemporalVisibilityDxbc.Unread(text);
                Assert.Equal(precise ? 8 : 0, unread.Count);
                Assert.All(unread, u => Assert.DoesNotContain("itof", u, StringComparison.Ordinal));
                Assert.Equal(precise ? 6 : 0, unread.Count(u => Regex.IsMatch(u, @"^component [xyzw]: mad[^,]*, r\d+\.[xyzw]+, cb\d+\[")));
            }

            static string Forms(IEnumerable<TemporalVisibilityDxbc.Chain> list) => string.Concat(list
                .Select(c => TemporalVisibilityDxbc.Form(c.Signature) switch
                {
                    TemporalVisibilityDxbc.HeadForm.Written => "W",
                    TemporalVisibilityDxbc.HeadForm.Reassociated => "R",
                    _ => "O",
                }).Order());
        }

        /// <summary>The check has teeth on real text. D's in-loop head swapped to the other order in the shipped
        /// program, the case review 7f r3 named, fails as an in-loop difference and nothing else, even though both
        /// orders are analysed ones. A fast path head given a shared <c>0.5 - J</c>, a third association, fails as
        /// neither order and nothing else, and turns exactly the chains through its half into that form, each with its
        /// own remainder (review 7f r4, N-1).</summary>
        [Theory]
        [InlineData("DecisionHelper", 2)]
        [InlineData("TemporalAccumulateFrag", 4)]
        [InlineData("TemporalResolveHalfFrag", 4)]
        public void MutatedRealTextFailsTheCheck(string program, int shared)
        {
            string d = Load(program, d: true), shipped = Load(program, d: false);
            List<string> swapped = TemporalVisibilityDxbcComparison.Compare(program, d,
                TemporalVisibilityDxbcMutations.SwapInLoopHead(shipped));
            Assert.Contains($"{program}: the shipped program's in-loop chains differ from D's", swapped);
            Assert.All(swapped, f => Assert.True(f.StartsWith($"{program}: the shipped program's in-loop chains differ from D's",
                StringComparison.Ordinal) || f.StartsWith($"{program}:   ", StringComparison.Ordinal), f));

            string mutated = TemporalVisibilityDxbcMutations.ShareStraightLineHead(shipped);
            List<string> failures = TemporalVisibilityDxbcComparison.Compare(program, d, mutated);
            Assert.NotEmpty(failures);
            Assert.All(failures, f => Assert.StartsWith(
                $"{program}: the shipped program has a position chain whose head is neither analysed order: ", f, StringComparison.Ordinal));
            List<string> before = TemporalVisibilityDxbcMutations.Side(shipped, inLoop: false);
            List<string> after = TemporalVisibilityDxbcMutations.Side(mutated, inLoop: false);
            Assert.Equal(TemporalVisibilityDxbcMutations.Side(shipped, inLoop: true), TemporalVisibilityDxbcMutations.Side(mutated, inLoop: true));
            Assert.Equal(before.Count, after.Count);
            List<string> other = after.Where(c => TemporalVisibilityDxbc.Form(c) == TemporalVisibilityDxbc.HeadForm.Other).ToList();
            List<string> changed = TemporalVisibilityDxbcComparison.Without(before, after);
            Assert.Equal(shared, other.Count);
            Assert.Equal(shared, changed.Count);
            Assert.All(changed, c => Assert.Equal(TemporalVisibilityDxbc.HeadForm.Written, TemporalVisibilityDxbc.Form(c)));
            Assert.Equal(changed.Select(Shared).Order(StringComparer.Ordinal), other.Order(StringComparer.Ordinal));

            // A written chain as the shared head reads it: the itof, then one add of a register holding 0.5 - J.
            static string Shared(string chain)
            {
                string[] steps = chain.Split(" > ");
                string add = steps[1][..steps[1].IndexOf(" +l(", StringComparison.Ordinal)];
                return $"{steps[0]} > {add} +r(add {TemporalVisibilityDxbc.Jitter(chain)} +l(0.500000)) > "
                    + TemporalVisibilityDxbc.Remainder(chain);
            }
        }
    }
}
