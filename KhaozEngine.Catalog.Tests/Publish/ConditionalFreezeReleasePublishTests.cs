using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.ConditionalFreeze;
using KhaozEngine.Tests.Catalog.Upgrade;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Console publishers racing across a version boundary, each interleaving forced once with gates. The
/// defect under test is a publisher that lost its base version and then takes away the freeze marker a newer
/// publisher set on the NEXT draft, which turns the newer publisher's complete text commit into a
/// <c>text-state-mismatch</c> refusal.
/// <para>
/// <b>Every winner publishes over a decorator that declares the whole text companion</b>, and the test asserts
/// its commit really went through <c>CommitTextPublishAsync</c>, because the row-only commit never reads the
/// marker and a harness on that route would pass on the defect. Each case runs on the in-memory store and on
/// two SQLite replicas of one file.
/// </para>
/// </summary>
/// <param name="output">Where each participant's outcome is written before anything is asserted.</param>
public sealed class ConditionalFreezeReleasePublishTests(ITestOutputHelper output)
{
    /// <summary>
    /// T2. P1 freezes at 1 and parks at its commit. P2 commits version 2, the console opens D2, and P3 freezes
    /// D2 at 2 and parks at its commit. P1 resumes, is refused and runs its cleanup. Its cleanup must leave
    /// P3's marker standing, so P3 commits version 3.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is two SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T2_OlderPublishReleasePreservesNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));

        FreezeRaceStore p1 = await fixture.TextParticipantAsync();
        FreezeRaceStore p2 = await fixture.TextParticipantAsync();
        FreezeRaceStore winner = await fixture.TextParticipantAsync();
        OnceGate p1Commit = fixture.Gate();
        OnceGate winnerCommit = fixture.Gate();
        p1.ArmTextCommitEntry(p1Commit, 1);
        winner.ArmTextCommitEntry(winnerCommit, 2);

        Task<ContentPublishResult> older = fixture.Start(() => p1.PublishAsync(PublishFixtures.Request(1)));
        await FreezeRaceFixture.ReachAsync(p1Commit, older, "P1's text commit at base 1");

        (ContentPublishResult? second, Exception? secondFailure) = await FreezeRaceFixture.OutcomeAsync(
            fixture.Start(() => p2.PublishAsync(PublishFixtures.Request(1))));
        output.WriteLine("P2 " + FreezeRaceFixture.Describe(secondFailure));
        Assert.Null(secondFailure);
        Assert.Equal(2, second!.VersionNumber);

        await fixture.EditAsync(fixture.OldRow(33));
        Task<ContentPublishResult> newer = fixture.Start(() => winner.PublishAsync(PublishFixtures.Request(2)));
        await FreezeRaceFixture.ReachAsync(winnerCommit, newer, "P3's text commit at base 2");

        p1Commit.Resume();
        (_, Exception? olderFailure) = await FreezeRaceFixture.OutcomeAsync(older);
        int? markerAfterOlderExit = (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion;
        output.WriteLine("P1 " + FreezeRaceFixture.Describe(olderFailure));
        output.WriteLine("D2 marker after P1's exit: " + FreezeRaceFixture.Marker(markerAfterOlderExit));

        winnerCommit.Resume();
        (ContentPublishResult? winnerResult, Exception? winnerFailure) = await FreezeRaceFixture.OutcomeAsync(newer);
        output.WriteLine("P3 " + FreezeRaceFixture.Describe(winnerFailure));

        Assert.Equal(1, winner.TextCommitCalls);
        Assert.Equal(0, winner.RowCommitCalls);
        Assert.Null(winnerFailure);
        Assert.Equal(2, markerAfterOlderExit);
        Assert.Equal(3, winnerResult!.VersionNumber);
        Assert.Null(await inner.GetOpenDraftAsync());
        Assert.Equal(
            ContentAuthoringException.BaseVersionMovedReason,
            Assert.IsType<ContentAuthoringException>(olderFailure).Reason);
    }

    /// <summary>
    /// T2p. P1 publishes over a row-only decorator, so its publish takes the row route. It has read its baseline
    /// at 1 and parks at the entry of its freeze step. P2 commits version 2, the console opens a row-only D2,
    /// and P3 freezes D2 at 2 and parks at its commit. P1 resumes and finishes. Its freeze step must not take
    /// P3's marker, so P3 commits version 3.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is two SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T2p_RowPublishFreezePreservesNewerTextWinner(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));

        RowFreezeRaceStore p1 = await fixture.RowParticipantAsync(forwardPublish: false);
        FreezeRaceStore p2 = await fixture.TextParticipantAsync();
        FreezeRaceStore winner = await fixture.TextParticipantAsync();
        OnceGate p1Freeze = fixture.Gate();
        OnceGate winnerCommit = fixture.Gate();
        p1.ArmPrepareFreezeEntry(p1Freeze, 1);
        winner.ArmTextCommitEntry(winnerCommit, 2);

        Task<ContentPublishResult> older = fixture.Start(() => p1.PublishAsync(PublishFixtures.Request(1)));
        await FreezeRaceFixture.ReachAsync(p1Freeze, older, "P1's row route freeze step at base 1");

        (ContentPublishResult? second, Exception? secondFailure) = await FreezeRaceFixture.OutcomeAsync(
            fixture.Start(() => p2.PublishAsync(PublishFixtures.Request(1))));
        output.WriteLine("P2 " + FreezeRaceFixture.Describe(secondFailure));
        Assert.Null(secondFailure);
        Assert.Equal(2, second!.VersionNumber);

        await fixture.EditAsync(fixture.OldRow(33));
        Task<ContentPublishResult> newer = fixture.Start(() => winner.PublishAsync(PublishFixtures.Request(2)));
        await FreezeRaceFixture.ReachAsync(winnerCommit, newer, "P3's text commit at base 2");

        p1Freeze.Resume();
        (_, Exception? olderFailure) = await FreezeRaceFixture.OutcomeAsync(older);
        int? markerAfterOlderExit = (await inner.GetOpenDraftAsync())?.FrozenForBaseVersion;
        output.WriteLine("P1 " + FreezeRaceFixture.Describe(olderFailure));
        output.WriteLine("D2 marker after P1's exit: " + FreezeRaceFixture.Marker(markerAfterOlderExit));

        winnerCommit.Resume();
        (ContentPublishResult? winnerResult, Exception? winnerFailure) = await FreezeRaceFixture.OutcomeAsync(newer);
        output.WriteLine("P3 " + FreezeRaceFixture.Describe(winnerFailure));

        Assert.Equal(1, winner.TextCommitCalls);
        Assert.Equal(0, winner.RowCommitCalls);
        Assert.Null(winnerFailure);
        Assert.Equal(2, markerAfterOlderExit);
        Assert.Equal(3, winnerResult!.VersionNumber);
        Assert.Null(await inner.GetOpenDraftAsync());
        Assert.Equal(
            ContentAuthoringException.BaseVersionMovedReason,
            Assert.IsType<ContentAuthoringException>(olderFailure).Reason);
    }

    /// <summary>
    /// T2r, the accepted same-base residue. P1 and P2 freeze one draft at the same base. P1 is cancelled inside
    /// its pack write while P2 parks at its commit, and P1's cleanup releases the shared marker. An edit lands
    /// in the gap. P2's commit must refuse rather than publish a draft it did not freeze, nothing is published,
    /// and the draft keeps every edit, the gap edit included. It is green before and after the repair.
    /// </summary>
    /// <param name="sqlite">Whether the catalog is two SQLite replicas rather than the in-memory store.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task T2r_SameBaseCancellationStillRefusesChangedTextDraft(bool sqlite)
    {
        await using var fixture = new FreezeRaceFixture(sqlite);
        await fixture.SeedAsync();
        IContentAuthoringStore inner = fixture.Inner;
        await fixture.EditAsync(fixture.OldRow(22));

        FreezeRaceStore p1 = await fixture.TextParticipantAsync();
        FreezeRaceStore sameBaseWinner = await fixture.TextParticipantAsync();
        FreezeRacePackStore p1Pack = Assert.IsType<FreezeRacePackStore>(p1.Packs);
        OnceGate p1Put = fixture.Gate();
        OnceGate winnerCommit = fixture.Gate();
        p1Pack.ArmPut(p1Put);
        sameBaseWinner.ArmTextCommitEntry(winnerCommit, 1);

        using var cancelP1 = new CancellationTokenSource();
        Task<ContentPublishResult> cancelled = fixture.Start(
            () => p1.PublishAsync(PublishFixtures.Request(1), cancelP1.Token));
        await FreezeRaceFixture.ReachAsync(p1Put, cancelled, "P1's pack write at base 1");

        Task<ContentPublishResult> sameBase = fixture.Start(
            () => sameBaseWinner.PublishAsync(PublishFixtures.Request(1)));
        await FreezeRaceFixture.ReachAsync(winnerCommit, sameBase, "P2's text commit at base 1");

        await cancelP1.CancelAsync();
        (_, Exception? cancelledFailure) = await FreezeRaceFixture.OutcomeAsync(cancelled);
        output.WriteLine("P1 " + FreezeRaceFixture.Describe(cancelledFailure));
        Assert.IsAssignableFrom<OperationCanceledException>(cancelledFailure);
        Assert.Equal(1, p1Pack.CancelledPuts);
        Assert.Single(p1.ReleaseTokens);

        await fixture.EditAsync(
            ContentEdit.Add(UpgradeFixtures.Thing, new ContentKey("gap_row"), PublishFixtures.Fields(77)));

        winnerCommit.Resume();
        (_, Exception? failure) = await FreezeRaceFixture.OutcomeAsync(sameBase);
        output.WriteLine("P2 " + FreezeRaceFixture.Describe(failure));
        ContentAuthoringException sameBaseFailure = Assert.IsType<ContentAuthoringException>(failure);
        ContentDraft held = Assert.IsType<ContentDraft>(await inner.GetOpenDraftAsync());

        Assert.Equal(ContentAuthoringException.TextStateMismatchReason, sameBaseFailure.Reason);
        Assert.Equal(1, await inner.GetActiveVersionAsync());
        Assert.Single(await inner.ListVersionsAsync());
        Assert.Equal(new[] { "old_row", "gap_row" }, held.Changes.Edits.Select(e => e.Key.ToString()));
        Assert.Equal(22, held.Changes.Edits[0].Fields[0].Value.Number);
        Assert.Equal(77, held.Changes.Edits[1].Fields[0].Value.Number);
        Assert.False(held.IsFrozen);
        Assert.Equal(1, sameBaseWinner.TextCommitCalls);
    }
}
