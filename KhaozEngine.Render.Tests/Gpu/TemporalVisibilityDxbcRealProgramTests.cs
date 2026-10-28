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
    /// The hosted Windows check on FXC's real disassembly of every visibility program. The opt-in dump fact
    /// <see cref="D3D11FxcValidationTests.DumpVisibilityDisassembly"/> printed all fourteen listings in hosted run
    /// 39636752557, job 119349307880, on windows-2025-vs2026 20261018.275.1, with the compiler banner
    /// <c>Microsoft (R) HLSL Shader Compiler 10.1</c>. Task 7h Z round 2 recaptured the six full shipped programs with
    /// DontFlatten selection control. FXC keeps all twelve scalar barriers inside the non-bypass branch.
    /// Both decision helpers and all seven D prime listings stayed
    /// byte-identical to 7g.5. Every shipped listing has twelve gathers
    /// and every listing has zero
    /// precise markings. D's two in-loop heads are reassociated in both programs, and the fast path adds four
    /// reassociated heads and two written ones. D's in-loop forms agree between fused and split entries too
    /// (<see cref="TemporalVisibilityDxbcBarrierTests"/>). The fixtures hold the printed text, carriage returns
    /// removed, beside the SHA-256 of the GLSL it was compiled from. The unchanged check compares D's in-loop chains
    /// exactly and checks all six fast-path chains. Mutated copies must still fail each rule on its own
    /// (<see cref="TemporalVisibilityDxbcRuleMutationTests"/>). Device-free on every leg.
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
        /// fixture folder holds both sides plus the additional shipped release variants, each with its source hash.</summary>
        [Fact]
        public void TheFixturesCoverEveryProgramTheHostedFactChecks()
        {
            string[] programs = ((IEnumerable<object[]>)Programs).Select(row => (string)row[0]).ToArray();
            string[] hosted = [Helper, .. D3D11FxcValidationTests.VisibilityPrograms.Select(p => p.Name)];
            Assert.Equal(hosted, programs);
            string[] expected = programs.SelectMany(p => new[] { "d", "shipped" }.SelectMany(side =>
                new[] { $"{p}.{side}.dxbc.gz", $"{p}.{side}.glsl.sha256" }))
                .Concat(TemporalLockReleaseDxbcPrograms.Additional.SelectMany(p => new[]
                    { $"{p.Name}.shipped.dxbc.gz", $"{p.Name}.shipped.glsl.sha256" }))
                .Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(expected, Directory.GetFiles(Fixtures).Select(f => Path.GetRelativePath(Fixtures, f))
                .Order(StringComparer.Ordinal));
        }

        /// <summary>Task 7h Z recaptured every stale fixture in hosted run 39636752557. This set is empty, so every
        /// source pin must match the current GLSL on both sides.</summary>
        internal static readonly string[] StaleUntil7g3b = [];

        /// <summary>Each fixture is FXC's compile of the GLSL the code builds today: the SHA-256 of that text,
        /// LF line endings, UTF-8, equals the one stored beside the fixture. A program in
        /// <see cref="StaleUntil7g3b"/> passes only while both source pins differ.</summary>
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
            if (StaleUntil7g3b.Contains(program))
            {
                foreach (string line in stale) output.WriteLine("expected stale until Task 7g.3b recaptures it: " + line);
                Assert.True(stale.Count == 2, $"{program} is listed in {nameof(StaleUntil7g3b)}, so both its fixtures "
                    + $"must be stale until Task 7g.3b recaptures them, and {2 - stale.Count} of them match the current "
                    + "GLSL. Remove a recaptured program from the list.");
                return;
            }
            Assert.True(stale.Count == 0, string.Join("\n", stale) + "\nRecapture: push the branch, dispatch "
                + "cross-platform-gpu.yml on it with legs=direct3d11-native and dumpDxbc=true, then run "
                + "scripts/recapture-taa-dxbc.py <run-id> --write once the run completes.");
        }

        [Theory]
        [MemberData(nameof(Programs))]
        public void TheRealProgramPassesTheCheck(string program)
        {
            List<string> failures = TemporalVisibilityDxbcComparison.Compare(program, Load(program, d: true),
                Load(program, d: false), output.WriteLine);
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        /// <summary>What FXC made of each program: D's in-loop head forms in D and shipped, the fast path's
        /// straight-line forms, declared temps, precise markings and unread non-chain positions.</summary>
        [Theory]
        [InlineData("DecisionHelper", "RR", "RR", "RRRRWW", 12, 18, false, 0)]
        [InlineData("TemporalAccumulateFrag", "RR", "RR", "RRRRWW", 29, 37, false, 10)]
        [InlineData("TemporalAccumulateHalfFrag", "RR", "RR", "RRRRWW", 32, 38, false, 10)]
        [InlineData("TemporalAccumulateAtDisplaySizeFrag", "RR", "RR", "RRRRWW", 26, 36, false, 10)]
        [InlineData("TemporalAccumulateAtDisplaySizeHalfFrag", "RR", "RR", "RRRRWW", 29, 37, false, 10)]
        [InlineData("TemporalResolveFrag", "RR", "RR", "RRRRWW", 41, 46, false, 10)]
        [InlineData("TemporalResolveHalfFrag", "RR", "RR", "RRRRWW", 42, 48, false, 10)]
        public void EachProgramCompilesItsChainsInTheseForms(string program, string dLoop, string loop, string straight,
            int dTemps, int temps, bool precise, int unreadCount)
        {
            string d = Load(program, d: true), shipped = Load(program, d: false);
            List<TemporalVisibilityDxbc.Chain> dChains = TemporalVisibilityDxbc.Found(d), chains = TemporalVisibilityDxbc.Found(shipped);
            Assert.Equal(dLoop, Forms(dChains.Where(c => c.LoopDepth > 0)));
            Assert.DoesNotContain(dChains, c => c.LoopDepth == 0);
            Assert.Equal(loop, Forms(chains.Where(c => c.LoopDepth > 0)));
            Assert.Equal(straight, Forms(chains.Where(c => c.LoopDepth == 0)));
            Assert.Equal((dTemps, temps), (TemporalVisibilityDxbc.Declarations(d).Temps, TemporalVisibilityDxbc.Declarations(shipped).Temps));
            // Every current chain ends in a mad. The reader also retains the historical precise mul/add form.
            string end = precise ? " > add [precise] l(-0.500000)" : "l(-0.500000)";
            Assert.All(dChains.Concat(chains), c => Assert.EndsWith(end, c.Signature, StringComparison.Ordinal));
            Assert.All(dChains.Concat(chains), c => Assert.Equal(precise, c.Signature.StartsWith("itof [precise]", StringComparison.Ordinal)));
            // The -0.5 positions left unread have no itof behind them. In each full program six are a sample position,
            // uv times a constant-buffer size less 0.5. Four are bicubic weight mads, two by a register and two by a literal.
            foreach (string text in new[] { d, shipped })
            {
                List<string> unread = TemporalVisibilityDxbc.Unread(text);
                Assert.Equal(unreadCount, unread.Count);
                Assert.All(unread, u => Assert.DoesNotContain("itof", u, StringComparison.Ordinal));
                Assert.Equal(unreadCount == 0 ? 0 : 6, unread.Count(u => Regex.IsMatch(u, @"^component [xyzw]: mad[^,]*, r\d+\.[xyzw]+, cb\d+\[")));
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
        [InlineData("TemporalAccumulateFrag", 2)]
        [InlineData("TemporalResolveHalfFrag", 2)]
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
