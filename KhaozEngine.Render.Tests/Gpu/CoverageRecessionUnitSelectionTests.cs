using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageRecessionUnitSelectionTests
{
    const string Unit = "Real_body_size_carry_and_scene_overhead_for_1_and_32_actors";
    const string Cost = "KhaozEngine.Tests.Gpu.CoverageRecessionCostGpuTests." + Unit;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-unit")]
    [InlineData("real_body_size_carry_and_scene_overhead_for_1_and_32_actors")]
    public void General_gpu_opt_in_cannot_execute_the_cost_unit_through_broad_or_unfiltered_selection(string? unit)
    {
        // Simulate the metadata inputs. Never set KE_GPU_TESTS=1 or probe a device in this test.
        var fact = new CoverageRecessionHardwareFactAttribute("1", unit,
            () => throw new InvalidOperationException("An unapproved selection must not probe hardware."));
        Assert.NotNull(fact.Skip);
        foreach (string? filter in new[] { null, "FullyQualifiedName~CoverageRecession", "FullyQualifiedName=" + Cost })
        {
            bool matches = filter is null || filter.Contains("~CoverageRecession", StringComparison.Ordinal)
                || filter == "FullyQualifiedName=" + Cost;
            Assert.True(matches);
            string[] executable = new[] { Cost }.Where(_ => matches && fact.Skip is null).ToArray();
            Assert.Empty(executable);
        }
    }

    [Fact]
    public void Only_the_exact_unit_and_gpu_opt_ins_reach_the_existing_hardware_gate()
    {
        int probes = 0;
        string? Probe() { probes++; return null; }
        Assert.NotNull(new CoverageRecessionHardwareFactAttribute("0", Unit, Probe).Skip);
        Assert.NotNull(new CoverageRecessionHardwareFactAttribute(null, Unit, Probe).Skip);
        Assert.Equal(0, probes);
        Assert.Null(new CoverageRecessionHardwareFactAttribute("1", Unit, Probe).Skip);
        Assert.Equal(1, probes);
        Assert.NotNull(new CoverageRecessionHardwareFactAttribute("1", Unit, () => "Hardware refused.").Skip);
    }
}
