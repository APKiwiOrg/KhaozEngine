using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Publish;
using KhaozEngine.Tests.Catalog.Store;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The field-level diff of spec 10.6, plus the chunk summary that says what publishing it would cost a
/// client to download.
/// <para>
/// <b>It is computed over the ROWS and never over chunk hashes.</b> Two versions whose chunk hashes differ
/// tell an operator that something changed somewhere in a slot range of 256 ids, which is not an answer, and
/// two versions whose hashes agree can still differ in the server-only half of a field set.
/// </para>
/// </summary>
public class ContentDiffTests
{
    static ContentTypeId Thing => new(PublishFixtures.ThingTypeId);

    static ContentRowRevision Revision(int id, string key, int value, bool retired = false, int validFrom = 1)
        => new(
            new ContentRow(
                Thing,
                id,
                new ContentKey(key),
                0,
                retired,
                [
                    ContentFieldValue.OfNumber(ContentFieldKind.Int, value),
                    ContentFieldValue.Absent(ContentFieldKind.Bool),
                ]),
            validFrom,
            null,
            null);

    [Fact]
    public void ADiffNamesEveryDifferingFieldWithItsBeforeAndAfter()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        ContentDiff diff = ContentDiff.Between(
            1,
            [Revision(1, "one", 10), Revision(2, "two", 20)],
            2,
            [Revision(1, "one", 99), Revision(2, "two", 20)],
            registry);

        ContentDiffEntry entry = Assert.Single(diff.Changes);
        Assert.Equal(ContentDiffOperation.Update, entry.Operation);
        Assert.Equal(1, entry.Id);
        Assert.Equal("one", entry.Key.ToString());

        ContentDiffField field = Assert.Single(entry.Fields);
        Assert.Equal(PublishFixtures.ValueField, field.Field);
        Assert.Equal("10", field.Before);
        Assert.Equal("99", field.After);
        Assert.False(diff.IsEmpty);
        Assert.Equal(1, diff.FromVersion);
        Assert.Equal(2, diff.ToVersion);
    }

    [Fact]
    public void AnAddCarriesItsWholeFieldSetAndARetireIsItsOwnOperation()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        ContentDiff diff = ContentDiff.Between(
            1,
            [Revision(1, "one", 10)],
            2,
            [Revision(1, "one", 10, retired: true), Revision(2, "two", 20)],
            registry);

        Assert.Equal(2, diff.Changes.Count);
        ContentDiffEntry retired = diff.Changes.Single(change => change.Id == 1);
        Assert.Equal(ContentDiffOperation.Retire, retired.Operation);
        Assert.Empty(retired.Fields);

        ContentDiffEntry added = diff.Changes.Single(change => change.Id == 2);
        Assert.Equal(ContentDiffOperation.Add, added.Operation);
        ContentDiffField field = Assert.Single(added.Fields);
        Assert.Null(field.Before);
        Assert.Equal("20", field.After);
    }

    [Fact]
    public void ADefinitionLiveAtTheSourceAndNotAtTheDestinationIsReportedRatherThanDropped()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        // A publish can never produce this, because a definition that leaves play is retired and its row
        // stays in the pack forever. A diff whose destination is an EARLIER version can.
        ContentDiff diff = ContentDiff.Between(
            2,
            [Revision(1, "one", 10), Revision(2, "two", 20)],
            1,
            [Revision(1, "one", 10)],
            registry);

        ContentDiffEntry entry = Assert.Single(diff.Changes);
        Assert.Equal(ContentDiffOperation.Removed, entry.Operation);
        Assert.Equal(2, entry.Id);
    }

    [Fact]
    public void ADiffOfTwoIdenticalRowSetsIsEmpty()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        ContentDiff diff = ContentDiff.Between(
            1, [Revision(1, "one", 10)], 2, [Revision(1, "one", 10)], registry);

        Assert.True(diff.IsEmpty);
        Assert.Empty(diff.Changes);
        Assert.Single(diff.ChunkSummary);
        Assert.Equal(0, diff.ChunkSummary[0].ChangedChunks);
        Assert.Equal(1, diff.ChunkSummary[0].TotalChunks);
    }

    [Fact]
    public void TheChunkSummaryIsTheDownloadAnEditWouldCost()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        // Ids 1 and 2 sit in chunk 0 and id 300 sits in chunk 1 at 256 slots, so editing one row of chunk 0
        // is one changed chunk out of two.
        ContentRowRevision[] before =
        [
            Revision(1, "one", 10),
            Revision(2, "two", 20),
            Revision(PublishFixtures.SecondChunkId, "far", 30),
        ];
        ContentRowRevision[] after =
        [
            Revision(1, "one", 99),
            Revision(2, "two", 20),
            Revision(PublishFixtures.SecondChunkId, "far", 30),
        ];

        ContentChunkSummaryEntry summary = Assert.Single(ContentDiff.Between(1, before, 2, after, registry).ChunkSummary);

        Assert.Equal(PublishFixtures.ThingTypeKey, summary.TypeKey);
        Assert.Equal(1, summary.ChangedChunks);
        Assert.Equal(2, summary.TotalChunks);
    }

    [Fact]
    public void ADiffAgainstTheDraftAppliedCandidateCarriesNoDestinationNumber()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);

        ContentDiff diff = ContentDiff.Between(
            1, [Revision(1, "one", 10)], null, [Revision(1, "one", 99)], registry);

        // Spec 10.6's "to": 0 read as what it is: a row set that has no number yet because it has not been
        // published.
        Assert.Null(diff.ToVersion);
        Assert.Single(diff.Changes);
    }

    [Fact]
    public void ChangesAreOrderedByTypeThenDefinitionIdWhicheverOrderTheRowsArrivedIn()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing, PublishFixtures.Other);
        var other = new ContentTypeId(PublishFixtures.OtherTypeId);

        ContentRowRevision Second(int id, string key, int value)
            => new(
                new ContentRow(
                    other,
                    id,
                    new ContentKey(key),
                    0,
                    false,
                    [
                        ContentFieldValue.OfNumber(ContentFieldKind.Int, value),
                        ContentFieldValue.Absent(ContentFieldKind.Bool),
                    ]),
                1,
                null,
                null);

        ContentDiff diff = ContentDiff.Between(
            1,
            [],
            2,
            [Second(7, "g", 1), Revision(9, "b", 1), Second(3, "f", 1), Revision(4, "a", 1)],
            registry);

        Assert.Equal(
            [
                (PublishFixtures.ThingTypeId, 4),
                (PublishFixtures.ThingTypeId, 9),
                (PublishFixtures.OtherTypeId, 3),
                (PublishFixtures.OtherTypeId, 7),
            ],
            diff.Changes.Select(change => (change.Type.Value, change.Id)));
    }

    [Fact]
    public async Task ThePublishAuditIsTheDiffsOwnRendering()
    {
        using var root = new TemporaryRoot();
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var pack = new FileSystemPackStore(root.Path);
        InMemoryContentAuthoringStore store = PublishFixtures.Store(registry, pack);

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Add(Thing, new ContentKey("one"), PublishFixtures.Fields(10)));
        await store.PublishAsync(PublishFixtures.Request(0));

        await PublishFixtures.ApplyAsync(
            store,
            ContentEdit.Update(
                Thing,
                1,
                new ContentKey("one"),
                [new ContentFieldEdit(PublishFixtures.ValueField, ContentFieldValue.OfNumber(ContentFieldKind.Int, 42))]));
        await store.PublishAsync(PublishFixtures.Request(1));

        // An operator reading a diff and an operator reading the audit row the publish wrote see the SAME
        // string for the same value rather than two formattings of one number.
        ContentAuditEntry entry = (await store.ListAuditAsync(default, 0, 0, 100))
            .First(one => string.Equals(one.Action, ContentAuditActions.Publish, StringComparison.Ordinal)
                && one.VersionNumber == 2);

        Assert.Equal(PublishFixtures.ValueField, entry.FieldName);
        Assert.Equal("10", entry.BeforeValue);
        Assert.Equal("42", entry.AfterValue);
    }

    [Fact]
    public void ADiffOverATypeTheRegistryDoesNotDeclareIsRefused()
    {
        ContentTypeRegistry registry = PublishFixtures.Registry(PublishFixtures.Thing);
        var stranger = new ContentRowRevision(
            new ContentRow(new ContentTypeId(2001), 1, new ContentKey("x"), 0, false, []), 1, null, null);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(
            () => ContentDiff.Between(1, [], 2, [stranger], registry));

        Assert.Equal(ContentAuthoringException.UnknownTypeReason, refused.Reason);
    }
}
