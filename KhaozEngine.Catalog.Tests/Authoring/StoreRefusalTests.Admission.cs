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

public abstract partial class StoreRefusalTests
{
    [Theory]
    [InlineData(ContentAuthoringRefusal.TypeUnknown)]
    [InlineData(ContentAuthoringRefusal.FamilyUnknown)]
    [InlineData(ContentAuthoringRefusal.NoPendingDraft)]
    [InlineData(ContentAuthoringRefusal.BaseVersionMoved)]
    [InlineData(ContentAuthoringRefusal.PublishInProgress)]
    [InlineData(ContentAuthoringRefusal.TextBindingRowUnknown)]
    [InlineData(ContentAuthoringRefusal.TextLanguageUndeclared)]
    [InlineData(ContentAuthoringRefusal.VersionUnknown)]
    [InlineData(ContentAuthoringRefusal.EditTargetOperationCollision)]
    public async Task AdmissionRefusalsKeepStoredContentUntilTheInputIsRepaired(ContentAuthoringRefusal reason)
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        var unknown = new ContentTypeId(1025);
        if (reason == ContentAuthoringRefusal.EditTargetOperationCollision)
        {
            await store.ApplyEditsAsync([Add()], Actor, Operator, "");
            await store.PublishAsync(Request(0));
            await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(2)])], Actor, Operator, "");
        }
        if (reason is ContentAuthoringRefusal.BaseVersionMoved or ContentAuthoringRefusal.PublishInProgress
            or ContentAuthoringRefusal.TextLanguageUndeclared)
            await store.ApplyEditsAsync([Add()], Actor, Operator, "");
        if (reason == ContentAuthoringRefusal.PublishInProgress) await store.FreezeChangesAsync(0);
        int active = await store.GetActiveVersionAsync();
        ContentDraft? before = await store.GetOpenDraftAsync();
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        var marks = await ((IContentIdPersistence)store).ReadHighWaterAsync(Item);
        string? bundle = active == 0 ? null : ContentBundleJson.Write(await store.ExportBundleAsync(active));
        Task Refuse() => reason switch
        {
            ContentAuthoringRefusal.TypeUnknown => store.CreateFamilyAsync(unknown, "weapons", 16, Actor, Operator),
            ContentAuthoringRefusal.FamilyUnknown => ((IContentIdPersistence)store).CommitFamilyIssueAsync(999, 0),
            ContentAuthoringRefusal.NoPendingDraft => store.FreezeChangesAsync(0),
            ContentAuthoringRefusal.BaseVersionMoved => store.FreezeChangesAsync(1),
            ContentAuthoringRefusal.PublishInProgress => store.ApplyEditsAsync([Add(value: 2)], Actor, Operator, ""),
            ContentAuthoringRefusal.TextBindingRowUnknown => ApplyAsync(store, [Add("shield")], ContentTextEdit.Set(Target("name", "en"), "Sword")),
            ContentAuthoringRefusal.TextLanguageUndeclared => ApplyAsync(store, [Add("shield")], ContentTextEdit.Remove(Target("name", "en"))),
            ContentAuthoringRefusal.VersionUnknown => store.ReadTextSnapshotAsync(1),
            ContentAuthoringRefusal.EditTargetOperationCollision => store.ApplyEditsAsync(
                [Add("shield"), ContentEdit.Retire(Item, 1, Sword, ContentRetirePolicy.Placeholder, 0)], Actor, Operator, ""),
            _ => throw new InvalidOperationException(),
        };
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(Refuse);
        Assert.Equal(reason, refused.Refusal);
        switch (reason)
        {
            case ContentAuthoringRefusal.TypeUnknown: Assert.Equal(unknown, refused.Type); break;
            case ContentAuthoringRefusal.FamilyUnknown: Assert.Equal(999, refused.FamilyId); break;
            case ContentAuthoringRefusal.BaseVersionMoved:
                Assert.Equal(1, refused.ExpectedBaseVersion); Assert.Equal(0, refused.ActualBaseVersion); break;
            case ContentAuthoringRefusal.PublishInProgress:
                Assert.Equal("ApplyEditsAsync", refused.MemberName); Assert.Equal(0, refused.FrozenForBaseVersion); break;
            case ContentAuthoringRefusal.TextBindingRowUnknown:
                Assert.Equal(Item, refused.Type); Assert.Equal(0, refused.Id); Assert.Equal("sword", refused.RowKey); break;
            case ContentAuthoringRefusal.TextLanguageUndeclared:
                Assert.Equal(Item, refused.Type); Assert.Equal(0, refused.Id);
                Assert.Equal("name", refused.FieldName); Assert.Equal("en", refused.Language); break;
            case ContentAuthoringRefusal.VersionUnknown: Assert.Equal(1, refused.VersionNumber); break;
            case ContentAuthoringRefusal.EditTargetOperationCollision:
                Assert.Equal(Item, refused.Type); Assert.Equal(1, refused.Id); Assert.Equal("sword", refused.RowKey);
                Assert.Equal(ContentEditOperation.Update, refused.StandingOperation);
                Assert.Equal(ContentEditOperation.Retire, refused.RequestedOperation); break;
        }
        ContentDraft? after = await store.GetOpenDraftAsync();
        if (before is null) Assert.Null(after);
        else Assert.True(ContentTextCompatibility.SameDraft(before, after!));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        Assert.Equal(marks, await ((IContentIdPersistence)store).ReadHighWaterAsync(Item));
        if (bundle is not null) Assert.Equal(bundle, ContentBundleJson.Write(await store.ExportBundleAsync(active)));
        switch (reason)
        {
            case ContentAuthoringRefusal.TypeUnknown:
            case ContentAuthoringRefusal.FamilyUnknown:
                ContentFamily family = await store.CreateFamilyAsync(Item, "weapons", 16, Actor, Operator);
                Assert.True(family.Contains(await store.AllocateInFamilyAsync(family.FamilyId))); break;
            case ContentAuthoringRefusal.NoPendingDraft:
            case ContentAuthoringRefusal.BaseVersionMoved:
            case ContentAuthoringRefusal.PublishInProgress:
                await store.ClearDraftFreezeAsync();
                await store.ApplyEditsAsync([Add(value: 2)], Actor, Operator, "");
                Assert.Equal(0, (await store.FreezeChangesAsync(0)).BaseVersion); break;
            case ContentAuthoringRefusal.TextBindingRowUnknown:
            case ContentAuthoringRefusal.TextLanguageUndeclared:
                ContentDraft repaired = await ApplyAsync(store,
                    reason == ContentAuthoringRefusal.TextBindingRowUnknown ? [Add()] : null,
                    ContentTextEdit.Set(Target("name", "en"), "Sword"));
                Assert.Equal("Sword", Assert.Single(repaired.TextState!.Edits).Value); break;
            case ContentAuthoringRefusal.VersionUnknown:
                await store.ApplyEditsAsync([Add()], Actor, Operator, "");
                await store.PublishAsync(Request(0));
                Assert.Equal(1, (await store.ReadTextSnapshotAsync(1)).VersionNumber); break;
            case ContentAuthoringRefusal.EditTargetOperationCollision:
                ContentDraft changed = await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(3)])], Actor, Operator, "");
                Assert.Equal(3, Assert.Single(Assert.Single(changed.Changes.Edits).Fields).Value.Number); break;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowCommitChecksCandidateFindingsAndTheNextVersionBeforeWriting(bool invalid)
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        await store.ApplyEditsAsync(invalid
            ? [ContentEdit.Import(Item, 1, Sword, [Value(1)]), ContentEdit.Import(Item, 2, Sword, [Value(2)])]
            : [Add()], Actor, Operator, "");
        var publisher = new ContentPublisher(store, (IContentIdPersistence)store, TextRegistry());
        ContentPublishPlan plan = await publisher.PrepareAsync(Request(0), await store.ReadPublishBaselineAsync());
        if (!invalid) await store.CommitPublishAsync(plan, Request(0), null);
        ContentDraft? draft = await store.GetOpenDraftAsync();
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        string? bundle = invalid ? null : ContentBundleJson.Write(await store.ExportBundleAsync(1));
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.CommitPublishAsync(plan, Request(0), null));
        Assert.Equal(invalid ? ContentAuthoringRefusal.CandidateRejected : ContentAuthoringRefusal.NextVersionMismatch, refused.Refusal);
        if (invalid) Assert.Equal(new[] { "KEC0000", "KEC0002" },
            refused.Findings.Select(finding => finding.Code).OrderBy(code => code, StringComparer.Ordinal));
        else
        {
            Assert.Equal(1, refused.RequestedVersion); Assert.Equal(1, refused.HighestVersion); Assert.Equal(2, refused.NextVersion);
            Assert.Equal(bundle, ContentBundleJson.Write(await store.ExportBundleAsync(1)));
        }
        if (draft is null) Assert.Null(await store.GetOpenDraftAsync());
        else Assert.True(ContentTextCompatibility.SameDraft(draft, (await store.GetOpenDraftAsync())!));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        await store.ClearDraftFreezeAsync();
        if (invalid)
        {
            await store.DiscardDraftAsync(Actor, Operator);
            await store.ApplyEditsAsync([Add()], Actor, Operator, "");
        }
        else await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(3)])], Actor, Operator, "");
        int active = invalid ? 0 : 1;
        ContentPublishPlan repaired = await publisher.PrepareAsync(Request(active), await store.ReadPublishBaselineAsync());
        Assert.Equal(active + 1, (await store.CommitPublishAsync(repaired, Request(active), null)).VersionNumber);
    }

    [Fact]
    public async Task PackTargetIsRequiredBeforePublishingAndAnEmptyCatalogIsRequiredBeforeImporting()
    {
        var store = await OpenAsync();
        await store.ApplyEditsAsync([Add()], Actor, Operator, "");
        ContentDraft draft = (await store.GetOpenDraftAsync())!;
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        ContentAuthoringException missing = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.PublishAsync(Request(0)));
        Assert.Equal(ContentAuthoringRefusal.PackStoreMissing, missing.Refusal);
        Assert.Equal("PublishAsync", missing.MemberName);
        Assert.True(ContentTextCompatibility.SameDraft(draft, (await store.GetOpenDraftAsync())!));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        using var files = new TemporaryCatalogDatabase();
        var repaired = await OpenAsync(files.Pack());
        await repaired.ApplyEditsAsync([Add()], Actor, Operator, "");
        Assert.Equal(1, (await repaired.PublishAsync(Request(0))).VersionNumber);
        ContentBundle bundle = await repaired.ExportBundleAsync(1);
        string before = ContentBundleJson.Write(bundle);
        audit = await repaired.ListAuditAsync(default, 0, 0, 500);
        ContentAuthoringException nonempty = await Assert.ThrowsAsync<ContentAuthoringException>(() => repaired.ImportBundleAsync(bundle, Actor, Operator, ""));
        Assert.Equal(ContentAuthoringRefusal.CatalogNotEmpty, nonempty.Refusal);
        Assert.Equal(1, nonempty.ActiveVersion);
        Assert.Equal(before, ContentBundleJson.Write(await repaired.ExportBundleAsync(1)));
        Assert.Equal(audit, await repaired.ListAuditAsync(default, 0, 0, 500));
        using var emptyFiles = new TemporaryCatalogDatabase();
        using var empty = new SqliteContentAuthoringStore(emptyFiles.ConnectionString, TextRegistry(), emptyFiles.Pack());
        await empty.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        Assert.Equal(1, (await empty.ImportBundleAsync(bundle, Actor, Operator, "")).VersionNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RowOnlyForkAndRollbackKeepTheTextTheyCannotRepresent(bool fork)
    {
        using var files = new TemporaryCatalogDatabase();
        var store = await OpenAsync(files.Pack());
        await PublishNamedRowAsync(store, "sword", "Sword");
        if (fork) await store.ApplyEditsAsync([ContentEdit.Fork(Item, 1, Sword, new("copy"), "legacy", [])], Actor, Operator, "");
        string before = ContentBundleJson.Write(await store.ExportBundleAsync(1));
        ContentDraft? draft = await store.GetOpenDraftAsync();
        var audit = await store.ListAuditAsync(default, 0, 0, 500);
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => fork
            ? store.FreezeDraftAsync(1) : (Task)store.RollbackToAsync(1, Actor, Operator, ""));
        Assert.Equal(fork ? ContentAuthoringRefusal.TextForkOnRowOnlyRoute : ContentAuthoringRefusal.TextBaseOnRowOnlyRoute, refused.Refusal);
        Assert.Equal(fork ? "FreezeDraftAsync" : "RollbackToAsync", refused.MemberName);
        if (fork) { Assert.Equal(Item, refused.TextRowType); Assert.Equal(1, refused.TextRowId); }
        else { Assert.Equal(1, refused.VersionNumber); Assert.Equal(1, refused.RecordedLanguageCount); Assert.Equal(1, refused.RecordedValueCount); }
        Assert.Equal(before, ContentBundleJson.Write(await store.ExportBundleAsync(1)));
        if (draft is null) Assert.Null(await store.GetOpenDraftAsync());
        else Assert.True(ContentTextCompatibility.SameDraft(draft, (await store.GetOpenDraftAsync())!));
        Assert.Equal(audit, await store.ListAuditAsync(default, 0, 0, 500));
        if (fork)
        {
            await store.DiscardDraftAsync(Actor, Operator);
            await store.ApplyEditsAsync([ContentEdit.Update(Item, 1, Sword, [Value(2)])], Actor, Operator, "");
            await store.FreezeDraftAsync(1);
            Assert.True((await store.GetOpenDraftAsync())!.IsFrozen);
        }
        else Assert.NotNull(await store.RollbackTextToAsync(1, Actor, Operator, ""));
    }
}
