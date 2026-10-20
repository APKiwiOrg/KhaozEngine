using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfDiscoveryTests
{
    internal const string Project = "tools/EquipmentCoverage.Perf.Tests/EquipmentCoverage.Perf.Tests.csproj";
    internal const string Filter = "FullyQualifiedName=EquipmentCoverage.Perf.Tests.CoveragePerfTests.Measure_authorized_unit&Category=CoveragePerformance";
    const string Name = "EquipmentCoverage.Perf.Tests";
    static readonly string[] MeasurementSteps =
        { "Verify coverage authorization commit", "Build coverage performance project", "Measure authorized coverage unit" };

    [Fact]
    public void Perf_project_is_outside_the_solution_and_every_ordinary_selection()
    {
        string root = Root();
        Assert.True(File.Exists(Path.Combine(root, Project)), "The opt-in Perf project must exist.");
        Assert.DoesNotContain(Name, Read("KhaozEngine.slnx"), StringComparison.Ordinal);
        Assert.Equal(new[] { "KhaozEngine.slnx" }, Directory.EnumerateFiles(root, "*.sln*").Select(Path.GetFileName));
        Assert.Empty(Directory.EnumerateFiles(root, "*.csproj"));
        string selective = Read("scripts/ci-selective-test.sh");
        Assert.DoesNotContain(Name, CoverageCostContractTests.ActiveSelectionText(selective), StringComparison.Ordinal);
        Assert.Contains("grep -oE 'Path=\"[^\"]+\"' \"$SLNX\"", selective, StringComparison.Ordinal);
        foreach (string project in Projects(root, 3).Where(p => !p.EndsWith(Name + ".csproj", StringComparison.Ordinal)))
            Assert.DoesNotContain(Name, File.ReadAllText(project), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_explicit_manual_measurement_steps_may_name_the_project()
    {
        foreach (string workflow in Directory.EnumerateFiles(Path.Combine(Root(), ".github/workflows")))
        {
            string active = CoverageCostContractTests.ActiveSelectionText(CoveragePerfWorkflowText.Lf(File.ReadAllText(workflow)));
            if (!active.Contains(Name, StringComparison.Ordinal)) continue;
            Assert.Contains(Path.GetFileName(workflow), new[] { "stress-test.yml", "gpu-hosted-bench.yml" });
            AssertOnlyMeasurementStepsNameProject(active);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void An_unnamed_unconditional_step_cannot_inherit_measurement_authorization(string newline)
    {
        string workflow = string.Join(newline, "jobs:", "  bench:", "    steps:",
            "      - name: Verify coverage authorization commit", "        if: inputs.coverage-perf-unit != ''",
            "        run: echo verified", "      - uses: actions/setup-dotnet@pinned", "        with:",
            "          dotnet-version: " + Name, "");
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(Record.Exception(() => AssertOnlyMeasurementStepsNameProject(workflow)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Gate_text_inside_a_script_does_not_authorize_a_step(string newline)
    {
        string workflow = string.Join(newline, "jobs:", "  bench:", "    steps:",
            "      - name: Build coverage performance project", "        if: always()", "        run: |",
            "          echo \"if: inputs.coverage-perf-unit != ''\"", "          dotnet build " + Name, "");
        Assert.IsAssignableFrom<Xunit.Sdk.XunitException>(Record.Exception(() => AssertOnlyMeasurementStepsNameProject(workflow)));
    }

    static void AssertOnlyMeasurementStepsNameProject(string workflow)
    {
        var steps = CoveragePerfWorkflowText.Steps(workflow);
        Assert.DoesNotContain(Name, steps[0], StringComparison.Ordinal);
        foreach (string step in steps.Skip(1).Where(s => s.Contains(Name, StringComparison.Ordinal)))
        {
            // The gate must be the step's own condition line, not text that merely appears inside its script.
            Assert.Contains(MeasurementSteps, name => step.StartsWith(
                "name: " + name + "\n        if: inputs.coverage-perf-unit != ''\n", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Entrypoint_is_one_env_gated_fact_that_never_scans_authorizations()
    {
        string source = Read("tools/EquipmentCoverage.Perf.Tests/CoveragePerfTests.cs");
        Assert.Contains("[CoveragePerfFact]", source, StringComparison.Ordinal);
        Assert.Contains("[Trait(\"Category\", \"CoveragePerformance\")]", source, StringComparison.Ordinal);
        Assert.Contains("public void Measure_authorized_unit()", source, StringComparison.Ordinal);
        Assert.Contains("Skip =", source, StringComparison.Ordinal);
        Assert.Contains("KE_COVERAGE_PERF_UNIT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("[Theory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("EnumerateFiles", source, StringComparison.Ordinal);
        Assert.Contains("authorized/*.json", Read(Project), StringComparison.Ordinal);
    }

    internal static string Read(string path) => CoveragePerfWorkflowText.Lf(File.ReadAllText(Path.Combine(Root(), path)));
    internal static string Root([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../.."));

    static IEnumerable<string> Projects(string directory, int depth)
    {
        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "node_modules" || name.StartsWith('.')) continue;
            foreach (string project in Directory.EnumerateFiles(child, "*.csproj")) yield return project;
            if (depth > 1) foreach (string project in Projects(child, depth - 1)) yield return project;
        }
    }
}
