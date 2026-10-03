using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The complete bundle, export and rollback facts of the text conformance suite, which every store runs
/// positive: the companion import, the format-aware export of one exact version and the companion rollback.
/// </summary>
public abstract partial class ContentAuthoringTextStoreConformance
{
    [Fact]
    public virtual async Task Text13_CompanionImportLandsACompleteBundleAndRefusesBeforeStaging()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        ContentBundle bundle = CompleteBundle();

        var lost = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportTextBundleAsync(RowOnly(bundle), Actor, Operator, "import"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, lost.Reason);
        var rowOnly = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(bundle, Actor, Operator, "import"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rowOnly.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Empty(await AuditAsync(store));

        ContentPublishResult imported = await store.ImportTextBundleAsync(bundle, Actor, Operator, "import");

        Assert.Equal(1, imported.VersionNumber);
        ContentVersionTextSnapshot text = await store.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "en-US", "fr" }, text.Languages.Select(language => language.WireTag));
        Assert.Equal(Hash("fr"), text.Languages.Single(language => language.Language == "fr").Hash);
        Assert.Equal(
            Hash("en-US", ("item.shield.name", "Old Shield"), ("item.sword.name", "Sword")),
            text.Languages.Single(language => language.Language == "en-us").Hash);
        Assert.Equal(2, text.Revisions.Count);
        Assert.True((await store.ListRowsAsync(Item, 1, "shield", true, 0, 1)).Rows.Single().IsRetired);
        Assert.Equal(new[] { 3, 4 }, (await store.ListRowsAsync(Item, 1, null, true, 0, 10)).Rows.Select(row => row.Id));

        ContentBundle exported = await store.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.TextFormatVersion, exported.FormatVersion);
        Assert.Equal(new[] { "en-US", "fr" }, exported.TextState!.Languages.Select(language => language.WireTag));
        Assert.Equal(
            bundle.TextState!.Values.OrderBy(value => value.Target.Key.ToString(), StringComparer.Ordinal),
            exported.TextState.Values.OrderBy(value => value.Target.Key.ToString(), StringComparer.Ordinal));

        var again = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportTextBundleAsync(bundle, Actor, Operator, "again"));
        Assert.Equal(ContentAuthoringException.CatalogNotEmptyReason, again.Reason);
        Assert.Equal(1, await store.GetActiveVersionAsync());
    }

    [Fact]
    public virtual async Task Text15_CompanionRollbackRestoresValuesAndKeepsEveryDeclaredLanguage()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await PublishTwoTextVersionsAsync(store);
        var legacy = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, legacy.Reason);

        ContentDraft draft = await store.RollbackTextToAsync(1, Actor, Operator, "rollback");

        Assert.Equal(0, draft.EditCount);
        Assert.Equal(
            new[] { (ContentTextEditOperation.Set, "en", (string?)"Sword"), (ContentTextEditOperation.Remove, "fr", null) },
            draft.TextState!.Edits.Select(edit => (edit.Operation, edit.Target.Language, edit.Value)));
        await store.PublishAsync(Request(2));
        ContentVersionTextSnapshot restored = await store.ReadTextSnapshotAsync(3);
        Assert.Equal("Sword", restored.Revisions.Single().Value);
        Assert.Equal(new[] { "en", "fr" }, restored.Languages.Select(language => language.WireTag));
        Assert.Equal(Hash("fr"), restored.Languages.Single(language => language.Language == "fr").Hash);
        Assert.Equal(2, (await store.ReadTextSnapshotAsync(2)).Revisions.Count);
    }

    [Fact]
    public virtual async Task Text16_ExportWritesFormatTwoExactlyWhenTheVersionDeclaresALanguage()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await store.ApplyEditsAsync(new[] { Add("sword") }, Actor, Operator, "rows");
        await store.PublishAsync(Request(0));
        await ApplyAsync(store, null, Set("sword", NameField, "en", "Sword"));
        await store.PublishAsync(Request(1));

        ContentBundle textFree = await store.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.CurrentFormatVersion, textFree.FormatVersion);
        Assert.Null(textFree.TextState);
        ContentBundle named = await store.ExportBundleAsync(2);
        Assert.Equal(ContentBundle.TextFormatVersion, named.FormatVersion);
        ContentBundleTextValue value = Assert.Single(named.TextState!.Values);
        Assert.Equal(("sword", NameField, "en", "Sword"), (value.Target.Key.ToString(), value.Target.FieldName, value.Target.Language, value.Value));
        Assert.Equal(named.TextState.Values, ContentBundleJson.Read(ContentBundleJson.Write(named)).TextState!.Values);
    }

    [Fact]
    public virtual async Task Text17_ARowOnlyImportOverADraftHoldingTextIsRefusedBeforeStaging()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await ApplyAsync(store, new[] { Add("stone_sword") }, Set("stone_sword", NameField, "en", "Stone Sword"));
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        ContentIdHighWater mark = await ((IContentIdPersistence)store).ReadHighWaterAsync(Item);
        int audits = (await AuditAsync(store)).Count;

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportBundleAsync(RowSeed(), Actor, Operator, "import"));

        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Empty(await store.ListFamiliesAsync(default));
        Assert.Equal(mark, await ((IContentIdPersistence)store).ReadHighWaterAsync(Item));
        Assert.Empty(await PackObjectsAsync(store));
        Assert.Equal(audits, (await AuditAsync(store)).Count);
        ContentDraft after = (await store.GetOpenDraftAsync())!;
        Assert.True(held.TextState!.IsSameAs(after.TextState));
        Assert.Equal((held.BaseVersion, held.OpenedBy, held.OpenedAtUtc, held.Note), (after.BaseVersion, after.OpenedBy, after.OpenedAtUtc, after.Note));
        Assert.Equal("stone_sword", Assert.Single(after.Changes.Edits).Key.ToString());
        Assert.Equal("Stone Sword", Assert.Single(after.TextState!.Edits).Value);
        Assert.Equal("en", Assert.Single(after.TextState.Introductions).Language);
    }

    [Fact]
    public virtual async Task Text18_ARowOnlyImportOverADraftHoldingOnlyRowsIsNotATextRefusal()
    {
        IContentTextAuthoringStore store = await OpenAsync();
        await store.ApplyEditsAsync(new[] { Add("axe") }, Actor, Operator, "rows");

        // Only the text refusal is pinned here. What the import does with the draft's pending rows is #1251.
        ContentPublishResult imported = await store.ImportBundleAsync(RowSeed(), Actor, Operator, "import");

        Assert.Equal(1, imported.VersionNumber);
        Assert.Equal(3, (await store.ListRowsAsync(Item, 1, "sword", true, 0, 1)).Rows.Single().Id);
    }

    /// <summary>Every object the store's pack target actually holds.</summary>
    async Task<List<string>> PackObjectsAsync(IContentTextAuthoringStore store)
    {
        var held = new List<string>();
        await foreach (string hash in ((IPackStorePruning)PackOf(store)).EnumerateAsync())
        {
            held.Add(hash);
        }

        return held;
    }

    /// <summary>A valid format 1 seed: the complete bundle's live sword row with its carried id and no text.</summary>
    static ContentBundle RowSeed()
    {
        ContentBundle complete = CompleteBundle();
        return new ContentBundle(
            ContentBundle.CurrentFormatVersion, "seed", 0, complete.Types, complete.Rows.Take(1).ToArray(), complete.Families, complete.Rules);
    }

    /// <summary>Version 1 names the sword in English, and version 2 renames it and adds French.</summary>
    static async Task PublishTwoTextVersionsAsync(IContentTextAuthoringStore store)
    {
        await ApplyAsync(store, new[] { Add("sword") }, Set("sword", NameField, "en", "Sword"));
        await store.PublishAsync(Request(0));
        await ApplyAsync(store, null, Set("sword", NameField, "en", "Blade"), Set("sword", NameField, "fr", "Lame"));
        await store.PublishAsync(Request(1));
    }

    /// <summary>
    /// A format 2 seed: a live row and a retired one with carried ids, a historical <c>en-US</c> spelling, an
    /// empty French declaration, and one value per row, the retired row's included.
    /// </summary>
    static ContentBundle CompleteBundle()
    {
        ContentBundleType[] types = TextRegistry().ByTypeId
            .Select(registration => new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema))
            .ToArray();
        ContentBundleRow[] rows =
        [
            new(Item, 3, new ContentKey("sword"), false, null, Value(3)),
            new(Item, 4, new ContentKey("shield"), true, null, Value(4)),
        ];
        var text = new ContentBundleTextState(
            [new ContentTextLanguageDeclaration("en-us", "en-US"), new ContentTextLanguageDeclaration("fr", "fr")],
            [
                new ContentBundleTextValue(new ContentTextTarget(Item, new ContentKey("sword"), NameField, "en-US"), "Sword"),
                new ContentBundleTextValue(new ContentTextTarget(Item, new ContentKey("shield"), NameField, "en-us"), "Old Shield"),
            ]);
        return new ContentBundle(
            ContentBundle.TextFormatVersion, "seed", 0, types, rows, Array.Empty<ContentFamily>(), Array.Empty<RemapRule>(), text);
    }

    /// <summary>The same bundle through the OLD row-only constructor, which is how a wrapper loses its text.</summary>
    static ContentBundle RowOnly(ContentBundle bundle)
        => new(bundle.FormatVersion, bundle.StoreEpoch, bundle.SourceVersion, bundle.Types, bundle.Rows, bundle.Families, bundle.Rules);
}
