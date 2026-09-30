using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>The public rollback plan reports the rule it found, including an absent rule.</summary>
public sealed class ContentRollbackBlockerTests
{
    [Theory]
    [InlineData(false, null, 0, 0)]
    [InlineData(true, RemapRuleKind.Retired, 7, 2)]
    public void Prepare_CarriesOnlyTheMatchingRetiringRule(
        bool includeRetiringRule,
        RemapRuleKind? expectedKind,
        int expectedSequence,
        int expectedIntroducedIn)
    {
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        var key = new ContentKey("sword");
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentFieldValue[] fields = [ContentFieldValue.OfNumber(ContentFieldKind.Int, 10)];
        var live = new ContentRow(type, 1, key, 0, false, fields);
        var retired = new ContentRow(type, 1, key, 0, true, fields);
        RemapRule[] rules = includeRetiringRule
            ? [new RemapRule(7, 2, type, RemapRuleKind.Retired, 1, 0, [RemapRule.RetirePolicyPlaceholder])]
            : [];

        ContentRollbackPlan plan = ContentRollback.Prepare(
            1, [new ContentRowRevision(live, 1, 2, null)],
            2, [new ContentRowRevision(retired, 2, null, null)],
            rules, registry);

        Assert.True(plan.IsBlocked);
        Assert.Empty(plan.Edits);
        ContentRollbackBlocker blocker = Assert.Single(plan.Blockers);
        Assert.Equal(expectedKind, blocker.RuleKind);
        // Keep the original five-field positional constructor and deconstruction usable.
        var (actualType, id, actualKey, sequence, introducedIn) = blocker;
        Assert.Equal(new ContentRollbackBlocker(type, 1, key, expectedSequence, expectedIntroducedIn),
            new ContentRollbackBlocker(actualType, id, actualKey, sequence, introducedIn));
        ContentFinding finding = Assert.Single(plan.Findings);
        Assert.Equal("KEC0039", finding.Code);
        Assert.Equal(type, finding.Type);
        Assert.Equal(1, finding.Id);
        Assert.Contains("new key", finding.Message);
    }
}
