using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests;

public partial class ArchitectureTests
{
    const string BugReportsRelay = "KhaozEngine.BugReports.Relay";
    const string BugReportsRelayTests = "KhaozEngine.BugReports.Relay.Tests";

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
}
