using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    const string BugReportsCore = "KhaozEngine.BugReports";
    const string BugReportsClient = "KhaozEngine.BugReports.Client";
    const string BugReportsContracts = "KhaozEngine.BugReports.Contracts";

    /// <summary>The wire contracts are a packable BCL-only leaf.</summary>
    [Fact]
    public void BugReportsContracts_HasNoReferences()
    {
        Project contracts = LoadGraph()[BugReportsContracts];
        Assert.True(contracts.IsPackableLibrary);
        Assert.Empty(contracts.ProjectRefs);
        Assert.DoesNotContain(contracts.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

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

    /// <summary>The report core references only the headless packages it declares.</summary>
    [Fact]
    public void BugReports_ReferencesItsDeclaredSet()
    {
        Project core = LoadGraph()[BugReportsCore];
        Assert.Equal(new[] { "BugReports.Contracts", "Diagnostics", "Http", "Platform" },
            core.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The report core declares no third-party package.</summary>
    [Fact]
    public void BugReports_HasNoThirdPartyPackage()
    {
        Project core = LoadGraph()[BugReportsCore];
        Assert.DoesNotContain(core.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>Bug reports are a consumer choice, so no umbrella carries any report package.</summary>
    [Fact]
    public void BugReportsPackages_AreExplicitReferencesOutsideEveryUmbrella()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Assert.True(graph[BugReportsClient].IsPackableLibrary);
        Assert.True(graph[BugReportsContracts].IsPackableLibrary);
        foreach (string umbrella in Umbrellas)
        {
            HashSet<string> closure = TransitiveClosure(umbrella, graph);
            Assert.DoesNotContain(BugReportsCore, closure);
            Assert.DoesNotContain(BugReportsClient, closure);
            Assert.DoesNotContain(BugReportsContracts, closure);
        }
    }

    /// <summary>The client package references exactly the engine projects its capture, media and sections build on.</summary>
    [Fact]
    public void BugReportsClient_ReferencesItsDeclaredSet()
    {
        Project client = LoadGraph()[BugReportsClient];
        Assert.Equal(
            new[]
            {
                "App", "BugReports", "BugReports.Contracts", "Diagnostics", "Game", "Gpu", "Gui", "Imaging", "Windowing",
            },
            client.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The client tests reference exactly the engine projects their section and fact tests build on.</summary>
    [Fact]
    public void BugReportsClientTests_ReferenceTheirDeclaredSet()
    {
        Project tests = LoadGraph()["KhaozEngine.BugReports.Client.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(
            new[]
            {
                "App", "BugReports", "BugReports.Client", "BugReports.Contracts", "Diagnostics", "Game", "Gui",
                "Imaging", "Primitives", "Render2D", "TestSupport.Windowing", "Windowing",
            },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
