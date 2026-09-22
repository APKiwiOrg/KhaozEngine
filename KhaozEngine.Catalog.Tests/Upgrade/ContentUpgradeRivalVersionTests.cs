using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Upgrade;

/// <summary>
/// A rival that ADVANCED the catalog version while this run was working, and that holds a draft carrying
/// exactly what the shipped definition plans against that newer version.
/// <para>
/// The classification of a draft this run may not publish replans every still-pending definition, and those
/// replans export at the version the run stands on. A run reading the cached version replans against a
/// baseline nobody is on any more, so the rival's live draft matches no plan, holds some of this run's own
/// planned work and reads as merged, which stops a boot with an operator draft over a draft holding nothing
/// an operator wrote.
/// </para>
/// <para>
/// The definition here plans DIFFERENTLY on the two baselines, which is what makes the stale replan
/// observable at all. An additive plan whose edits carry committed ids is the same change set on either
/// version and hides the defect entirely.
/// </para>
/// </summary>
public sealed class ContentUpgradeRivalVersionTests
{
    /// <summary>The deadline a run is given, well under the stand-off budget, so a stall fails fast.</summary>
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>The one definition this build ships, whose plan grows once the marker row is published.</summary>
    const string TwoStepId = "adds-other-in-two-steps";

    /// <summary>The key of the row whose presence makes the definition plan its second addition.</summary>
    const string MarkerKey = "marker";

    /// <summary>
    /// The run waits the rival out and ends through the ledger, rather than reporting an operator draft over
    /// a draft that is a rival runner's live plan.
    /// </summary>
    [Fact]
    public async Task ARivalThatAdvancedTheVersionIsWaitedOutRatherThanReportedAsAnOperatorDraft()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        ContentBundle target = TargetFor(harness.Registry);
        ContentUpgradeDefinition definition = TwoStep(target);
        var rival = new RivalAdvancesTheVersionStore(harness.Store, harness.Registry, definition);

        using var deadline = new CancellationTokenSource(Deadline);
        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            rival,
            harness.Registry,
            new ContentUpgradeSet(definition),
            UpgradeFixtures.Apply(),
            deadline.Token);

        Assert.True(rival.Raced, "the rival never took the version, so the interleaving was not exercised.");
        string lines = string.Join(" | ", report.Lines);
        Assert.NotEqual(ContentUpgradeOutcome.OperatorDraftOpen, report.Outcome);
        Assert.True(report.Success, lines);

        // The rival's work stands, once, and this run neither republished it nor left a draft behind.
        ContentUpgradeRecord record = Assert.Single(await harness.Store.ListUpgradesAsync());
        Assert.Equal(TwoStepId, record.Id);
        Assert.Null(await harness.Store.GetOpenDraftAsync());
        ContentRowPage others = await harness.Store.ListRowsAsync(
            UpgradeFixtures.Other, 0, null, true, 0, 50);
        Assert.Equal(["new_row", "second_new_row"], Keys(others));
    }

    /// <summary>
    /// The `ExpectedVersion` re-check inside the draft classification stops the run mid-flight, and the
    /// reading it hands back is a live publisher's, which every other caller waits out. A run already stopped
    /// on `BaselineMoved` may not wait and may not have that outcome overwritten: the wait would delay a run
    /// that is finished, and a spent attempt budget would report `Failed` over the one thing the expected
    /// version exists to say.
    /// <para>
    /// The absence of the wait is asserted through the reads the stand-off itself makes rather than through
    /// the clock, because the first backoff is fifty milliseconds and no clock assertion that small survives
    /// a loaded machine. Every stand-off delay is preceded by the progress reading, and that reading is one
    /// of the three store calls counted here, so exactly one active-version read after the move means the
    /// run neither waited nor came back for another attempt.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ABaselineThatMovedDuringTheDraftClassificationStopsAtOnceAndReportsOnce()
    {
        using var harness = new UpgradeHarness();
        await harness.SeedOlderCatalogAsync();
        var moving = new VersionMovesDuringClassificationStore(harness.Store);

        using var deadline = new CancellationTokenSource(Deadline);
        ContentUpgradeReport report = await ContentUpgradeRunner.RunAsync(
            moving, harness.Registry, harness.Set, UpgradeFixtures.Apply(expectedVersion: 1), deadline.Token);

        Assert.True(moving.Moved, "the version never moved, so the interleaving was not exercised.");
        Assert.Equal(ContentUpgradeOutcome.BaselineMoved, report.Outcome);
        Assert.Single(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.BaselineMoved, StringComparison.Ordinal));
        Assert.DoesNotContain(
            report.Diagnostics,
            diagnostic => string.Equals(
                diagnostic.Code, ContentUpgradeCodes.PublishFailed, StringComparison.Ordinal));

        // The read that NOTICED the move, and no other. A stand-off reads the active version to compute its
        // progress signature and then again on the attempt it comes back for.
        Assert.Equal(1, moving.ActiveReadsAfterTheMove);

        // Nothing was applied and nothing was touched: both definitions stand pending and the draft the
        // classification was about is exactly as it was.
        Assert.Equal(2, report.Steps.Count);
        Assert.All(report.Steps, step => Assert.Equal(ContentUpgradeStepState.Pending, step.State));
        Assert.Single(await harness.Store.ListVersionsAsync());
        Assert.Empty(await harness.Store.ListUpgradesAsync());
        Assert.Equal(1, (await harness.Store.GetOpenDraftAsync())!.EditCount);
    }

    /// <summary>
    /// The committed bundle this build ships, which carries the marker row the rival publishes as well as
    /// both rows the definition adds.
    /// </summary>
    /// <param name="registry">The registry the bundle declares its types from.</param>
    static ContentBundle TargetFor(ContentTypeRegistry registry) => UpgradeFixtures.Target(
        registry,
        UpgradeFixtures.Row(UpgradeFixtures.Thing, 1, "old_row", 11),
        UpgradeFixtures.Row(UpgradeFixtures.Thing, 2, MarkerKey, 44),
        UpgradeFixtures.Row(UpgradeFixtures.Other, 1, "new_row", 22),
        UpgradeFixtures.Row(UpgradeFixtures.Other, 2, "second_new_row", 33));

    /// <summary>
    /// The definition whose plan DEPENDS on the baseline: one addition always, and a second one only once the
    /// marker row is published. It is the ordinary shape of a step that only applies after earlier content
    /// landed, and it is what a stale replan gets wrong.
    /// </summary>
    /// <param name="target">The committed target bundle.</param>
    static ContentUpgradeDefinition TwoStep(ContentBundle target) => new(
        TwoStepId,
        1,
        "adds the second type's rows, the second one only once the marker is published",
        context =>
        {
            var builder = new ContentUpgradePlanBuilder(context, target)
                .AddRow(UpgradeFixtures.Other, new ContentKey("new_row"));
            if (HoldsMarker(context.Baseline))
            {
                builder.AddRow(UpgradeFixtures.Other, new ContentKey("second_new_row"));
            }

            return builder.Build();
        });

    /// <summary>Whether an exported baseline already carries the marker row.</summary>
    /// <param name="baseline">The baseline the planner was handed.</param>
    static bool HoldsMarker(ContentBundle baseline)
    {
        IReadOnlyList<ContentBundleRow> rows = baseline.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Type == UpgradeFixtures.Thing
                && string.Equals(rows[i].Key.ToString(), MarkerKey, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    static string[] Keys(ContentRowPage page)
    {
        var keys = new string[page.Rows.Count];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = page.Rows[i].Key.ToString();
        }

        return keys;
    }
}

/// <summary>
/// A store where a draft the runner must classify appears at the publish pre-flight and the ACTIVE VERSION
/// moves in the same moment. The classification replans against the current version, so it is the first
/// thing to notice the move, and it notices it with a draft in hand that every other caller would wait out.
/// <para>
/// The version is bumped by the double rather than really published, because what is under test is the
/// runner's own bookkeeping after its expected version stopped it, not the catalog's state.
/// </para>
/// </summary>
/// <param name="inner">The store behind the double, which keeps the upgrade ledger.</param>
internal sealed class VersionMovesDuringClassificationStore(InMemoryContentAuthoringStore inner)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    /// <summary>An edit no definition plans, so the draft is one the runner has to classify.</summary>
    static readonly ContentEdit Foreign = ContentEdit.Add(
        UpgradeFixtures.Thing, new ContentKey("leftover"), PublishFixtures.Fields(77));

    int _looks;

    /// <summary>Whether the version really moved, which a test asserts the interleaving happened by.</summary>
    public bool Moved { get; private set; }

    /// <summary>How many times the active version was read after it moved.</summary>
    public int ActiveReadsAfterTheMove { get; private set; }

    /// <inheritdoc />
    public override async Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default)
    {
        int active = await base.GetActiveVersionAsync(cancellationToken);
        if (!Moved)
        {
            return active;
        }

        ActiveReadsAfterTheMove++;
        return active + 1;
    }

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        _looks++;
        if (_looks == 2)
        {
            await inner.ApplyEditsAsync(
                [Foreign],
                UpgradeFixtures.Actor,
                "oid:injected",
                ContentUpgradeRunner.NoteFor(UpgradeHarness.SecondId),
                cancellationToken);
            Moved = true;
        }

        return await base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);
}

/// <summary>
/// A store where a RIVAL runner takes the version and then holds a draft carrying exactly what the shipped
/// definition plans against the version it just published.
/// <para>
/// The rival moves on the run's SECOND look at the open draft, which is the publish pre-flight, so the run
/// planned against the older baseline and meets the newer catalog with a stale plan in hand. It publishes
/// its own draft on a later look, which is what lets the run resolve through the ledger and finish.
/// </para>
/// </summary>
/// <param name="inner">The store behind the double, which keeps the upgrade ledger.</param>
/// <param name="registry">The registry the rival plans through.</param>
/// <param name="definition">The definition the rival is running, which is the one this build ships.</param>
internal sealed class RivalAdvancesTheVersionStore(
    InMemoryContentAuthoringStore inner,
    ContentTypeRegistry registry,
    ContentUpgradeDefinition definition)
    : ForwardingContentAuthoringStore(inner), IContentUpgradeLedger
{
    /// <summary>The look the rival publishes the draft it is holding on, which lets the run finish.</summary>
    const int PublishAtLook = 5;

    int _looks;

    /// <summary>Whether the rival took the version, which a test asserts the interleaving happened by.</summary>
    public bool Raced { get; private set; }

    /// <inheritdoc />
    public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
    {
        _looks++;
        if (_looks == 2)
        {
            await TakeTheVersionAsync(cancellationToken);
        }
        else if (_looks == PublishAtLook && Raced)
        {
            await PublishItsOwnDraftAsync(cancellationToken);
        }

        return await base.GetOpenDraftAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ContentUpgradeRecord>> ListUpgradesAsync(
        CancellationToken cancellationToken = default)
        => inner.ListUpgradesAsync(cancellationToken);

    /// <inheritdoc />
    public Task RecordUpgradeAsync(
        ContentUpgradeStamp stamp,
        ContentUpgradeDisposition disposition,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default)
        => inner.RecordUpgradeAsync(stamp, disposition, actor, operatorId, cancellationToken);

    /// <summary>
    /// The rival publishes the marker row as its own version and then opens a draft holding exactly what the
    /// definition plans against THAT version, under the runner identity two replicas of one deploy share.
    /// </summary>
    /// <param name="cancellationToken">Cancels the writes.</param>
    async Task TakeTheVersionAsync(CancellationToken cancellationToken)
    {
        int baseVersion = await inner.GetActiveVersionAsync(cancellationToken);
        await inner.ApplyEditsAsync(
            [ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("marker"), PublishFixtures.Fields(44))],
            "a-rival-runner",
            "oid:rival",
            "the rival takes the version",
            cancellationToken);
        await inner.PublishAsync(
            new ContentPublishRequest(
                "a-rival-runner", "oid:rival", "the rival takes the version", baseVersion, 0, 0),
            cancellationToken);

        await inner.ApplyEditsAsync(
            await PlannedEditsAsync(cancellationToken),
            UpgradeFixtures.Actor,
            "oid:rival",
            ContentUpgradeRunner.NoteFor(definition.Id),
            cancellationToken);
        Raced = true;
    }

    /// <summary>The rival's own publish of the draft it holds, stamped so its ledger row lands with it.</summary>
    /// <param name="cancellationToken">Cancels the publish.</param>
    async Task PublishItsOwnDraftAsync(CancellationToken cancellationToken)
    {
        if (await inner.GetOpenDraftAsync(cancellationToken) is null)
        {
            return;
        }

        int baseVersion = await inner.GetActiveVersionAsync(cancellationToken);
        await inner.PublishAsync(
            new ContentPublishRequest(
                UpgradeFixtures.Actor,
                "oid:rival",
                ContentUpgradeRunner.NoteFor(definition.Id),
                baseVersion,
                0,
                0)
            {
                Upgrade = definition.Stamp,
            },
            cancellationToken);
    }

    /// <summary>What the definition plans against the version the rival has just published.</summary>
    /// <param name="cancellationToken">Cancels the export.</param>
    async Task<IReadOnlyList<ContentEdit>> PlannedEditsAsync(CancellationToken cancellationToken)
    {
        int active = await inner.GetActiveVersionAsync(cancellationToken);
        ContentBundle baseline = await inner.ExportBundleAsync(active, cancellationToken);
        return definition.Plan(new ContentUpgradeContext(active, baseline, registry)).Edits;
    }
}
