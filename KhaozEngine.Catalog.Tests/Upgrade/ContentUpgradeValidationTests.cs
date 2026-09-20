using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// The value types a game builds its upgrades out of: the definition, the set, the three plan shapes, the
/// generic checks, and the report's rendering and exit code. None of these touches a store.
/// </summary>
public sealed class ContentUpgradeValidationTests
{
    static ContentUpgradePlan Nothing(ContentUpgradeContext context) => ContentUpgradePlan.Refused("unused");

    /// <summary>A definition validates its id and its order at construction, through the ledger's own stamp.</summary>
    [Theory]
    [InlineData("", 1)]
    [InlineData("ok", 0)]
    [InlineData("ok", -1)]
    public void ADefinitionRefusesAnEmptyIdOrANonPositiveOrder(string id, int order)
        => Assert.ThrowsAny<ArgumentException>(
            () => new ContentUpgradeDefinition(id, order, "description", Nothing));

    /// <summary>An id longer than the ledger column takes is refused where it is written, not at the insert.</summary>
    [Fact]
    public void ADefinitionRefusesAnIdLongerThanTheLedgerColumn()
        => Assert.Throws<ArgumentException>(
            () => new ContentUpgradeDefinition(
                new string('x', ContentUpgradeStamp.MaxIdLength + 1), 1, "description", Nothing));

    /// <summary>A set ORDERS its definitions ascending, whatever order the caller handed them in.</summary>
    [Fact]
    public void ASetOrdersItsDefinitionsAscendingByOrder()
    {
        var set = new ContentUpgradeSet(
            new ContentUpgradeDefinition("third", 30, "c", Nothing),
            new ContentUpgradeDefinition("first", 10, "a", Nothing),
            new ContentUpgradeDefinition("second", 20, "b", Nothing));

        Assert.Equal(["first", "second", "third"], Ids(set));
        Assert.Equal(3, set.Count);
        Assert.True(set.Contains("second"));
        Assert.True(set.TryGet("third", out ContentUpgradeDefinition? third));
        Assert.Equal(30, third.Order);
    }

    /// <summary>
    /// Two definitions under one id would have the ledger record one of them and skip the other forever, so
    /// the set refuses at construction rather than at the run that finally needed both.
    /// </summary>
    [Fact]
    public void ASetRefusesTwoDefinitionsUnderOneId()
    {
        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ContentUpgradeSet(
            new ContentUpgradeDefinition("same", 1, "a", Nothing),
            new ContentUpgradeDefinition("same", 2, "b", Nothing)));

        Assert.Contains("same", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Two definitions under one order would run in whatever order the caller's list happened to be.</summary>
    [Fact]
    public void ASetRefusesTwoDefinitionsUnderOneOrder()
        => Assert.Throws<ArgumentException>(() => new ContentUpgradeSet(
            new ContentUpgradeDefinition("one", 7, "a", Nothing),
            new ContentUpgradeDefinition("two", 7, "b", Nothing)));

    /// <summary>A plan that changes nothing says so, rather than carrying an empty change list.</summary>
    [Fact]
    public void AChangePlanWithNoEditsIsRefusedAtConstruction()
        => Assert.Throws<ArgumentException>(() => ContentUpgradePlan.Changes([], []));

    /// <summary>
    /// An identity present under the same id AND the same key is present. One half without the other, or
    /// each half under a different row, is a CONFLICT rather than a partial success.
    /// </summary>
    [Fact]
    public void AnIdentityIsPresentOnlyUnderBothTheSameIdAndTheSameKey()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentBundleRow wanted = UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "sword", 5);

        ContentBundle absent = UpgradeFixtures.Target(
            registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "shield", 1));
        Assert.Equal(
            ContentUpgradeIdentityState.Absent,
            ContentUpgradeChecks.Identity(absent, wanted, out _));

        ContentBundle present = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "shield", 1),
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "sword", 99));
        Assert.Equal(
            ContentUpgradeIdentityState.Present,
            ContentUpgradeChecks.Identity(present, wanted, out _));

        // The id is taken by another key, which is operator content the upgrade would otherwise overwrite.
        ContentBundle clashing = UpgradeFixtures.Target(
            registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "an_operators_row", 4));
        Assert.Equal(
            ContentUpgradeIdentityState.Conflict,
            ContentUpgradeChecks.Identity(clashing, wanted, out string? conflict));
        Assert.Contains("an_operators_row", conflict!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plain allocation must issue EXACTLY the committed ids: contiguous, starting one past the type's
    /// current highest row id. Anything else files the new rows under numbers the committed bundle names
    /// other rows by.
    /// </summary>
    [Fact]
    public void AllocationMustIssueExactlyTheCommittedIdsFromTheCurrentHighWater()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentBundle baseline = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "one", 1),
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "two", 2));

        Assert.Null(ContentUpgradeChecks.AllocationIssuesExactly(
            baseline,
            [
                UpgradeFixtures.Row(UpgradeFixtures.Thing, 3, "three", 3),
                UpgradeFixtures.Row(UpgradeFixtures.Thing, 4, "four", 4),
            ]));

        // A gap: id 3 would be issued to the row committed as id 5.
        Assert.NotNull(ContentUpgradeChecks.AllocationIssuesExactly(
            baseline, [UpgradeFixtures.Row(UpgradeFixtures.Thing, 5, "five", 5)]));

        // An id already below the high-water mark.
        Assert.NotNull(ContentUpgradeChecks.AllocationIssuesExactly(
            baseline, [UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "two", 2)]));
    }

    /// <summary>
    /// A baseline type whose schema no longer matches the target is refused with the type NAMED. The rows
    /// already in the catalog cannot be carried forward, and nothing here can know what a new field holds.
    /// </summary>
    [Fact]
    public void AnIncompatibleBaselineTypeIsRefusedWithTheTypeNamed()
    {
        ContentTypeRegistry current = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentTypeRegistry moved = PublishFixtures.Registry(PublishFixtures.SecretThing);
        ContentBundle target = UpgradeFixtures.Target(current);
        ContentBundle baseline = UpgradeFixtures.Target(moved);

        string? refusal = ContentUpgradeChecks.BaselineIsSchemaCompatible(baseline, target);

        Assert.NotNull(refusal);
        Assert.Contains(PublishFixtures.ThingTypeKey, refusal, StringComparison.Ordinal);

        // The same disagreement from the registry's side, which is the committed bundle being wrong.
        Assert.NotNull(ContentUpgradeChecks.TargetMatchesRegistry(baseline, current));
        Assert.Null(ContentUpgradeChecks.TargetMatchesRegistry(target, current));
    }

    /// <summary>
    /// A builder whose staged work is PART done refuses rather than completing the rest, because completing
    /// it would guess which of the existing rows an operator owns.
    /// </summary>
    [Fact]
    public void ABuilderRefusesAPartiallyAppliedUpgrade()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentBundle target = UpgradeFixtures.Target(
            registry,
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "one", 1),
            UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, "two", 2));
        ContentBundle baseline = UpgradeFixtures.Target(
            registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "one", 1));
        var context = new ContentUpgradeContext(4, baseline, registry);

        ContentUpgradePlan plan = new ContentUpgradePlanBuilder(context, target)
            .AddRows([
                UpgradeFixtures.Identity(UpgradeFixtures.Thing, "one"),
                UpgradeFixtures.Identity(UpgradeFixtures.Thing, "two"),
            ])
            .Build();

        Assert.Equal(ContentUpgradePlanKind.Refused, plan.Kind);
        Assert.Contains("partial", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A value patch acts on the OLD SHIPPED DEFAULT only. A field an operator has tuned is left exactly as
    /// it is, and a field already carrying the new value is satisfied.
    /// </summary>
    [Theory]
    [InlineData(11, true)]
    [InlineData(22, false)]
    [InlineData(99, false)]
    public void APatchActsOnlyOnTheOldShippedDefault(int current, bool patched)
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        ContentBundle target = UpgradeFixtures.Target(
            registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "one", 22));
        ContentBundle baseline = UpgradeFixtures.Target(
            registry, UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "one", current));
        var context = new ContentUpgradeContext(4, baseline, registry);

        ContentUpgradePlan plan = new ContentUpgradePlanBuilder(context, target)
            .PatchField(
                UpgradeFixtures.Thing,
                new ContentKey("one"),
                PublishFixtures.ValueField,
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 11),
                ContentFieldValue.OfNumber(ContentFieldKind.Int, 22))
            .Build();

        Assert.Equal(
            patched ? ContentUpgradePlanKind.Changes : ContentUpgradePlanKind.AlreadySatisfied,
            plan.Kind);
    }

    /// <summary>
    /// A planner that throws its refusal the way a hand-written check does still reaches the operator as a
    /// LINE. That is the shape the first explicit upgrade command had, and a stack trace is not an
    /// instruction.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task APlannerThatThrowsItsRefusalIsReportedAsARefusal()
    {
        using var harness = new UpgradeHarness();
        var set = new ContentUpgradeSet(new ContentUpgradeDefinition(
            "throws",
            1,
            "throws its refusal",
            _ => throw new InvalidDataException("the catalog contains part of this upgrade.")));

        // The catalog is empty here, so the run stops at NoCatalog before the planner is ever reached, which
        // is itself the point: the gates run before anything is planned.
        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());
        Assert.Equal(ContentUpgradeOutcome.NoCatalog, report.Outcome);

        await harness.SeedOlderCatalogAsync();
        report = await ContentUpgradeRunner.RunAsync(
            harness.Store, harness.Registry, set, UpgradeFixtures.Apply());

        Assert.Equal(ContentUpgradeOutcome.Refused, report.Outcome);
        Assert.Contains("part of this upgrade", report.Steps[0].Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every line the report writes carries the content boot's line prefix, so an operator greps one token
    /// across a boot and an upgrade, and the exit code is the content refusal code on a refusal and 0
    /// otherwise.
    /// </summary>
    [Fact]
    public void EveryReportLineCarriesTheContentPrefixAndTheExitCodeFollowsSuccess()
    {
        var definition = new ContentUpgradeDefinition("one", 1, "adds a row", Nothing);
        var applied = new ContentUpgradeReport(
            ContentUpgradeOutcome.Applied,
            4,
            5,
            [ContentUpgradeStepResult.Applied(definition, 5, ["add thing 3 'sword'"])],
            [new ContentUpgradeDiagnostic(ContentUpgradeCodes.PinHeld, "repin to version 5.")]);

        Assert.All(applied.Lines, line => Assert.StartsWith(ContentBoot.LinePrefix, line, StringComparison.Ordinal));
        Assert.Equal(4, applied.Lines.Count);
        Assert.Equal(0, applied.ExitCode);
        Assert.Contains("one", applied.Lines[1], StringComparison.Ordinal);
        Assert.Contains("add thing 3 'sword'", applied.Lines[2], StringComparison.Ordinal);

        var output = new StringWriter();
        var error = new StringWriter();
        applied.WriteTo(output, error);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains(ContentBoot.LinePrefix, output.ToString(), StringComparison.Ordinal);

        var refused = new ContentUpgradeReport(
            ContentUpgradeOutcome.Refused,
            4,
            4,
            [ContentUpgradeStepResult.Refused(definition, "half applied.")],
            [new ContentUpgradeDiagnostic(ContentUpgradeCodes.PlanRefused, "refused.")]);

        Assert.False(refused.Success);
        Assert.Equal(ContentBootResult.ContentFailureExitCode, refused.ExitCode);
        var refusedOutput = new StringWriter();
        var refusedError = new StringWriter();
        refused.WriteTo(refusedOutput, refusedError);
        Assert.Equal(string.Empty, refusedOutput.ToString());
        Assert.All(
            refused.Lines,
            line => Assert.Contains(line, refusedError.ToString(), StringComparison.Ordinal));
    }

    static string[] Ids(ContentUpgradeSet set)
    {
        IReadOnlyList<ContentUpgradeDefinition> definitions = set.Definitions;
        var ids = new string[definitions.Count];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = definitions[i].Id;
        }

        return ids;
    }
}
