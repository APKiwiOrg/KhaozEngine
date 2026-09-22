using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// What a run does with a fault that is NOT contention. A permissions failure and a dropped connection reach
/// the runner as the same exception type a deadlock does, and the difference between them is whether the
/// provider calls the fault transient.
/// <para>
/// Retrying a non-transient fault costs a whole attempt budget of replans to produce a message saying
/// another publisher held the draft, which sends an operator to the wrong place entirely.
/// </para>
/// </summary>
public sealed class ContentUpgradeFaultTests
{
    /// <summary>
    /// A fault from the BASELINE EXPORT is reported rather than thrown. The export sits outside the publish
    /// path, so a run that let it escape handed the host a stack trace instead of the report the whole
    /// design promises.
    /// </summary>
    [Fact]
    public async Task ANonTransientFaultFromTheExportIsOneAttemptAndAFailedReport()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var faulting = new DatabaseFaultStore(harness.Store, onExport: true, onPublish: false);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            faulting, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        await AssertFailedOnceAsync(harness, report);
        Assert.Equal(1, faulting.Exports);
        Assert.Contains(
            "export the baseline bundle", Message(report), StringComparison.Ordinal);
        Assert.Contains(UpgradeHarness.FirstId, Message(report), StringComparison.Ordinal);
    }

    /// <summary>A fault from the PUBLISH costs one attempt too, and leaves no draft standing.</summary>
    [Fact]
    public async Task ANonTransientFaultFromThePublishIsOneAttemptAndAFailedReport()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var faulting = new DatabaseFaultStore(harness.Store, onExport: false, onPublish: true);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            faulting, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        await AssertFailedOnceAsync(harness, report);
        Assert.Equal(1, faulting.Publishes);
    }

    /// <summary>
    /// A TRANSIENT fault is still contention and is still waited out, which is the case the blanket retry
    /// was right about and the only one it was right about.
    /// </summary>
    [Fact]
    public async Task ATransientFaultFromThePublishIsRetried()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var faulting = new DatabaseFaultStore(
            harness.Store, onExport: false, onPublish: true, transient: true, faults: 1);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            faulting, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, faulting.Publishes);
        Assert.Equal(3, report.ActiveVersionAfter);
    }

    /// <summary>A failed run stops at one attempt, reports the code and leaves the catalog as it stands.</summary>
    static async Task AssertFailedOnceAsync(UpgradeHarness harness, ContentUpgradeReport report)
    {
        Assert.Equal(ContentUpgradeOutcome.Failed, report.Outcome);
        Assert.False(report.Success);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.PublishFailed, StringComparison.Ordinal));
        Assert.Null(await harness.Store.GetOpenDraftAsync());
        Assert.Single(await harness.Store.ListVersionsAsync());
        Assert.Empty(await harness.Store.ListUpgradesAsync());
    }

    /// <summary>The failure diagnostic's message, which has to name the upgrade and what it was doing.</summary>
    static string Message(ContentUpgradeReport report)
    {
        for (int i = 0; i < report.Diagnostics.Count; i++)
        {
            if (string.Equals(
                report.Diagnostics[i].Code, ContentUpgradeCodes.PublishFailed, StringComparison.Ordinal))
            {
                return report.Diagnostics[i].Message;
            }
        }

        return string.Empty;
    }
}
