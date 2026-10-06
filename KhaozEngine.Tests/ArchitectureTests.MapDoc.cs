using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    const string Harness = "KhaozEngine.MapDoc.OracleHarness.Tests";

    [Fact]
    public void OracleHarness_IsOutsideTheSolutionAndBothCiTestSelections()
    {
        string root = RepoRoot();
        var slnxPaths = Regex.Matches(File.ReadAllText(Path.Combine(root, "KhaozEngine.slnx")), "Path=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.DoesNotContain(slnxPaths, p => p.Contains(Harness, StringComparison.Ordinal));                     // full path builds and tests the slnx
        Assert.Equal(new[] { "KhaozEngine.slnx" }, Directory.EnumerateFiles(root, "*.sln*").Select(Path.GetFileName)); // bare dotnet resolves to it
        Assert.Empty(Directory.EnumerateFiles(root, "*.csproj"));
        string selective = File.ReadAllText(Path.Combine(root, "scripts", "ci-selective-test.sh"));
        Assert.Contains("SLNX=\"KhaozEngine.slnx\"", selective);                                                   // selective intersects with slnx test paths
        Assert.Contains("grep -oE 'Path=\"[^\"]+\"' \"$SLNX\"", selective);
        Assert.DoesNotContain(Harness, selective);
        Assert.DoesNotContain(Harness, File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml")));
        Assert.True(File.Exists(Path.Combine(root, Harness, Harness + ".csproj")));
    }

    [Fact]
    public void OracleHarness_IsUnreferencedAndNotPackable()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Assert.DoesNotContain(graph.Values, p => p.ProjectRefs.Contains(Harness));
        Assert.False(graph[Harness].IsPackableLibrary);
        Assert.Contains(XDocument.Load(Path.Combine(RepoRoot(), Harness, Harness + ".csproj")).Root!.Descendants("IsPackable"),
            e => string.Equals((string?)e, "false", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TileWorld_ReachesMapDocProjectsOnlyThroughTheTwoOracleTestProjects()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        string[] allowed = { "KhaozEngine.MapDoc.Compatibility.Tests", Harness };
        foreach (Project p in graph.Values.Where(p => p.Name.StartsWith("KhaozEngine.MapDoc", StringComparison.Ordinal) || p.Name.StartsWith("KhaozEngine.MapEdit", StringComparison.Ordinal)))
            if (TransitiveClosure(p.Name, graph).Contains("KhaozEngine.TileWorld")) Assert.Contains(p.Name, allowed);
    }
}
