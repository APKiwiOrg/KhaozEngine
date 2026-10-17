using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    const string BugReportsCore = "KhaozEngine.BugReports";
    const string BugReportsClient = "KhaozEngine.BugReports.Client";

    /// <summary>The report core stays server-safe: no GPU, window, renderer, GUI or game package in its closure.</summary>
    [Fact]
    public void BugReports_StaysHeadless()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Assert.True(graph[BugReportsCore].IsPackableLibrary);
        HashSet<string> closure = TransitiveClosure(BugReportsCore, graph).Select(Short).ToHashSet(StringComparer.Ordinal);
        string[] hits = GpuRuntimeStack.Append("Game").Where(closure.Contains).ToArray();

        bool clean = hits.Length == 0;
        Assert.True(clean, "KhaozEngine.BugReports is the headless report model but its ProjectReference closure pulls in: "
            + string.Join(", ", hits));
    }

    /// <summary>The report core declares no third-party package.</summary>
    [Fact]
    public void BugReports_HasNoThirdPartyPackage()
    {
        Project core = LoadGraph()[BugReportsCore];
        Assert.DoesNotContain(core.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>Bug reports are a consumer choice, so no umbrella carries either package.</summary>
    [Fact]
    public void BugReportsPackages_AreExplicitReferencesOutsideEveryUmbrella()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Assert.True(graph[BugReportsClient].IsPackableLibrary);
        foreach (string umbrella in Umbrellas)
        {
            HashSet<string> closure = TransitiveClosure(umbrella, graph);
            Assert.DoesNotContain(BugReportsCore, closure);
            Assert.DoesNotContain(BugReportsClient, closure);
        }
    }

    /// <summary>The client tests reference only the client package.</summary>
    [Fact]
    public void BugReportsClientTests_ReferenceOnlyTheClient()
    {
        Project tests = LoadGraph()["KhaozEngine.BugReports.Client.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "BugReports.Client" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
