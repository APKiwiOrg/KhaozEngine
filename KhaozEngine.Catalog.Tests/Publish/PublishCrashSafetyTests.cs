using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Spec 15.6, the publish crash-safety cases: ONE per <see cref="ContentPublishStep"/>, against the in-memory
/// reference store AND the SQLite provider, with the kill thrown from the <c>OnStep</c> hook.
/// <para>
/// Each case asserts the same three things. The store is EITHER entirely at the old version OR entirely at
/// the new one and never half way between. The pack store holds no file any version references but cannot
/// serve. And a REPUBLISH after the kill succeeds and produces the same manifest hash it would have produced
/// without the kill, which is the idempotence assertion and the one that matters: a publish that merely fails
/// safely but cannot be retried is not recoverable.
/// </para>
/// <para>
/// <b>In-process hooks prove the ORDERING and a real kill proves the DURABILITY.</b> The out-of-process half
/// is <c>KhaozEngine.Benchmarks --catalog-crash-probe</c>, which kills a child at each of these same steps
/// against a real SQLite file.
/// </para>
/// </summary>
public class PublishCrashSafetyTests
{
    /// <summary>What a publish of the crash draft produces when nothing interrupts it.</summary>
    /// <param name="VersionNumber">The version it lands at.</param>
    /// <param name="ServerManifestHash">The server manifest's content address.</param>
    /// <param name="ClientManifestHash">The client manifest's content address.</param>
    public sealed record ControlPublish(int VersionNumber, string ServerManifestHash, string ClientManifestHash);

    [Theory]
    [InlineData(CrashStore.InMemory, ContentPublishStep.BeforeIdAllocation)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.AfterIdAllocation)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.BeforeChunkWrite)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.AfterChunkWrite)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.BeforeManifestWrite)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.AfterManifestWrite)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.BeforeCommit)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.AfterCommit)]
    [InlineData(CrashStore.InMemory, ContentPublishStep.DuringSweep)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.BeforeIdAllocation)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.AfterIdAllocation)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.BeforeChunkWrite)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.AfterChunkWrite)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.BeforeManifestWrite)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.AfterManifestWrite)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.BeforeCommit)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.AfterCommit)]
    [InlineData(CrashStore.Sqlite, ContentPublishStep.DuringSweep)]
    public async Task AKillAtAnyStepLeavesTheOldVersionOrTheNewOneAndRepublishesToTheSameHash(
        CrashStore kind,
        ContentPublishStep killAt)
    {
        ControlPublish control = await ControlAsync(kind);

        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        await harness.ApplyAsync(CrashSafetyHarness.Reprice());

        var reached = new List<ContentPublishStep>();
        ContentPublishCommit killed = harness.Commit(step =>
        {
            reached.Add(step);
            if (step == killAt)
            {
                throw new CrashProbeKill(step);
            }
        });

        CrashProbeKill kill = await Assert.ThrowsAsync<CrashProbeKill>(
            () => killed.PublishAsync(CrashSafetyHarness.Request(1)));
        Assert.Equal(killAt, kill.Step);
        Assert.Contains(killAt, reached);

        // 1. Entirely at the old version, or entirely at the new one.
        int active = await harness.Store.GetActiveVersionAsync();
        Assert.True(active is 1 or 2, FormattableString.Invariant($"The store stands at {active}, which is neither version."));
        await AssertWholeAsync(harness, active, control);

        // 2. Nothing any version references is missing from the pack.
        await harness.AssertEveryReferencedFileServesAsync();

        // 3. The retry lands on the SAME manifest hash the uninterrupted publish produced.
        ContentVersionRecord landed = active == 2
            ? Assert.IsType<ContentVersionRecord>(await harness.Store.GetVersionAsync(2))
            : await RepublishAsync(harness);

        Assert.Equal(control.VersionNumber, landed.VersionNumber);
        Assert.Equal(control.ServerManifestHash, landed.ServerManifestHash);
        Assert.Equal(control.ClientManifestHash, landed.ClientManifestHash);
        await harness.AssertEveryReferencedFileServesAsync();
    }

    /// <summary>
    /// Spec 6.11's first pinned state: a kill between step 3's reservation commit and step 4 leaves a gap of
    /// RESERVED BUT UNISSUED ids, bounded by <see cref="ContentIdAllocator.ReserveBatch"/> per type, and needs
    /// no recovery at all.
    /// <para>
    /// The ids the killed attempt ISSUED are burned with it, and the retry takes the next ones. That is
    /// reserve before issue working: the worst a crash can do is skip ids that were never used, and it can
    /// never hand out one that is already on a row.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(CrashStore.InMemory)]
    [InlineData(CrashStore.Sqlite)]
    public async Task AKillAfterTheReservationLeavesABoundedGapOfUnissuedIdsAndNeedsNoRecovery(CrashStore kind)
    {
        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        var ids = (IContentIdPersistence)harness.Store;
        await harness.ApplyAsync(ContentEdit.Add(type, new ContentKey("three"), PublishFixtures.Fields(33)));

        ContentPublishCommit killed = harness.Commit(step =>
        {
            if (step == ContentPublishStep.AfterIdAllocation)
            {
                throw new CrashProbeKill(step);
            }
        });
        await Assert.ThrowsAsync<CrashProbeKill>(() => killed.PublishAsync(CrashSafetyHarness.Request(1)));

        ContentIdHighWater mark = await ids.ReadHighWaterAsync(type);
        Assert.Equal(3, mark.IssuedThrough);
        Assert.True(
            mark.ReservedThrough - mark.IssuedThrough <= ContentIdAllocator.ReserveBatch,
            FormattableString.Invariant(
                $"The gap is {mark.ReservedThrough - mark.IssuedThrough} ids, past the {ContentIdAllocator.ReserveBatch} bound."));
        Assert.Equal(1, await harness.Store.GetActiveVersionAsync());
        Assert.Equal(2, (await harness.RowsAsync()).Total);

        // No recovery, just a republish. The row takes the NEXT id, and the burned one is never reissued.
        ContentPublishResult retried = await harness.Commit().PublishAsync(CrashSafetyHarness.Request(1));
        Assert.Equal(2, retried.VersionNumber);

        ContentRowPage rows = await harness.RowsAsync();
        Assert.Equal(3, rows.Total);
        Assert.Equal(4, rows.Rows[2].Id);
        Assert.Equal("three", rows.Rows[2].Key.ToString());
    }

    /// <summary>
    /// Spec 6.11's second pinned state: a kill between step 9 and step 10 leaves every file of the new version
    /// in the pack AND its <c>versions/n</c> pointer, with nothing in the database referencing them and the
    /// old version still active. The retried publish takes the SAME version number and OVERWRITES the stale
    /// pointer.
    /// <para>
    /// The retry publishes DIFFERENT content, so the overwrite is observable rather than a coincidence: a
    /// pointer that was never rewritten would still name the dead attempt's manifest.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(CrashStore.InMemory)]
    [InlineData(CrashStore.Sqlite)]
    public async Task AKillBetweenTheFilesAndTheCommitLeavesAStalePointerTheRetryOverwrites(CrashStore kind)
    {
        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        await harness.ApplyAsync(CrashSafetyHarness.Reprice());

        ContentPublishCommit killed = harness.Commit(step =>
        {
            if (step == ContentPublishStep.BeforeCommit)
            {
                throw new CrashProbeKill(step);
            }
        });
        await Assert.ThrowsAsync<CrashProbeKill>(() => killed.PublishAsync(CrashSafetyHarness.Request(1)));

        // Every file of the attempt is there, the pointer included, and the database knows nothing about it.
        PackVersionPointer? stale = await harness.Pack.GetVersionPointerAsync(2);
        Assert.NotNull(stale);
        Assert.NotNull(await harness.Pack.GetAsync(stale.ServerManifestHash));
        Assert.NotNull(await harness.Pack.GetAsync(stale.ClientManifestHash));
        Assert.Null(await harness.Store.GetVersionAsync(2));
        Assert.Equal(1, await harness.Store.GetActiveVersionAsync());
        Assert.Equal(11, (await harness.RowsAsync()).Rows[0].Fields[0].Number);

        // A second edit onto the same open draft, so the retry cannot produce the dead attempt's bytes.
        await harness.ApplyAsync(
            ContentEdit.Update(
                new ContentTypeId(PublishFixtures.ThingTypeId),
                2,
                new ContentKey("two"),
                PublishFixtures.Fields(77)));
        ContentPublishResult retried = await harness.Commit().PublishAsync(CrashSafetyHarness.Request(1));

        // The SAME version number, and the pointer now names what actually committed.
        Assert.Equal(2, retried.VersionNumber);
        PackVersionPointer? fresh = await harness.Pack.GetVersionPointerAsync(2);
        Assert.NotNull(fresh);
        Assert.NotEqual(stale.ServerManifestHash, fresh.ServerManifestHash);
        Assert.Equal(retried.ServerManifestHash, fresh.ServerManifestHash);
        Assert.Equal(retried.ClientManifestHash, fresh.ClientManifestHash);
        await harness.AssertEveryReferencedFileServesAsync();
    }

    /// <summary>The publish the kill is measured against: the same draft, the same store, nothing thrown.</summary>
    /// <param name="kind">Which store to run against.</param>
    static async Task<ControlPublish> ControlAsync(CrashStore kind)
    {
        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        await harness.ApplyAsync(CrashSafetyHarness.Reprice());
        ContentPublishResult published = await harness.Commit().PublishAsync(CrashSafetyHarness.Request(1));
        return new ControlPublish(
            published.VersionNumber, published.ServerManifestHash, published.ClientManifestHash);
    }

    /// <summary>
    /// The store read WHOLE: at the old version the draft is untouched and every row is the old one, and at
    /// the new version the draft is gone and every row is the new one. A store half way between the two is
    /// what every one of these cases exists to catch.
    /// </summary>
    static async Task AssertWholeAsync(CrashSafetyHarness harness, int active, ControlPublish control)
    {
        ContentRowPage rows = await harness.RowsAsync();
        ContentDraft? draft = await harness.Store.GetOpenDraftAsync();

        if (active == 1)
        {
            Assert.Null(await harness.Store.GetVersionAsync(2));
            Assert.Single(await harness.Store.ListVersionsAsync());
            Assert.Equal(11, rows.Rows[0].Fields[0].Number);
            Assert.NotNull(draft);
            Assert.Equal(1, draft.EditCount);
            return;
        }

        ContentVersionRecord? landed = await harness.Store.GetVersionAsync(2);
        Assert.NotNull(landed);
        Assert.Equal(control.ServerManifestHash, landed.ServerManifestHash);
        Assert.Equal(99, rows.Rows[0].Fields[0].Number);
        Assert.Null(draft);
    }

    /// <summary>The retry: the draft is still open, so the same request publishes it again.</summary>
    static async Task<ContentVersionRecord> RepublishAsync(CrashSafetyHarness harness)
    {
        ContentPublishResult retried = await harness.Commit().PublishAsync(CrashSafetyHarness.Request(1));
        ContentVersionRecord? landed = await harness.Store.GetVersionAsync(retried.VersionNumber);
        Assert.NotNull(landed);
        return landed;
    }
}

/// <summary>
/// The kill itself, thrown from the step hook. Its own type so a case can assert it caught ITS kill rather
/// than a defect that happened to throw at the same moment.
/// </summary>
/// <param name="step">The step the publish was interrupted at.</param>
public sealed class CrashProbeKill(ContentPublishStep step)
    : Exception(FormattableString.Invariant($"Crash probe kill at {step}."))
{
    /// <summary>The step the publish was interrupted at.</summary>
    public ContentPublishStep Step { get; } = step;
}
