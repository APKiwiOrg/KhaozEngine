using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>Malformed data from a planner is a reported refusal at both planner boundaries.</summary>
public sealed class ContentUpgradePlannerRefusalTests
{
    /// <summary>A refused replan leaves the draft for its operator, and a refused initial plan writes nothing.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedDataIsReportedDuringInitialPlanningAndDraftReplanning(bool replanDraft)
    {
        // M7 W3c fix round 1, D25: a pure planner can refuse captured malformed bytes from any engine family.
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        if (replanDraft)
        {
            ContentBundle baseline = await harness.Store.ExportBundleAsync(1);
            ContentUpgradePlan plan = harness.First.Plan(new ContentUpgradeContext(1, baseline, harness.Registry));
            await harness.Store.ApplyEditsAsync(
                plan.Edits, UpgradeFixtures.Actor, UpgradeFixtures.Operator,
                ContentUpgradeRunner.NoteFor(UpgradeHarness.FirstId));
        }

        bool refuse = true;
        var definition = new ContentUpgradeDefinition(
            harness.First.Id, harness.First.Order, harness.First.Description,
            context => refuse
                ? throw new PlannerDataException("the captured model is malformed.")
                : harness.First.Plan(context));
        var set = new ContentUpgradeSet(definition);
        CatalogFootprint before = await harness.FootprintAsync();

        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());

        Assert.Equal(
            replanDraft ? ContentUpgradeOutcome.OperatorDraftOpen : ContentUpgradeOutcome.Refused,
            report.Outcome);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == (
            replanDraft ? ContentUpgradeCodes.OperatorDraftOpen : ContentUpgradeCodes.PlanRefused));
        Assert.Equal(before, await harness.FootprintAsync());
        if (replanDraft)
        {
            Assert.Empty(report.Steps);
            ContentDraft draft = Assert.IsType<ContentDraft>(await harness.Store.GetOpenDraftAsync());
            Assert.Equal(1, draft.EditCount);
        }
        else
        {
            Assert.Equal(ContentUpgradeStepState.Refused, Assert.Single(report.Steps).State);
        }

        // The same catalog and draft are accepted when the planner's one refusal is removed.
        refuse = false;
        ContentUpgradeReport accepted = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Applied, accepted.Outcome);
        Assert.Equal(2, accepted.ActiveVersionAfter);
        Assert.Equal(ContentUpgradeStepState.Applied, Assert.Single(accepted.Steps).State);
        Assert.Single(await harness.Store.ListUpgradesAsync());
        Assert.Null(await harness.Store.GetOpenDraftAsync());
    }

    sealed class PlannerDataException(string message) : MalformedDataException(message);
}
