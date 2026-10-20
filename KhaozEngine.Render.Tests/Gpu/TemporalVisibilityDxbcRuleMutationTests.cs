using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static KhaozEngine.Tests.Gpu.TemporalVisibilityDxbcMutations;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// One mutation of FXC's real disassembly per rule of <see cref="TemporalVisibilityDxbcComparison"/> (review 7f
    /// r4, I-1, M-2 and M-3). Each must fail the check with that rule's message and no other, so a pass of the real
    /// programs shows every rule held rather than one rule hiding behind another. The in-loop head swap and the shared
    /// fast path head are in <see cref="TemporalVisibilityDxbcRealProgramTests"/>. Device-free on every leg.
    /// </summary>
    public sealed class TemporalVisibilityDxbcRuleMutationTests
    {
        public static TheoryData<string> Programs => TemporalVisibilityDxbcRealProgramTests.Programs;

        /// <summary>The six full programs, whose every chain step FXC marks precise.</summary>
        public static TheoryData<string> PrecisePrograms
        {
            get
            {
                var data = new TheoryData<string>();
                foreach ((string name, _) in D3D11FxcValidationTests.VisibilityPrograms) data.Add(name);
                return data;
            }
        }

        static (string D, string Shipped) Load(string program) =>
            (TemporalVisibilityDxbcRealProgramTests.Load(program, d: true), TemporalVisibilityDxbcRealProgramTests.Load(program, d: false));

        // The failures, every one of them the rule's own.
        static List<string> Only(string program, string d, string shipped, string rule)
        {
            List<string> failures = TemporalVisibilityDxbcComparison.Compare(program, d, shipped);
            Assert.NotEmpty(failures);
            Assert.All(failures, f => Assert.StartsWith($"{program}: {rule}", f, StringComparison.Ordinal));
            return failures;
        }

        /// <summary>Rule: both programs declare the same indexable temps. The shipped program declares one more.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void AnAddedIndexableTempFails(string program)
        {
            (string d, string shipped) = Load(program);
            Assert.Single(Only(program, d, AddIndexableTemp(shipped), "indexable temps ["));
        }

        /// <summary>Rule: D's DXBC shows two in-loop chains or more. D's program loses one of its two.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void DWithOneInLoopChainFails(string program)
        {
            (string d, string shipped) = Load(program);
            List<End> ends = Ends(d, inLoop: true);
            Assert.Equal(2, ends.Count);
            Assert.Equal([$"{program}: D's DXBC shows 1 in-loop position chain components, not 2 or more"],
                TemporalVisibilityDxbcComparison.Compare(program, Remove(d, ends[0]), shipped));
        }

        /// <summary>Rule: D's DXBC shows no straight-line chain. D's program gains a copy of its in-loop chain's two
        /// components in straight-line code.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void DWithAStraightLineChainFails(string program)
        {
            (string d, string shipped) = Load(program);
            string copied = CopyOutOfLoop(d, Ends(d, inLoop: true)[^1]);
            Assert.Equal(2, Side(copied, inLoop: false).Count);
            Assert.Equal(Side(d, inLoop: true), Side(copied, inLoop: true));
            Assert.Equal([$"{program}: D's DXBC shows 2 straight-line position chain components, not 0"],
                TemporalVisibilityDxbcComparison.Compare(program, copied, shipped));
        }

        /// <summary>Rule: exactly six straight-line chains. One fast path chain's last step loses its component, so
        /// five remain, or is repeated into a fresh register, so seven read.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void FiveOrSevenStraightLineChainsFail(string program)
        {
            (string d, string shipped) = Load(program);
            List<End> ends = Ends(shipped, inLoop: false);
            Assert.Equal(TemporalVisibilityDxbcComparison.FastPathComponents, ends.Count);
            Assert.Equal([$"{program}: 5 straight-line position chain components, 6 expected"],
                TemporalVisibilityDxbcComparison.Compare(program, d, Remove(shipped, ends[0])));
            Assert.Equal([$"{program}: 7 straight-line position chain components, 6 expected"],
                TemporalVisibilityDxbcComparison.Compare(program, d, Duplicate(shipped, ends[0])));
        }

        /// <summary>Rule: each straight-line chain has a remainder one of D's chains has. A fast path chain scales by
        /// constant-buffer element 99, which no chain of D's reads.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void AStraightLineRemainderNoChainOfDsHasFails(string program)
        {
            (string d, string shipped) = Load(program);
            List<string> failures = Only(program, d, RescaleFrom99(shipped, Ends(shipped, inLoop: false)[0]),
                "a straight-line position chain whose remainder no chain of D's has: ");
            Assert.All(failures, f => Assert.Contains("[99].", f, StringComparison.Ordinal));
        }

        /// <summary>Rule: each straight-line chain's jitter element and remainder are one of D's chains' together
        /// (review 7f r4, M-3). The first fast path jitter subtract reads the other element on each component, so a
        /// chain subtracts <c>.x</c> while it divides and scales by the <c>.y</c> elements. Three chains still subtract
        /// each element and every remainder is still one of D's.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void AStraightLineJitterBesideTheOtherAxisRemainderFails(string program)
        {
            (string d, string shipped) = Load(program);
            string rule = "a straight-line position chain whose remainder D's chains reach only from another jitter element: ";
            List<string> failures = Only(program, d, SwapJitterElements(shipped), rule);
            Assert.Contains(failures, f =>
            {
                string chain = f[$"{program}: {rule}".Length..];
                return TemporalVisibilityDxbc.Jitter(chain)?.EndsWith(".x", StringComparison.Ordinal) == true
                    && Regex.Matches(TemporalVisibilityDxbc.Remainder(chain), @"cb\d+\[\d+\]\.([xyzw])")
                        .All(m => m.Groups[1].Value == "y");
            });
        }

        /// <summary>Rule: three straight-line chains per jitter element. One fast path chain subtracting <c>.x</c> is
        /// replaced by a copy of one subtracting <c>.y</c>, so six chains read, each with D's jitter element and
        /// remainder together. A chain whose jitter element alone switches fails the pair rule above first.</summary>
        [Theory]
        [MemberData(nameof(Programs))]
        public void FourChainsOnOneJitterElementFail(string program)
        {
            (string d, string shipped) = Load(program);
            List<End> ends = Ends(shipped, inLoop: false);
            string[] jitters = ends.Select(e => TemporalVisibilityDxbc.Jitter(e.Signature)!).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(2, jitters.Length);
            Assert.EndsWith(".x", jitters[0], StringComparison.Ordinal);
            End x = ends.First(e => TemporalVisibilityDxbc.Jitter(e.Signature) == jitters[0]);
            End y = ends.First(e => TemporalVisibilityDxbc.Jitter(e.Signature) == jitters[1]);
            string mutated = Duplicate(Remove(shipped, x), y);
            Assert.Equal(TemporalVisibilityDxbcComparison.FastPathComponents, Side(mutated, inLoop: false).Count);
            Assert.Single(Only(program, d, mutated, "the straight-line chains subtract ["));
        }

        /// <summary>Rule: head precise markings (review 7f r4, M-2). The half add of the first fast path head loses its
        /// precise marking, which no chain of D's lacks, so a driver could fold it with the jitter. Taken off D's
        /// in-loop head inside the shipped program, it fails as an in-loop difference.</summary>
        [Theory]
        [MemberData(nameof(PrecisePrograms))]
        public void AHeadAddWithoutItsPreciseMarkingFails(string program)
        {
            (string d, string shipped) = Load(program);
            string fast = UnmarkHalf(shipped, inLoop: false), slow = UnmarkHalf(shipped, inLoop: true);
            Assert.NotEqual(shipped, fast);
            Assert.NotEqual(shipped, slow);
            Only(program, d, fast, "a straight-line position chain whose head precise markings differ from every chain of D's "
                + "with its jitter element and remainder: ");
            List<string> failures = TemporalVisibilityDxbcComparison.Compare(program, d, slow);
            Assert.Contains($"{program}: the shipped program's in-loop chains differ from D's", failures);
            Assert.All(failures, f => Assert.True(f.StartsWith($"{program}: the shipped program's in-loop chains differ from D's",
                StringComparison.Ordinal) || f.StartsWith($"{program}:   ", StringComparison.Ordinal), f));
        }
    }
}
