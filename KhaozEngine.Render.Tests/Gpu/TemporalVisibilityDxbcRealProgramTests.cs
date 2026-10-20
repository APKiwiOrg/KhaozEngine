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
    /// The hosted Windows check, run on FXC's real disassembly of every visibility program (review 7f r3, I-1 and m-1).
    /// Hosted runs 39374926165 and 39376184230 printed the decision helper, the four accumulate programs and the two
    /// fused resolve programs, each as shipped and as rewritten to D, at the shipped flags, and the two runs printed the
    /// same text. The fixtures hold that text, carriage returns removed. The r3 reader found no chain in the six full
    /// programs, because FXC marks every instruction there precise and so ends each chain <c>mul S</c> then
    /// <c>add -0.5</c> rather than <c>mad S -0.5</c>. Here every program reads as the check requires: D's in-loop chains
    /// equal in both programs, and six straight-line chains for the fast path. Mutated copies of the real text, D's
    /// in-loop head swapped to the other order, or a fast path head given a third association, must fail it.
    /// Device-free on every leg.
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

        internal static string Load(string program, bool d)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "taa-visibility-dxbc",
                $"{program}.{(d ? "d" : "shipped")}.dxbc.gz");
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return reader.ReadToEnd();
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
            // Every -0.5 position left unread is the history's own sample position, with no itof behind it.
            foreach (string text in new[] { d, shipped })
            {
                List<string> unread = TemporalVisibilityDxbc.Unread(text);
                Assert.Equal(precise ? 8 : 0, unread.Count);
                Assert.All(unread, u => Assert.DoesNotContain("itof", u, StringComparison.Ordinal));
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
        /// program, the case review 7f r3 named, fails as an in-loop difference even though both orders are analysed
        /// ones. A fast path head given a shared <c>0.5 - J</c>, a third association, fails as neither order.</summary>
        [Theory]
        [InlineData("DecisionHelper")]
        [InlineData("TemporalAccumulateFrag")]
        [InlineData("TemporalResolveHalfFrag")]
        public void MutatedRealTextFailsTheCheck(string program)
        {
            string d = Load(program, d: true), shipped = Load(program, d: false);
            List<string> swapped = TemporalVisibilityDxbcComparison.Compare(program, d, SwapInLoopHead(shipped));
            Assert.Contains($"{program}: the shipped program's in-loop chains differ from D's", swapped);
            Assert.DoesNotContain(swapped, f => f.Contains("straight-line", StringComparison.Ordinal));
            List<string> shared = TemporalVisibilityDxbcComparison.Compare(program, d, ShareStraightLineHead(shipped));
            Assert.Contains(shared, f => f.Contains("head is neither analysed order", StringComparison.Ordinal));
            Assert.DoesNotContain(shared, f => f.Contains("in-loop", StringComparison.Ordinal));
        }

        // Every line of the text, with the loop depth it sits at.
        static (string Line, int Depth)[] Lines(string text)
        {
            int depth = 0;
            return text.Split('\n').Select(raw =>
            {
                string line = raw.Trim();
                if (line == "endloop") depth--;
                (string, int) entry = (raw, depth);
                if (line == "loop") depth++;
                return entry;
            }).ToArray();
        }

        // The in-loop chain's head: the jitter subtract and the half added beside it, in the other order.
        static string SwapInLoopHead(string text)
        {
            (string Line, int Depth)[] lines = Lines(text);
            int jitter = Array.FindIndex(lines, l => l.Depth > 0 && IsJitter(l.Line));
            int half = new[] { jitter - 1, jitter + 1 }.Single(i => lines[i].Line.Contains("0.500000", StringComparison.Ordinal)
                && !lines[i].Line.Contains("-0.500000", StringComparison.Ordinal));
            string[] result = lines.Select(l => l.Line).ToArray();
            (result[jitter], result[half]) = (result[half], result[jitter]);
            return string.Join('\n', result);
        }

        // The first straight-line head that adds the half and then the jitter on a whole register, rewritten to add
        // 0.5 - J computed into a fresh register.
        static string ShareStraightLineHead(string text)
        {
            (string Line, int Depth)[] lines = Lines(text);
            string[] result = lines.Select(l => l.Line).ToArray();
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Depth != 0 || !IsJitter(lines[i].Line)) continue;
                int half = lines[i - 1].Line.Contains("l(0.5", StringComparison.Ordinal) ? i - 1 : i + 1;
                Match add = Regex.Match(lines[half].Line, @"^(\s*add(?: \[precise[^\]]*\])?) (r\d+)(\.[xyzw]+), (r\d+\.[xyzw]+), (l\([^)]*\))$");
                Match sub = Regex.Match(lines[i].Line, @"^\s*add(?: \[precise[^\]]*\])? r\d+\.[xyzw]+, r\d+\.[xyzw]+, (-cb\d+\[\d+\]\.[xyzw]+)$");
                if (!add.Success || !sub.Success) continue;
                string op = add.Groups[1].Value, register = add.Groups[2].Value, mask = add.Groups[3].Value;
                int first = Math.Min(i, half), second = Math.Max(i, half);
                result[first] = $"{op} r99{mask}, {sub.Groups[1].Value}, {add.Groups[5].Value}";
                result[second] = $"{op} {register}{mask}, {add.Groups[4].Value}, r99{Swizzle(add.Groups[4].Value)}";
                return string.Join('\n', result);
            }
            throw new InvalidOperationException("no straight-line head to rewrite");
        }

        // The swizzle a source register reads, so r99 is read the same way.
        static string Swizzle(string source) => source[source.IndexOf('.', StringComparison.Ordinal)..];

        static bool IsJitter(string line) => Regex.IsMatch(line, @"^\s*add(?: \[precise[^\]]*\])? r\d+\.[xyzw]+, r\d+\.[xyzw]+, -cb\d+\[\d+\]\.[xyzw]+$");
    }
}
