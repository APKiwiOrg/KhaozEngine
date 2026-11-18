using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Requires the real FXC listings to branch around the shipped release's twelve scalar barrier ORs.
/// Task 7h Z round 2 retains the branch through FXC. D prime keeps its unqualified helper call.</summary>
public sealed class TemporalLockReleaseDxbcTests(ITestOutputHelper output)
{
    public static TheoryData<string> Programs => new()
    {
        "TemporalAccumulateFrag", "TemporalAccumulateHalfFrag", "TemporalAccumulateAtDisplaySizeFrag",
        "TemporalAccumulateAtDisplaySizeHalfFrag", "TemporalResolveFrag", "TemporalResolveHalfFrag",
    };

    [Theory]
    [MemberData(nameof(Programs))]
    public void TwelveScalarBarrierOrsAreConditionalOnlyInShippedPrograms(string program)
    {
        foreach (bool d in new[] { true, false })
            CheckBarriers(program, TemporalVisibilityDxbcRealProgramTests.Load(program, d), conditional: !d);
    }

    public static IEnumerable<object[]> AdditionalPrograms => TemporalLockReleaseDxbcPrograms.Additional
        .Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(AdditionalPrograms))]
    public void EveryAdditionalShippedProgramHasCurrentUnmarkedConditionalBarriers(string program)
    {
        string source = TemporalLockReleaseDxbcPrograms.Additional.Single(p => p.Name == program).Source;
        string pin = Path.Combine(AppContext.BaseDirectory, "Fixtures", "taa-visibility-dxbc",
            program + ".shipped.glsl.sha256");
        Assert.Equal(TemporalVisibilityFrozenD.Sha256(source), File.ReadAllText(pin).Trim());
        string listing = TemporalVisibilityDxbcRealProgramTests.Load(program, d: false);
        Assert.DoesNotContain("[precise", listing, StringComparison.Ordinal);
        if (program.Contains("FullyLit", StringComparison.Ordinal))
            Assert.DoesNotContain(TemporalVisibilityDxbc.Parse(listing), i => i.Opcode.StartsWith("gather", StringComparison.Ordinal));
        CheckBarriers(program, listing, conditional: true);
    }

    [Fact]
    public void DxbcCoverageIncludesEveryCheckedAndCertifiedRelease()
    {
        // The detail history adds eight release programs and no certified one.
        Assert.Equal(28, TemporalLockReleaseDxbcPrograms.All.Length);
        Assert.Equal(22, TemporalLockReleaseDxbcPrograms.Additional.Length);
        Assert.Equal(10, TemporalLockReleaseDxbcPrograms.All.Count(p => p.Name.Contains("FullyLit", StringComparison.Ordinal)));
        Assert.All(TemporalLockReleaseDxbcPrograms.All,
            p => Assert.Contains("[[dont_flatten]] if", p.Source, StringComparison.Ordinal));
    }

    void CheckBarriers(string program, string listing, bool conditional)
    {
        var instructions = TemporalVisibilityDxbc.Parse(listing);
        // FXC may hoist the integer sign shift or keep one per inlined release. Either way every barrier OR reads one
        // of them, and the program carries twelve conditional scalar barriers for each release it compiles.
        int[] shifts = Enumerable.Range(0, instructions.Count).Where(n => instructions[n].Opcode == "ushr"
            && instructions[n].Sources.Length == 2 && instructions[n].Sources[0].StartsWith("cb", StringComparison.Ordinal)
            && instructions[n].Sources[1] == "l(31)").ToArray();
        int releases = Render3D.TemporalLockReleaseSpirv.Releases(program[..^"Frag".Length]);
        Assert.Equal(releases > 0, shifts.Length > 0);
        // A program with one release keeps its one shift, as every program did before the detail history.
        if (releases == 1) Assert.Single(shifts);
        int total = 0;
        foreach (int shift in shifts)
        {
            string sign = Assert.Single(instructions[shift].Destinations);
            int branches = 0, scalarOrs = 0, orInstructions = 0;
            bool tracking = false;
            for (int n = 0; n < instructions.Count; n++)
            {
                var instruction = instructions[n];
                if (instruction.Opcode is "if_nz" or "if_z") branches++;
                if (instruction.Opcode == "endif") branches--;
                if (n == shift)
                {
                    // Every ordered barrier OR after its shift must stay conditional.
                    tracking = true;
                    continue;
                }
                if (!tracking) continue;
                if (instruction.Opcode == "or" && instruction.Sources.Any(s => Reads(s, sign)))
                {
                    string destination = Assert.Single(instruction.Destinations);
                    scalarOrs += destination.Length - destination.IndexOf('.', StringComparison.Ordinal) - 1;
                    orInstructions++;
                    Assert.True(conditional ? branches > 0 : branches == 0,
                        $"{program}: barrier OR at conditional depth {branches}");
                }
                if (instruction.Destinations.Any(s => Reads(s, sign))) break;
            }
            output.WriteLine($"{program} {(conditional ? "shipped" : "D")}: {orInstructions} OR instructions, {scalarOrs} scalar barriers");
            total += scalarOrs;
        }
        Assert.Equal(12 * releases, total);
    }

    static bool Reads(string operand, string scalar)
    {
        int dot = operand.IndexOf('.', StringComparison.Ordinal), scalarDot = scalar.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 && scalarDot >= 0 && operand[..dot] == scalar[..scalarDot]
            && operand[(dot + 1)..].Contains(scalar[^1], StringComparison.Ordinal);
    }
}
