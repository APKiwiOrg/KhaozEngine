using System;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Requires the real FXC listings to branch around the shipped release's twelve scalar barrier ORs.
/// Task 7h Z's capture exposes flattening and fails this gate. D prime retains its unqualified helper call.</summary>
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
        {
            var instructions = TemporalVisibilityDxbc.Parse(TemporalVisibilityDxbcRealProgramTests.Load(program, d));
            var shift = Assert.Single(instructions, i => i.Opcode == "ushr" && i.Sources.Length == 2
                && i.Sources[0].StartsWith("cb", StringComparison.Ordinal) && i.Sources[1] == "l(31)");
            string sign = Assert.Single(shift.Destinations);
            int branches = 0, scalarOrs = 0, orInstructions = 0;
            bool tracking = false;
            foreach (var instruction in instructions)
            {
                if (instruction.Opcode is "if_nz" or "if_z") branches++;
                if (instruction.Opcode == "endif") branches--;
                if (instruction.Equals(shift)) { tracking = true; continue; }
                if (!tracking) continue;
                if (instruction.Opcode == "or" && instruction.Sources.Any(s => Reads(s, sign)))
                {
                    string destination = Assert.Single(instruction.Destinations);
                    scalarOrs += destination.Length - destination.IndexOf('.', StringComparison.Ordinal) - 1;
                    orInstructions++;
                    Assert.True(d ? branches == 0 : branches > 0,
                        $"{program} {(d ? "D" : "shipped")}: barrier OR at conditional depth {branches}");
                }
                if (instruction.Destinations.Any(s => Reads(s, sign))) break;
            }
            output.WriteLine($"{program} {(d ? "D" : "shipped")}: {orInstructions} OR instructions, {scalarOrs} scalar barriers");
            Assert.Equal(12, scalarOrs);
        }
    }

    static bool Reads(string operand, string scalar)
    {
        int dot = operand.IndexOf('.', StringComparison.Ordinal), scalarDot = scalar.IndexOf('.', StringComparison.Ordinal);
        return dot >= 0 && scalarDot >= 0 && operand[..dot] == scalar[..scalarDot]
            && operand[(dot + 1)..].Contains(scalar[^1], StringComparison.Ordinal);
    }
}
