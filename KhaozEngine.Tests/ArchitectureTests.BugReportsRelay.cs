using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    const string BugReportsRelay = "KhaozEngine.BugReports.Relay";
    const string BugReportsRelayTests = "KhaozEngine.BugReports.Relay.Tests";
    const string BugReportsRelayAzure = "KhaozEngine.BugReports.Relay.Azure";

    /// <summary>
    /// The relay is host-agnostic: the report core, the wire contracts, the engine logger and the retrying HTTP helper,
    /// and no third-party package. Its absence from every umbrella is held by
    /// <see cref="OptInBackends_AreNotReachableFromAnyUmbrella"/>.
    /// </summary>
    [Fact]
    public void BugReportsRelay_ReferencesOnlyItsEngineEdges()
    {
        Project relay = LoadGraph()[BugReportsRelay];
        Assert.True(relay.IsPackableLibrary);
        Assert.Equal(new[] { "BugReports", "BugReports.Contracts", "Diagnostics", "Http" },
            relay.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(relay.PackageRefs, package => !IgnoredInfraPackages.Contains(package));
    }

    /// <summary>The relay tests reference exactly the engine projects their bundles, logs, images and HTTP fakes use.</summary>
    [Fact]
    public void BugReportsRelayTests_ReferenceOnlyWhatTheyUse()
    {
        Project tests = LoadGraph()[BugReportsRelayTests];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "BugReports", "BugReports.Contracts", "BugReports.Relay", "Diagnostics", "Http", "Imaging" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    /// <summary>The opt-in backend contains exactly the storage SDKs and depends only on the relay.</summary>
    [Fact]
    public void BugReportsRelayAzure_ReferencesRelayAndTheThreeSdksOnly()
    {
        Project backend = LoadGraph()[BugReportsRelayAzure];
        Assert.True(backend.IsPackableLibrary);
        Assert.Equal(new[] { "BugReports.Relay" }, backend.ProjectRefs.Select(Short).ToArray());
        Assert.Equal(new[] { "Azure.Data.Tables", "Azure.Storage.Blobs", "Azure.Storage.Queues" },
            backend.PackageRefs.Where(package => !IgnoredInfraPackages.Contains(package))
                .OrderBy(package => package, StringComparer.Ordinal).ToArray());
    }

    /// <summary>No umbrella transitively installs the Azure SDKs for consumers.</summary>
    [Fact]
    public void BugReportsRelayAzure_IsAnExplicitReferenceOutsideEveryUmbrella()
    {
        IReadOnlyDictionary<string, Project> graph = LoadGraph();
        Assert.True(graph[BugReportsRelayAzure].IsPackableLibrary);
        foreach (string umbrella in Umbrellas)
            Assert.DoesNotContain(BugReportsRelayAzure, TransitiveClosure(umbrella, graph));
    }

    /// <summary>The backend tests carry only the engine projects they exercise.</summary>
    [Fact]
    public void BugReportsRelayAzureTests_ReferenceOnlyTheRelayAndBackend()
    {
        Project tests = LoadGraph()["KhaozEngine.BugReports.Relay.Azure.Tests"];
        Assert.False(tests.IsPackableLibrary);
        Assert.Equal(new[] { "BugReports.Relay", "BugReports.Relay.Azure" },
            tests.ProjectRefs.Select(Short).OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }
}
