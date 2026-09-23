using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Publish;

/// <summary>
/// Step 3's seeding against a RIVAL that raises the same type's marks while the publish is in flight, which is
/// what two upgrade runners on one catalog do: both seed from carried ids, and neither holds a lock across the
/// other's prepare.
/// <para>
/// <b>The interleaving is placed, not hoped for.</b> The id seam is wrapped, and the rival's raise lands
/// immediately before this publish's first id write reaches the store. That is the latest moment a rival can
/// land, so any decision the seed made from an earlier read is stale by then, and a seed that compares inside
/// its own write is not affected at all.
/// </para>
/// <para>
/// A carried id the marks already cover publishes under that id, which the sequential case pins elsewhere. A
/// rival getting there first is the same catalog state, so it must give the same answer.
/// </para>
/// </summary>
public class CarriedIdSeedRaceTests
{
    /// <summary>The id the publish carries, above the marks version 1 left behind.</summary>
    const int Carried = 2000;

    /// <summary>Where the rival's raise leaves both marks, above the carried id.</summary>
    const int RivalThrough = 2048;

    [Theory]
    [InlineData(CrashStore.InMemory)]
    [InlineData(CrashStore.Sqlite)]
    public async Task ARivalRaisingTheMarksBeforeTheSeedLandsLeavesTheCarriedPublishIntact(CrashStore kind)
    {
        using CrashSafetyHarness harness = await CrashSafetyHarness.StartAsync(kind);
        var type = new ContentTypeId(PublishFixtures.ThingTypeId);
        var ids = (IContentIdPersistence)harness.Store;

        ContentIdHighWater before = await ids.ReadHighWaterAsync(type);
        Assert.True(before.ReservedThrough < Carried, Describe(before));

        await harness.ApplyAsync(
            ContentEdit.Import(type, Carried, new ContentKey("carried"), PublishFixtures.Fields(77)));

        var rival = new RivalRaiseIdPersistence(ids, type, RivalThrough);
        var commit = new ContentPublishCommit(
            harness.Store,
            harness.Pack,
            new ContentPublisher(harness.Store, rival, harness.Registry));

        ContentPublishResult published = await commit.PublishAsync(CrashSafetyHarness.Request(1));

        Assert.True(rival.Fired, "The rival's raise never landed, so the interleaving was not exercised.");
        Assert.Equal(2, published.VersionNumber);

        ContentRowPage rows = await harness.RowsAsync();
        Assert.Equal(3, rows.Total);
        Assert.Equal(Carried, rows.Rows[2].Id);
        Assert.Equal("carried", rows.Rows[2].Key.ToString());

        // The rival's higher promise stands. A seed that wrote its own lower number over it would take a
        // durable reservation back.
        Assert.Equal(new ContentIdHighWater(RivalThrough, RivalThrough), await ids.ReadHighWaterAsync(type));
    }

    static string Describe(ContentIdHighWater mark)
        => FormattableString.Invariant($"reserved {mark.ReservedThrough}, issued {mark.IssuedThrough}");

    /// <summary>
    /// Forwards every call, and on the FIRST write that reaches it for one type commits a rival's raise of
    /// that type's two marks through the inner store before forwarding the write. The rival writes in the
    /// same order any seed does, reserve then issue.
    /// </summary>
    sealed class RivalRaiseIdPersistence(IContentIdPersistence inner, ContentTypeId watched, int rivalThrough)
        : IContentIdPersistence
    {
        public bool Fired { get; private set; }

        public Task<ContentIdHighWater> ReadHighWaterAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadHighWaterAsync(type, cancellationToken);

        public Task<int?> ReadMaxDefinitionIdAsync(
            ContentTypeId type, CancellationToken cancellationToken = default)
            => inner.ReadMaxDefinitionIdAsync(type, cancellationToken);

        public async Task CommitReservedThroughAsync(
            ContentTypeId type, int reservedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync(type, cancellationToken).ConfigureAwait(false);
            await inner.CommitReservedThroughAsync(type, reservedThrough, cancellationToken).ConfigureAwait(false);
        }

        public async Task CommitIssuedThroughAsync(
            ContentTypeId type, int issuedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync(type, cancellationToken).ConfigureAwait(false);
            await inner.CommitIssuedThroughAsync(type, issuedThrough, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> CommitCarriedThroughAsync(
            ContentTypeId type, int carriedThrough, CancellationToken cancellationToken = default)
        {
            await RivalFirstAsync(type, cancellationToken).ConfigureAwait(false);
            return await inner.CommitCarriedThroughAsync(type, carriedThrough, cancellationToken).ConfigureAwait(false);
        }

        public Task<ContentFamily?> ReadFamilyAsync(
            long familyId, CancellationToken cancellationToken = default)
            => inner.ReadFamilyAsync(familyId, cancellationToken);

        public Task<ContentFamilyBlock> CommitFamilyBlockAsync(
            long familyId, int baseId, int issuedThrough, CancellationToken cancellationToken = default)
            => inner.CommitFamilyBlockAsync(familyId, baseId, issuedThrough, cancellationToken);

        public Task CommitFamilyNextFreeIdAsync(
            long familyId, int blockOrdinal, int nextFreeId, CancellationToken cancellationToken = default)
            => inner.CommitFamilyNextFreeIdAsync(familyId, blockOrdinal, nextFreeId, cancellationToken);

        async Task RivalFirstAsync(ContentTypeId written, CancellationToken cancellationToken)
        {
            if (Fired || written != watched)
            {
                return;
            }

            Fired = true;
            await inner.CommitReservedThroughAsync(watched, rivalThrough, cancellationToken).ConfigureAwait(false);
            await inner.CommitIssuedThroughAsync(watched, rivalThrough, cancellationToken).ConfigureAwait(false);
        }
    }
}
