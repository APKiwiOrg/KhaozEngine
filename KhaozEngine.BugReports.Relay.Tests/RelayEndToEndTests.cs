using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.Tests.BugReports.Relay.Support;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

public sealed class RelayEndToEndTests
{
    [Fact]
    public async Task EndToEnd_IntakeThenWorker_EveryBodyLinkResolvesToAStoredBlob()
    {
        var harness = new RelayHarness();
        await WorkerFixtures.SendAsync(harness);
        Assert.Equal(202, (await harness.IntakeAsync()).StatusCode);
        await WorkerFixtures.DrainAsync(harness);
        string body = Assert.Single(harness.GitHub.Issues).Body;
        string[] urls = Regex.Matches(body, "https://[^)\\\"\\s<>]+").Select(m => m.Value).ToArray();
        Assert.True(urls.Length >= 8);
        foreach (string url in urls)
        {
            Assert.StartsWith(harness.Options.PublicMediaBase.AbsoluteUri, url, StringComparison.Ordinal);
            string path = url[harness.Options.PublicMediaBase.AbsoluteUri.Length..];
            Assert.True(await harness.Media.ExistsAsync(ReportArea.Public, path, CancellationToken.None), path);
        }
    }

    [Fact]
    public async Task EndToEnd_ResendAfterIssueCreated_NoSecondIssue()
    {
        var harness = new RelayHarness();
        var first = await harness.IntakeAsync();
        await WorkerFixtures.DrainAsync(harness);
        string[] paths = harness.Media.Paths(ReportArea.Public).ToArray();
        harness.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(first.JsonBody, (await harness.IntakeAsync()).JsonBody);
        await WorkerFixtures.DrainAsync(harness);
        Assert.Single(harness.GitHub.Issues);
        Assert.Equal(paths, harness.Media.Paths(ReportArea.Public));
    }

    [Fact]
    public async Task EndToEnd_ProcessWork_UndefinedKind_Throws()
        => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new RelayHarness().Relay.ProcessWorkAsync(
            new ReportWorkItem((ReportWorkKind)99, RelayHarness.Id), CancellationToken.None));
}
