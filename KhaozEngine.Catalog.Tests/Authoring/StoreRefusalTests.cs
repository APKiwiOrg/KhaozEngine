using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using KhaozEngine.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

public abstract partial class StoreRefusalTests : IDisposable
{
    readonly TemporaryCatalogDatabase _database = new();
    SqliteContentAuthoringStore? _sqlite;

    protected abstract bool UsesSqlite { get; }

    async Task<IContentTextAuthoringStore> OpenAsync(IPackStore? pack = null, Func<DateTimeOffset>? clock = null)
    {
        if (!UsesSqlite) return new InMemoryContentAuthoringStore(TextRegistry(), pack, clock);
        _sqlite?.Dispose();
        _sqlite = new SqliteContentAuthoringStore(_database.ConnectionString, TextRegistry(), pack, clock);
        await _sqlite.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return _sqlite;
    }

    public void Dispose()
    {
        _sqlite?.Dispose();
        _database.Dispose();
    }

    [Theory]
    [InlineData(15, ContentAuthoringRefusal.FamilyDeclarationBlockSizeOutsideRange, 16)]
    [InlineData(65537, ContentAuthoringRefusal.FamilyDeclarationBlockSizeOutsideRange, 65536)]
    [InlineData(24, ContentAuthoringRefusal.FamilyDeclarationBlockSizeNotPowerOfTwo, 16)]
    public async Task FamilyDeclarationNamesItsSizeRuleAndAcceptsTheInclusiveRepair(
        int size, ContentAuthoringRefusal reason, int repair)
    {
        var store = await OpenAsync();
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.CreateFamilyAsync(Item, "weapons", size, Actor, Operator));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal("weapons", refused.FamilyKey);
        Assert.Equal(size, refused.ActualBlockSize);
        if (reason == ContentAuthoringRefusal.FamilyDeclarationBlockSizeOutsideRange)
        {
            Assert.Equal(16, refused.MinimumBlockSize);
            Assert.Equal(65536, refused.MaximumBlockSize);
        }
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Empty(await store.ListAuditAsync(default, 0, 0, 500));
        Assert.Equal(repair, (await store.CreateFamilyAsync(Item, "weapons", repair, Actor, Operator)).BlockSize);
    }

    [Fact]
    public async Task AFamilyKeyCollisionKeepsTheFirstDeclaration()
    {
        var store = await OpenAsync();
        ContentFamily first = await store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator));
        Assert.Equal(ContentAuthoringRefusal.FamilyKeyDuplicate, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal("weapons", refused.FamilyKey);
        ContentFamily after = Assert.Single(await store.ListFamiliesAsync(Item));
        Assert.Equal(first.FamilyId, after.FamilyId);
        Assert.Equal(first.FamilyKey, after.FamilyKey);
        Assert.Equal(first.BlockSize, after.BlockSize);
        Assert.Equal(first.Blocks, after.Blocks);
        Assert.Equal("armour", (await store.CreateFamilyAsync(Item, "armour", 16, Actor, Operator)).FamilyKey);
    }

    [Theory]
    [InlineData("name", ContentAuthoringRefusal.DerivedMarkerNotAuthorable, 0)]
    [InlineData("valeu", ContentAuthoringRefusal.FieldUnknown, 1)]
    public async Task FieldsMustBeDeclaredAndAuthorable(string field, ContentAuthoringRefusal refusal, int ordinal)
    {
        var store = await OpenAsync();
        ContentEdit invalid = ContentEdit.Add(Item, Sword,
            [new ContentFieldEdit(field, ContentFieldValue.OfNumber(ContentFieldKind.Int, 1))]);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.ApplyEditsAsync(ordinal == 0 ? [invalid] : [Add("shield"), invalid], Actor, Operator, ""));
        Assert.Equal(refusal, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(0, refused.Id);
        Assert.Equal("item", refused.TypeKey);
        Assert.Equal(field, refused.FieldName);
        Assert.Equal(ordinal, refused.EditOrdinal);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(ordinal + 1, (await store.ApplyEditsAsync(ordinal == 0 ? [Add()] : [Add("shield"), Add()], Actor, Operator, "")).EditCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BundleDeclarationsMustMatchTheRegisteredKeyAndChunkSlots(bool key)
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        ContentBundle Bundle(string typeKey, int slots) => new(1, "source", 0,
            [new ContentBundleType(Item, typeKey, ContentVisibility.Client, slots, null, Schema())], [new ContentBundleRow(Item, 1, Sword, false, null, [Value(1)])], [], []);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.ImportBundleAsync(Bundle(key ? "other" : "item", key ? 256 : 512), Actor, Operator, ""));
        Assert.Equal(ContentAuthoringRefusal.BundleTypeDeclarationMismatch, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(key ? "other" : "item", refused.ActualTypeKey);
        Assert.Equal("item", refused.RegisteredTypeKey);
        Assert.Equal(key ? 256 : 512, refused.ActualChunkSlots);
        Assert.Equal(256, refused.RegisteredChunkSlots);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(1, (await store.ImportBundleAsync(Bundle("item", 256), Actor, Operator, "")).VersionNumber);
    }

    [Fact]
    public async Task ABundleRowMustNameACarriedFamily()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        ContentBundle Bundle(string? family) => new(1, "source", 0,
            [new ContentBundleType(Item, "item", ContentVisibility.Client, 256, null, Schema())],
            [new ContentBundleRow(Item, 1, Sword, false, family, [Value(1)])], [], []);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.ImportBundleAsync(Bundle("absent"), Actor, Operator, ""));
        Assert.Equal(ContentAuthoringRefusal.BundleFamilyUnknown, refused.Refusal);
        Assert.Equal(Item, refused.Type);
        Assert.Equal(1, refused.Id);
        Assert.Equal("sword", refused.RowKey);
        Assert.Equal("absent", refused.FamilyKey);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(1, (await store.ImportBundleAsync(Bundle(null), Actor, Operator, "")).VersionNumber);
    }

    [Theory]
    [InlineData("missing", ContentAuthoringRefusal.FrozenDraftLost)]
    [InlineData("freeze", ContentAuthoringRefusal.TextPlanFreezeMismatch)]
    [InlineData("baseline", ContentAuthoringRefusal.TextPlanBaselineMismatch)]
    [InlineData("draft", ContentAuthoringRefusal.TextPlanDraftMismatch)]
    [InlineData("close", ContentAuthoringRefusal.TextCloseRevisionMissing)]
    [InlineData("row", ContentAuthoringRefusal.TextPlanRowUnknown)]
    public async Task TextCommitNamesTheStateThatChangedAndCommitsAfterRepair(string defect, ContentAuthoringRefusal reason)
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack(), () => DateTimeOffset.UnixEpoch);
        await PublishNamedRowAsync(store, "sword", "Sword");
        await ApplyAsync(store, [ContentEdit.Update(Item, 1, Sword, [Value(2)])], ContentTextEdit.Set(Target("name", "en"), "Blade"));
        ContentTextChunkRecord chunk = Chunk("en", ("sword", "Blade"));
        var (snapshot, rowPlan) = await FreezeAndPlanAsync(store, [new ManifestLanguageEntry("en", chunk.Hash)]);
        ContentTextPublishSnapshot originalSnapshot = snapshot;
        if (defect == "baseline")
            snapshot = new(snapshot.StoreEpoch, snapshot.Baseline,
                new(snapshot.StoreEpoch, 1, [new(Item, 1, "name", "en", "Forged", 1, null)], snapshot.BaselineText.Languages), snapshot.Draft);
        ContentTextRevision held = Assert.Single(snapshot.BaselineText.Revisions);
        var inserted = new ContentTextRevision(Item, 1, "name", "en", "Blade", 2, null);
        var candidate = new ContentTextCandidate([inserted], [new("en", "en")], [held], [inserted]);
        var plan = new ContentTextPublishPlan(rowPlan, snapshot, candidate, [chunk]);
        if (defect == "missing")
        {
            await store.ClearDraftFreezeAsync();
            Assert.True(await store.TryDiscardChangesAsync((await store.GetOpenDraftAsync())!, Actor, Operator));
        }
        if (defect == "freeze") await store.ClearDraftFreezeAsync();
        if (defect == "draft") snapshot.Draft.Changes.Apply(ContentEdit.Update(Item, 1, Sword, [Value(9)]));
        Task Attempt()
        {
            // These defensive guards now take typed fault values because validated plan lists resist casts.
            if (defect == "close")
            {
                var close = new ContentTextRevision(Item, 1, "name", "en", "Forged", 1, null);
                if (UsesSqlite) SqliteContentAuthoringStore.RefuseMissingTextCloseForTests(close, false);
                else InMemoryContentAuthoringStore.RefuseMissingTextCloseForTests(close, false);
                return Task.CompletedTask;
            }
            if (defect == "row")
            {
                var revision = new ContentTextRevision(Item, 9, "name", "en", "Blade", 2, null);
                if (UsesSqlite) SqliteContentAuthoringStore.RequireTextPlanRowForTests(rowPlan, revision);
                else InMemoryContentAuthoringStore.RequireTextPlanRowForTests(rowPlan, revision);
                return Task.CompletedTask;
            }
            return store.CommitTextPublishAsync(plan, Request(1), null);
        }
        string before = ContentBundleJson.Write(await store.ExportBundleAsync(1));
        ContentDraft? draftBefore = await store.GetOpenDraftAsync();
        IReadOnlyList<ContentAuditEntry> auditBefore = await store.ListAuditAsync(default, 0, 0, 500);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(Attempt);
        Assert.Equal(reason, refused.Refusal);
        if (defect is "row" or "close")
        {
            Assert.Equal(default(ContentTypeId), refused.Type);
            Assert.Equal(0, refused.Id);
            Assert.Equal(Item, refused.TextRowType);
            Assert.Equal(defect == "row" ? 9 : 1, refused.TextRowId);
            Assert.Equal("name", refused.FieldName);
            Assert.Equal("en", refused.Language);
            if (defect == "close") Assert.Equal(1, refused.FromVersion);
        }
        else Assert.Equal(1, refused.ExpectedBaseVersion);
        if (defect == "freeze") Assert.Null(refused.ActualBaseVersion);
        Assert.Equal(before, ContentBundleJson.Write(await store.ExportBundleAsync(1)));
        if (draftBefore is null) Assert.Null(await store.GetOpenDraftAsync());
        else Assert.True(ContentTextCompatibility.SameDraft(draftBefore, (await store.GetOpenDraftAsync())!));
        Assert.Equal(auditBefore, await store.ListAuditAsync(default, 0, 0, 500));
        Assert.Equal("Sword", Assert.Single((await store.ReadTextSnapshotAsync(1)).Revisions).Value);
        Assert.Equal(1, await store.GetActiveVersionAsync());
        Assert.Single(await store.ListVersionsAsync());
        if (defect == "draft") snapshot.Draft.Changes.Apply(ContentEdit.Update(Item, 1, Sword, [Value(2)]));
        if (defect == "baseline")
        {
            held = Assert.Single(originalSnapshot.BaselineText.Revisions);
            candidate = new([inserted], [new("en", "en")], [held], [inserted]);
            plan = new(rowPlan, originalSnapshot, candidate, [chunk]);
        }
        if (defect == "close")
        {
            if (UsesSqlite) SqliteContentAuthoringStore.RefuseMissingTextCloseForTests(held, true);
            else InMemoryContentAuthoringStore.RefuseMissingTextCloseForTests(held, true);
        }
        if (defect == "row")
            Assert.Equal(Sword, UsesSqlite
                ? SqliteContentAuthoringStore.RequireTextPlanRowForTests(rowPlan, inserted)
                : InMemoryContentAuthoringStore.RequireTextPlanRowForTests(rowPlan, inserted));
        if (defect == "missing") await ApplyAsync(store, [ContentEdit.Update(Item, 1, Sword, [Value(2)])], ContentTextEdit.Set(Target("name", "en"), "Blade"));
        if (defect is "missing" or "freeze") await store.FreezeChangesAsync(1);
        Assert.Equal(2, (await store.CommitTextPublishAsync(plan, Request(1), null)).VersionNumber);
        Assert.Equal("Blade", Assert.Single((await store.ReadTextSnapshotAsync(2)).Revisions).Value);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnUnreadableVersionNamesItsObjectAndReaderToken(bool manifest)
    {
        using var files = new TemporaryCatalogDatabase();
        FileSystemPackStore pack = files.Pack();
        var store = await OpenAsync(pack);
        await store.ApplyEditsAsync([Add()], Actor, Operator, "");
        await store.PublishAsync(Request(0));
        ContentVersionRecord version = (await store.GetVersionAsync(1))!;
        ContentPublishBaseline baseline = await store.ReadPublishBaselineAsync();
        string hash = manifest ? version.ServerManifestHash
            : baseline.Chunks.First(chunk => chunk.Side == ContentVisibility.ServerOnly).Hash;
        ReadOnlyMemory<byte> bytes = (await pack.GetAsync(hash))!.Value;
        Assert.True(await pack.DeleteAsync(hash));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.LoadSnapshotAsync(1, TextRegistry()));
        Assert.Equal(ContentAuthoringRefusal.PackUnreadable, refused.Refusal);
        Assert.Equal(1, refused.VersionNumber);
        Assert.Equal(hash, refused.ObjectHash);
        Assert.Equal("chunk-fetch-failed", refused.PackReaderReason);
        await pack.PutAsync(hash, bytes);
        Assert.Single((await store.LoadSnapshotAsync(1, TextRegistry())).Rows(Item));
    }

    [Fact]
    public async Task ARowOnlyPlanMustKeepTheRecordedTextManifest()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        await PublishNamedRowAsync(store, "sword", "Sword");
        await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(2)])], Actor, Operator, "");
        ContentPublishBaseline held = await store.ReadPublishBaselineAsync();
        var stripped = new ContentPublishBaseline(1, held.Rows, held.Rules, held.Chunks, [],
            held.MinimumServerBuild, held.MinimumClientBuild);
        var publisher = new ContentPublisher(store, (IContentIdPersistence)store, TextRegistry());
        ContentPublishPlan plan = await publisher.PrepareAsync(Request(1), stripped);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.CommitPublishAsync(plan, Request(1), null));
        Assert.Equal(ContentAuthoringRefusal.TextManifestOnRowOnlyRoute, refused.Refusal);
        Assert.Equal("CommitPublishAsync", refused.MemberName);
        Assert.Equal(1, refused.VersionNumber);
        Assert.Equal(0, refused.ActualCount);
        Assert.Equal(1, refused.ExpectedCount);
        Assert.Equal("Sword", Assert.Single((await store.ReadTextSnapshotAsync(1)).Revisions).Value);
        Assert.Single(await store.ListVersionsAsync());
        await store.ClearDraftFreezeAsync();
        ContentPublishPlan repaired = await publisher.PrepareAsync(Request(1), held);
        Assert.Equal(2, (await store.CommitPublishAsync(repaired, Request(1), null)).VersionNumber);
        Assert.Equal("Sword", Assert.Single((await store.ReadTextSnapshotAsync(2)).Revisions).Value);
    }

    [Fact]
    public async Task AnAlreadyRecordedUpgradeLeavesTheDraftAndVersionUntouched()
    {
        var store = await OpenAsync();
        var stamp = new ContentUpgradeStamp("upgrade", 1);
        await ((IContentUpgradeLedger)store).RecordUpgradeAsync(stamp, ContentUpgradeDisposition.Baseline, Actor, Operator);
        await ApplyAsync(store, [Add()]);
        var publisher = new ContentPublisher(store, (IContentIdPersistence)store, TextRegistry());
        ContentPublishPlan plan = await publisher.PrepareAsync(Request(0), await store.ReadPublishBaselineAsync());
        var request = new ContentPublishRequest(Actor, Operator, "", 0) { Upgrade = stamp };
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            store.CommitPublishAsync(plan, request, null));
        Assert.Equal(ContentAuthoringRefusal.UpgradeAlreadyRecorded, refused.Refusal);
        Assert.Equal("upgrade", refused.UpgradeId);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Equal("sword", Assert.Single((await store.GetOpenDraftAsync())!.Changes.Edits).Key.ToString());
        Assert.Equal(ContentUpgradeDisposition.Baseline, Assert.Single(await ((IContentUpgradeLedger)store).ListUpgradesAsync()).Disposition);
        Assert.Equal(1, (await store.CommitPublishAsync(plan, Request(0), null)).VersionNumber);
    }

}
