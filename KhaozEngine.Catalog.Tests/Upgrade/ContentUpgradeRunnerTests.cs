using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The runner's gates and its apply loop, against the in-memory reference store. The provider legs of the
/// same behaviour are in the shared conformance suite, which runs on SQLite and SQL Server too.
/// </summary>
public sealed class ContentUpgradeRunnerTests
{
    /// <summary>
    /// A FRESH install seeds the current bundle, records the baseline, and then has nothing to do. The run
    /// writes NOTHING: no version, no audit row, no ledger row and no draft.
    /// </summary>
    [Fact]
    public async Task AFreshInstallThatRecordedItsBaselineIsUpToDateWithZeroWrites()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedCurrentCatalogAsync();
        await ContentUpgradeRunner.RecordBaselineAsync(
            harness.Store, harness.Set, UpgradeFixtures.Actor, UpgradeFixtures.Operator);
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.UpToDate, report.Outcome);
        Assert.True(report.Success);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(1, report.ActiveVersionAfter);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(2, before.Ledger);
    }

    /// <summary>
    /// A second apply after a successful one is up to date and writes nothing, AND the planner of an already
    /// recorded definition is never called again. That is the whole point of the ledger: a planner whose
    /// defaults have moved since would otherwise reapply them over an operator's values.
    /// </summary>
    [Fact]
    public async Task ARepeatedApplyIsUpToDateAndNeverPlansARecordedUpgradeAgain()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        int planned = 0;
        var counted = new ContentUpgradeDefinition(
            UpgradeHarness.FirstId,
            1,
            "adds the second type's first row",
            context =>
            {
                planned++;
                return harness.First.Plan(context);
            });
        var set = new ContentUpgradeSet(counted);

        ContentUpgradeReport first = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.Applied, first.Outcome);
        Assert.Equal(1, planned);
        CatalogFootprint after = await harness.FootprintAsync();

        ContentUpgradeReport second = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.UpToDate, second.Outcome);
        Assert.Equal(1, planned);
        Assert.Equal(after, await harness.FootprintAsync());
    }

    /// <summary>
    /// Content that is already there with an EMPTY ledger is recorded as adopted rather than published, which
    /// is the catalog an older explicit upgrade command repaired. No version is published and the next run is
    /// up to date.
    /// </summary>
    [Fact]
    public async Task AlreadySatisfiedContentWithAnEmptyLedgerIsAdoptedAndPublishesNoVersion()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedCurrentCatalogAsync();
        Assert.Empty(await harness.Store.ListUpgradesAsync());

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(1, report.ActiveVersionAfter);
        Assert.Single(await harness.Store.ListVersionsAsync());
        Assert.All(report.Steps, step => Assert.Equal(ContentUpgradeStepState.Adopted, step.State));
        IReadOnlyList<ContentUpgradeRecord> ledger = await harness.Store.ListUpgradesAsync();
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, record => Assert.Equal(ContentUpgradeDisposition.Adopted, record.Disposition));

        ContentUpgradeReport again = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.UpToDate, again.Outcome);
    }

    /// <summary>
    /// Two definitions publish one version EACH, in order, and the second plans against the first one's
    /// published result rather than against the version the run started at.
    /// </summary>
    [Fact]
    public async Task EachPendingDefinitionPublishesItsOwnVersionInOrder()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(1, report.ActiveVersionBefore);
        Assert.Equal(3, report.ActiveVersionAfter);
        Assert.Equal([2, 3], Versions(report));
        Assert.Equal([UpgradeHarness.FirstId, UpgradeHarness.SecondId], Ids(report));
        Assert.Equal(3, (await harness.Store.ListVersionsAsync()).Count);

        // The second row could only be added at id 2, which the planner can only have proved against a
        // baseline that already carried the first one.
        Assert.Equal(2, (await harness.Store.ListRowsAsync(UpgradeFixtures.Other, 0, null, false, 0, 10)).Total);
    }

    /// <summary>A supplied expected version that is not the active one stops the run with nothing changed.</summary>
    [Fact]
    public async Task AnExpectedVersionThatIsNotTheActiveOneIsBaselineMoved()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply(expectedVersion: 99));

        Assert.Equal(ContentUpgradeOutcome.BaselineMoved, report.Outcome);
        Assert.False(report.Success);
        Assert.Equal(3, report.ExitCode);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(ContentUpgradeCodes.BaselineMoved, report.Diagnostics[0].Code);
    }

    /// <summary>
    /// The expected version is checked at EVERY re-read of the active version, not once at the gate. A run
    /// that stood off and came back to a version a rival left would otherwise publish onto a baseline nobody
    /// previewed, which is the whole thing the hosted arm supplies the number for.
    /// </summary>
    [Fact]
    public async Task AnExpectedVersionStopsTheRunWhenTheBaseMovesBetweenAttempts()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var moving = new BaseMovesBetweenAttemptsStore(harness.Store);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            moving, harness.Registry, harness.Set, UpgradeFixtures.Apply(expectedVersion: 1));

        Assert.Equal(ContentUpgradeOutcome.BaselineMoved, report.Outcome);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.BaselineMoved, System.StringComparison.Ordinal));
        Assert.Single(await harness.Store.ListVersionsAsync());
        Assert.Empty(await harness.Store.ListUpgradesAsync());
        Assert.Null(await harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// The same re-read does NOT stop a run standing on a version it published itself, which is the ordinary
    /// way a two-definition apply moves forward under an expected version.
    /// </summary>
    [Fact]
    public async Task AnExpectedVersionDoesNotStopARunStandingOnItsOwnPublishedVersion()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply(expectedVersion: 1));

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);
    }

    /// <summary>
    /// A definition whose ORDER moved since it ran is allowed. The id is the identity, so the catalog holds
    /// it and it never runs again, and the run says so rather than refusing a catalog that is correct.
    /// </summary>
    [Fact]
    public async Task ADefinitionWhoseOrderMovedSinceItRanIsAllowedAndSaysSo()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.RecordUpgradeAsync(
            new ContentUpgradeStamp(UpgradeHarness.FirstId, 7),
            ContentUpgradeDisposition.Adopted,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.UpgradeOrderMoved, System.StringComparison.Ordinal));
        Assert.Equal(2, (await harness.Store.ListUpgradesAsync()).Count);
    }

    /// <summary>
    /// A pending definition ordered BELOW one the catalog already holds is allowed too, which is what two
    /// feature branches merging produces. It runs now, in its own order, and the run says so.
    /// </summary>
    [Fact]
    public async Task APendingDefinitionOrderedBelowAnAppliedOneIsAllowedAndSaysSo()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.RecordUpgradeAsync(
            harness.Second.Stamp,
            ContentUpgradeDisposition.Adopted,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Contains(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code,
                ContentUpgradeCodes.PendingBelowApplied,
                System.StringComparison.Ordinal));
        Assert.Equal(2, report.ActiveVersionAfter);
    }

    /// <summary>The LOCAL arm supplies no expected version and upgrades whatever it finds.</summary>
    [Fact]
    public async Task AnApplyWithNoExpectedVersionUpgradesWhateverItFinds()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply(expectedVersion: null));

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        Assert.Equal(3, report.ActiveVersionAfter);
    }

    /// <summary>
    /// A ledger id this build does not ship means the catalog was upgraded by a newer build. Running the
    /// older set against it is refused with nothing changed.
    /// </summary>
    [Fact]
    public async Task ALedgerIdTheSetDoesNotShipIsCatalogAheadOfBuild()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        await harness.Store.RecordUpgradeAsync(
            new ContentUpgradeStamp("from-a-newer-build", 9),
            ContentUpgradeDisposition.Baseline,
            UpgradeFixtures.Actor,
            UpgradeFixtures.Operator);
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.CatalogAheadOfBuild, report.Outcome);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Contains("from-a-newer-build", report.Diagnostics[0].Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A planner that refuses stops the run where it stands: the definitions before it stay APPLIED and the
    /// ones after it stay pending, because a refusal is about this catalog rather than about the build.
    /// </summary>
    [Fact]
    public async Task APlannerRefusalLeavesEarlierDefinitionsAppliedAndLaterOnesPending()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var set = new ContentUpgradeSet(
            harness.First,
            UpgradeFixtures.Refuses("refuses-here", 2, "this catalog carries half of it."),
            UpgradeFixtures.Adds(
                "runs-after-the-refusal",
                3,
                harness.Target,
                UpgradeFixtures.Identity(UpgradeFixtures.Other, "second_new_row")));

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Refused, report.Outcome);
        Assert.False(report.Success);
        Assert.Equal(2, report.ActiveVersionAfter);
        Assert.Equal(
            [ContentUpgradeStepState.Applied, ContentUpgradeStepState.Refused, ContentUpgradeStepState.Pending],
            States(report));
        Assert.Single(await harness.Store.ListUpgradesAsync());
        Assert.Null(await harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A PREVIEW writes nothing at all. It plans the first pending definition exactly, with its change lines,
    /// and lists the rest as pending, because a later plan reads an earlier one's published result.
    /// </summary>
    [Fact]
    public async Task APreviewPlansTheFirstPendingDefinitionAndWritesNothing()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Preview());

        Assert.Equal(ContentUpgradeOutcome.PreviewOnly, report.Outcome);
        Assert.True(report.Success);
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Equal(1, report.ActiveVersionAfter);
        Assert.Equal(
            [ContentUpgradeStepState.WouldPublish, ContentUpgradeStepState.Pending],
            States(report));
        Assert.Single(report.Steps[0].ChangeLines);
        Assert.Contains("new_row", report.Steps[0].ChangeLines[0], System.StringComparison.Ordinal);
        Assert.Empty(report.Steps[1].ChangeLines);
    }

    /// <summary>
    /// A previewed definition that an apply would PUBLISH says so in the ledger's own vocabulary, and the
    /// line an operator reads names the id and the number of changes the version would carry. The
    /// definitions after it are pending AND say why, which is that a later plan reads an earlier one's
    /// published result rather than that they were skipped.
    /// </summary>
    [Fact]
    public async Task APreviewSaysWhichDefinitionAnApplyWouldPublishAndWhyTheRestArePending()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Preview());

        Assert.Equal(ContentUpgradeDisposition.Applied, report.Steps[0].WouldRecord);
        Assert.Null(report.Steps[0].Disposition);
        Assert.Null(report.Steps[1].WouldRecord);
        Assert.Contains(
            report.Lines,
            line => line.Contains(
                "upgrade '" + UpgradeHarness.FirstId + "' would publish a new version with these 1 change(s).",
                System.StringComparison.Ordinal));
        Assert.Contains(
            report.Lines,
            line => line.Contains("upgrade '" + UpgradeHarness.SecondId + "' is pending.", System.StringComparison.Ordinal)
                && line.Contains("reads the published result", System.StringComparison.Ordinal));
    }

    /// <summary>
    /// The catalog this issue is about: seeded or repaired before the ledger existed, so every definition is
    /// already satisfied and an apply would publish NOTHING. The preview says that in words rather than
    /// reporting the same "planned" a publishing definition gets, and it still writes nothing.
    /// </summary>
    [Fact]
    public async Task APreviewOfAnAlreadySatisfiedDefinitionSaysTheApplyWouldPublishNothing()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedCurrentCatalogAsync();
        Assert.Empty(await harness.Store.ListUpgradesAsync());
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Preview());

        Assert.Equal(ContentUpgradeOutcome.PreviewOnly, report.Outcome);
        Assert.Equal(
            [ContentUpgradeStepState.WouldAdopt, ContentUpgradeStepState.Pending],
            States(report));
        Assert.Equal(ContentUpgradeDisposition.Adopted, report.Steps[0].WouldRecord);
        Assert.Empty(report.Steps[0].ChangeLines);
        Assert.Contains(
            report.Lines,
            line => line.Contains(
                "upgrade '" + UpgradeHarness.FirstId
                    + "' is already present and would only be recorded, no version published.",
                System.StringComparison.Ordinal));

        // The whole of the preview contract: no version, no audit row, no ledger row and no draft.
        Assert.Equal(before, await harness.FootprintAsync());
        Assert.Null(await harness.Store.GetOpenDraftAsync());
    }

    /// <summary>A store with no upgrade ledger cannot keep history, so the runner refuses to write anything.</summary>
    [Fact]
    public async Task AStoreWithNoUpgradeLedgerIsUnsupported()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var ledgerless = new LedgerlessStore(harness.Store);
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            ledgerless, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Unsupported, report.Outcome);
        Assert.False(report.Success);
        Assert.Equal(ContentUpgradeCodes.LedgerUnsupported, report.Diagnostics[0].Code);
        Assert.Equal(before, await harness.FootprintAsync());
    }

    /// <summary>
    /// The minimum builds RISE to this build's ordinals and never fall below the baseline's, so an older
    /// build applying an upgrade cannot lower the bar a client is admitted over.
    /// </summary>
    [Fact]
    public async Task TheMinimumBuildsRiseToThisBuildAndNeverFallBelowTheBaseline()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync(minimumServerBuild: 5, minimumClientBuild: 7);

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store,
            harness.Registry,
            harness.Set,
            UpgradeFixtures.Apply(serverBuild: 9, clientBuild: 3));

        Assert.Equal(ContentUpgradeOutcome.Applied, report.Outcome);
        ContentVersionRecord published = Assert.IsType<ContentVersionRecord>(
            await harness.Store.GetVersionAsync(report.ActiveVersionAfter));
        Assert.Equal(9, published.MinimumServerBuild);
        Assert.Equal(7, published.MinimumClientBuild);
    }

    /// <summary>
    /// A catalog with no published version is <see cref="ContentUpgradeOutcome.NoCatalog"/>, which IS a
    /// success: the runner never seeds and the caller's own import is the next step.
    /// </summary>
    [Fact]
    public async Task AnEmptyCatalogIsNoCatalogAndIsASuccess()
    {
        using var harness = new UpgradeHarness();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, harness.Set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.NoCatalog, report.Outcome);
        Assert.True(report.Success);
        Assert.True(report.CatalogAbsent);
        Assert.Equal(0, report.ExitCode);
        Assert.Empty(await harness.Store.ListVersionsAsync());
    }

    static int?[] Versions(ContentUpgradeReport report)
    {
        var versions = new int?[report.Steps.Count];
        for (int i = 0; i < versions.Length; i++)
        {
            versions[i] = report.Steps[i].PublishedVersion;
        }

        return versions;
    }

    static string[] Ids(ContentUpgradeReport report)
    {
        var ids = new string[report.Steps.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = report.Steps[i].Id;
        }

        return ids;
    }

    static ContentUpgradeStepState[] States(ContentUpgradeReport report)
    {
        var states = new ContentUpgradeStepState[report.Steps.Count];
        for (int i = 0; i < states.Length; i++)
        {
            states[i] = report.Steps[i].State;
        }

        return states;
    }
}
