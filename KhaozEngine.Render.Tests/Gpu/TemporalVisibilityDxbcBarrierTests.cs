using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The barrier lock release leaves FXC free to optimize every visibility program. Its listings must carry
/// no precise marking, and D's in-loop head forms must agree between fused and split entries.</summary>
public sealed class TemporalVisibilityDxbcBarrierTests(ITestOutputHelper output)
{
    public static TheoryData<string> Programs => TemporalVisibilityDxbcRealProgramTests.Programs;

    [Theory]
    [MemberData(nameof(Programs))]
    public void NoPreciseMarkingSurvivesInEitherListing(string program)
    {
        foreach (bool d in new[] { true, false })
        {
            string listing = TemporalVisibilityDxbcRealProgramTests.Load(program, d);
            Assert.DoesNotContain("[precise", listing, StringComparison.Ordinal);
            Assert.All(TemporalVisibilityDxbc.Parse(listing), instruction => Assert.Empty(instruction.Precise));
        }
    }

    [Theory]
    [InlineData("TemporalResolveFrag", "TemporalAccumulateFrag")]
    [InlineData("TemporalResolveFrag", "TemporalAccumulateAtDisplaySizeFrag")]
    [InlineData("TemporalResolveHalfFrag", "TemporalAccumulateHalfFrag")]
    [InlineData("TemporalResolveHalfFrag", "TemporalAccumulateAtDisplaySizeHalfFrag")]
    public void DsInLoopHeadFormsAgreeBetweenFusedAndSplit(string fused, string split)
    {
        foreach (bool d in new[] { true, false })
        {
            TemporalVisibilityDxbc.HeadForm[] a = Forms(fused, d), b = Forms(split, d);
            output.WriteLine($"{(d ? "D" : "shipped")} {fused} [{string.Join(", ", a)}], {split} [{string.Join(", ", b)}]");
            Assert.Equal(2, a.Length);
            Assert.Equal(2, b.Length);
            Assert.DoesNotContain(TemporalVisibilityDxbc.HeadForm.Other, a);
            Assert.DoesNotContain(TemporalVisibilityDxbc.HeadForm.Other, b);
            Assert.Equal(a, b);
        }

        static TemporalVisibilityDxbc.HeadForm[] Forms(string program, bool d) => TemporalVisibilityDxbc
            .Found(TemporalVisibilityDxbcRealProgramTests.Load(program, d)).Where(c => c.LoopDepth > 0)
            .Select(c => TemporalVisibilityDxbc.Form(c.Signature)).Order().ToArray();
    }
}
