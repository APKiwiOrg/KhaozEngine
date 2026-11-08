using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;
using static KhaozEngine.Tests.Catalog.Publish.TextPublishFixtures;

namespace KhaozEngine.Tests.Catalog.Publish;

public sealed class TextProducerRefusalTests
{
    [Theory]
    [InlineData("key-utf8", ContentAuthoringRefusal.TextKeyUtf8Invalid)]
    [InlineData("key-length", ContentAuthoringRefusal.TextKeyBytesExceeded)]
    [InlineData("unicode", ContentAuthoringRefusal.TextMalformedUnicode)]
    [InlineData("type-unicode", ContentAuthoringRefusal.TextMalformedUnicode)]
    [InlineData("row", ContentAuthoringRefusal.RowUnknown)]
    [InlineData("collision", ContentAuthoringRefusal.TextKeyCollision)]
    public void RegenerationNamesTheFailedProducerRuleAndAcceptsItsRepair(string defect, ContentAuthoringRefusal reason)
    {
        string field = defect == "unicode" ? "na\uD800me" : defect == "key-length" ? new string('n', 64) : "name";
        ContentTypeRegistry registry = MarkerRegistry(field);
        if (defect is "type-unicode" or "key-length")
        {
            var schema = new ContentFieldSchema([
                new("value", ContentFieldKind.Int, null, ContentVisibility.Client, true),
                new(field, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, false)]);
            registry = new ContentTypeRegistry();
            registry.RegisterContentType(ContentRegistrationBand.Game, Item.Value,
                defect == "type-unicode" ? "it\uD800em" : new string('t', 64),
                new MarkerCodec(schema), null, schema, ContentVisibility.Client, 256);
        }
        ContentTextRevision[] values = defect == "collision"
            ? [Revision(1, field), Revision(2, field)] : [Revision(1, field)];
        var keys = new Dictionary<(ushort, int), ContentKey> { [(Item.Value, 1)] = Sword };
        if (defect == "key-utf8") keys[(Item.Value, 1)] = new ContentKey([0xFF], 0, 1);
        if (defect == "key-length") keys[(Item.Value, 1)] = new ContentKey(new string('k', 63));
        if (defect == "row") keys.Clear();
        if (defect == "collision") keys[(Item.Value, 2)] = Sword;

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextChunkBuilder.Regenerate(registry, Snapshot(values), keys));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal(defect == "row" ? ContentAuthoringException.UnknownRowReason
            : defect == "collision" ? ContentAuthoringException.EditTargetCollisionReason
            : ContentAuthoringException.TextBoundsReason, refused.Reason);
        Assert.Equal("en", refused.Language);
        if (defect == "collision") Assert.Equal("item.sword.name", refused.DerivedKey);
        else
        {
            Assert.Equal(Item, refused.Type);
            Assert.Equal(1, refused.Id);
            Assert.Equal(field, refused.FieldName);
        }
        if (defect == "key-length")
        {
            Assert.Equal(193L, refused.ActualBytes);
            Assert.Equal(192L, refused.MaximumBytes);
        }
        if (defect is "unicode" or "type-unicode")
        {
            Assert.Equal(defect == "unicode" ? "FieldName" : "TypeKey", refused.MemberName);
            Assert.Equal(2, refused.Index);
            Assert.Equal(defect == "unicode" ? "value" : "registration",
                Assert.IsType<ArgumentException>(refused.InnerException).ParamName);
        }

        values = defect == "collision" ? [Revision(1), Revision(2)] : [Revision(1, defect == "key-length" ? field : "name")];
        ContentTypeRegistry repairedRegistry = defect == "key-length" ? registry : TextRegistry();
        keys[(Item.Value, 1)] = defect == "key-length" ? new ContentKey(new string('k', 62)) : Sword;
        if (defect == "collision") keys[(Item.Value, 2)] = new ContentKey("shield");
        ContentRegeneratedText repaired = Assert.Single(ContentTextChunkBuilder.Regenerate(repairedRegistry, Snapshot(values), keys));
        Assert.True(ContentTextIndex.TryDecode(repaired.StoredFile, out ContentTextIndex? index, out _));
        Assert.Equal(values.Length, index!.EntryCount);
        if (defect == "key-length")
        {
            keys[(Item.Value, 1)] = new ContentKey(new string('k', 61));
            Assert.Single(ContentTextChunkBuilder.Regenerate(repairedRegistry, Snapshot(values), keys));
        }
    }

    [Fact]
    public void FramedBodyLimitIsInclusiveAndCountsEveryLengthPrefix()
    {
        var values = new ContentTextRevision[2044];
        var keys = new Dictionary<(ushort, int), ContentKey>();
        for (int i = 0; i < values.Length; i++)
        {
            keys[(Item.Value, i + 1)] = new ContentKey(FormattableString.Invariant($"k{i:D4}"));
            values[i] = Revision(i + 1, value: new string('a', i == 2043 ? 4167 : 8192));
        }

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextChunkBuilder.Regenerate(TextRegistry(), Snapshot(values), keys));
        Assert.Equal(ContentAuthoringRefusal.TextChunkBytesExceeded, refused.Refusal);
        Assert.Equal(ContentAuthoringException.TextBoundsReason, refused.Reason);
        Assert.Equal("en", refused.Language);
        // Two count bytes, 2043 entries of 8210 bytes and one of 4185 bytes total 16777217.
        Assert.Equal(16777217L, refused.ActualBytes);
        Assert.Equal(16777216L, refused.MaximumBytes);
        values[^1] = Revision(2044, value: new string('a', 4166));
        Assert.Single(ContentTextChunkBuilder.Regenerate(TextRegistry(), Snapshot(values), keys));
        values[^1] = Revision(2044, value: new string('a', 4165));
        Assert.Single(ContentTextChunkBuilder.Regenerate(TextRegistry(), Snapshot(values), keys));
    }

    [Fact]
    public void AnUnboundTextIntentNamesItsKeyAndAcceptsALiveRow()
    {
        var text = new ContentDraftTextState([ContentTextEdit.Set(Target("name", "en"), "Sword")], []);
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextExpectedValues.Compute([], [], text, []));
        Assert.Equal(ContentAuthoringRefusal.RowUnknown, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(0, refused.Id);
        Assert.Equal("sword", refused.RowKey);
        var row = new ContentRowRevision(new ContentRow(Item, 1, Sword, 0, false, []), 1, null, null);
        Assert.Equal("Sword", Assert.Single(ContentTextExpectedValues.Compute([], [], text, [row])).Value);
    }

    [Fact]
    public async Task ReuseRequiresAChunkTheBaselineRecorded()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, [Add()], ContentTextEdit.Set(Target("name", "en"), "Sword"));
        await ApplyAsync(text, null, ContentTextEdit.Remove(Target("name", "en")));
        ContentTextChunkRecord real = Chunk("en");
        var (snapshot, rowPlan) = await FreezeAndPlanAsync(store, [new ManifestLanguageEntry("en", real.Hash)]);
        var candidate = new ContentTextCandidate([], [new("en", "en")], [], []);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, candidate, [real.AsReused()]);
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextChunkConfirmation.Require(TextRegistry(), plan));
        Assert.Equal(ContentAuthoringRefusal.TextReusedChunkUnrecorded, refused.Refusal);
        Assert.Equal("en", refused.Language);
        Assert.Equal(0, refused.VersionNumber);
        Assert.Equal(real.Hash, refused.TextHash);
        ContentTextChunkConfirmation.Require(TextRegistry(), new(rowPlan, snapshot, candidate, [real]));
    }

    [Fact]
    public async Task ReuseRefusesAChangedWireSpellingEvenWhenItsNewHashMatchesItsValues()
    {
        var store = TextStore();
        await PublishNamedRowAsync(store, "sword", "Sword");
        await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(9)])], Actor, Operator, "row only");
        ContentTextChunkRecord original = Chunk("en", ("sword", "Sword"));
        var (snapshot, rowPlan) = await FreezeAndPlanAsync(store, [new ManifestLanguageEntry("en", original.Hash)]);
        var candidate = new ContentTextCandidate(snapshot.BaselineText.Revisions, [new("en", "en")], [], []);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, candidate, [original.AsReused()]);
        // Public read-only list facades currently expose mutable arrays. A caller can replace their
        // entries after construction, so commit confirmation must still identify the spelling refusal.
        var languages = (IList<ContentTextLanguage>)plan.Languages;
        var chunks = (IList<ContentTextChunkRecord>)plan.Chunks;
        ContentTextChunkRecord changed = Chunk("EN", ("sword", "Sword"));
        languages[0] = new("en", "EN", changed.Hash);
        chunks[0] = changed.AsReused();

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextChunkConfirmation.Require(TextRegistry(), plan));
        Assert.Equal(ContentAuthoringRefusal.TextReusedWireTagMismatch, refused.Refusal);
        Assert.Equal("en", refused.Language);
        Assert.Equal(1, refused.VersionNumber);
        Assert.Equal("EN", refused.WireTag);
        Assert.Equal("en", refused.ExpectedWireTag);
        Assert.Equal(changed.Hash, refused.TextHash);
        languages[0] = new("en", "en", original.Hash);
        chunks[0] = original.AsReused();
        ContentTextChunkConfirmation.Require(TextRegistry(), plan);
    }

    [Fact]
    public async Task CandidateRequiresItsTextLanguageToBeDeclared()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, [Add()], ContentTextEdit.Set(Target("name", "en"), "Sword"));
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        var publisher = new ContentPublisher(store, store, TextRegistry());
        ContentPublishRowPhase rows = await publisher.PrepareRowsAsync(Request(0), snapshot.Baseline, snapshot.Draft, CancellationToken.None);
        var draft = new ContentDraft(new(snapshot.TextState.Edits, []), 0, Actor, DateTimeOffset.UnixEpoch,
            "missing language", snapshot.Draft.Changes, 0);
        var undeclared = new ContentTextPublishSnapshot(snapshot.StoreEpoch, snapshot.Baseline, snapshot.BaselineText, draft);
        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() =>
            ContentTextCandidateBuilder.Build(TextRegistry(), undeclared, rows));
        Assert.Equal(ContentAuthoringRefusal.BundleTextLanguageUndeclared, refused.Refusal);
        Assert.Equal("en", refused.Language);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(1, refused.Id);
        Assert.Equal("name", refused.FieldName);
        Assert.Single(ContentTextCandidateBuilder.Build(TextRegistry(), snapshot, rows).Values);
    }

    [Fact]
    public async Task TextPublisherNamesBothBaseVersionsBeforePreparingRows()
    {
        var store = TextStore();
        IContentTextAuthoringStore text = store;
        await ApplyAsync(text, [Add()], ContentTextEdit.Set(Target("name", "en"), "Sword"));
        ContentTextPublishSnapshot snapshot = await text.FreezeChangesAsync(0);
        var publisher = new ContentTextPublisher(new ContentPublisher(store, store, TextRegistry()));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            publisher.PrepareAsync(Request(7), snapshot, CancellationToken.None));
        Assert.Equal(ContentAuthoringRefusal.BaseVersionMoved, refused.Refusal);
        Assert.Equal(7, refused.ExpectedBaseVersion);
        Assert.Equal(0, refused.ActualBaseVersion);
        Assert.Equal(1, (await publisher.PrepareAsync(Request(0), snapshot, CancellationToken.None)).VersionNumber);
    }

    sealed class MarkerCodec(ContentFieldSchema schema) : ContentRowCodecBase(Item, schema);

    static ContentTextRevision Revision(int id, string field = "name", string value = "Sword")
        => new(Item, id, field, "en", value, 1, null);

    static ContentVersionTextSnapshot Snapshot(IReadOnlyList<ContentTextRevision> values)
        => new("epoch", 1, values, [new("en", "en", Hash("en"))]);
}
