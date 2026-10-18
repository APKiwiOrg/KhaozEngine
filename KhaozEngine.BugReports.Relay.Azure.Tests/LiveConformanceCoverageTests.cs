using System;
using System.Linq;
using System.Reflection;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Every shared conformance fact must have a separately gated live counterpart.</summary>
public sealed class LiveConformanceCoverageTests
{
    [Theory]
    [InlineData(typeof(ReportMediaStoreConformance), "Media")]
    [InlineData(typeof(ReportQuotaStoreConformance), "Quota")]
    public void EveryConformanceFact_HasNamedLiveCounterpart(Type suite, string area)
    {
        string[] live = typeof(AzureLiveTests).GetMethods()
            .Where(method => method.GetCustomAttribute<BugReportsAzureFactAttribute>() is not null)
            .Select(method => method.Name).ToArray();
        MethodInfo[] facts = suite.GetMethods().Where(method => method.GetCustomAttribute<FactAttribute>() is not null).ToArray();
        Assert.NotEmpty(facts);
        Assert.All(facts, method => Assert.Contains($"Live_{area}_{method.Name}", live));
    }
}
