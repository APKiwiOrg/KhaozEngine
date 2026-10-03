using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// The companion import's guards in the reference store: an open draft holding any work refuses it before
/// anything is staged, a failure after staging leaves the store empty and importable again, and the writer
/// refuses a format it does not write even when a text section is present.
/// </summary>
public sealed partial class TextBundleTests
{
    [Fact]
    public async Task An_open_draft_holding_any_work_refuses_a_companion_import_with_nothing_staged()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        ContentDraft pending = await ApplyAsync(text, new[] { Add("axe") }, ContentTextEdit.Set(Target(NameField, "fr", "axe"), "Hache"));
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => text.ImportTextBundleAsync(ImportSeed(), Actor, Operator, "seed"));

        Assert.Equal(ContentAuthoringException.DraftOpenReason, refused.Reason);
        ContentDraft held = (await store.GetOpenDraftAsync())!;
        Assert.Equal(pending.EditCount, held.EditCount);
        Assert.Equal("axe", held.Changes.Edits.Single().Key.ToString());
        Assert.True(pending.TextState!.IsSameAs(held.TextState));
        Assert.Equal(1, held.LanguageIntroductionCount);
        Assert.Empty(await store.ListVersionsAsync());
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Equal(0, (await store.ReadHighWaterAsync(Item)).ReservedThrough);
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [Fact]
    public async Task A_failure_after_staging_on_the_text_route_leaves_the_store_empty_and_importable()
    {
        using var files = new TemporaryCatalogDatabase();
        var pack = new RefusingPackStore(files.Pack()) { Armed = true };
        var store = TextStore(pack);
        IContentTextAuthoringStore text = store;

        await Assert.ThrowsAnyAsync<IOException>(() => text.ImportTextBundleAsync(ImportSeed(), Actor, Operator, "seed"));

        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Equal(0, (await store.ReadHighWaterAsync(Item)).ReservedThrough);

        pack.Armed = false;
        Assert.Equal(1, (await text.ImportTextBundleAsync(ImportSeed(), Actor, Operator, "retry")).VersionNumber);
        ContentVersionTextSnapshot snapshot = await text.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "en", "fr" }, snapshot.Languages.Select(language => language.WireTag));
        Assert.Equal(new[] { "Blade", "Sword" }, snapshot.Revisions.Select(revision => revision.Value).Order(StringComparer.Ordinal));
        Assert.Equal(16, Assert.Single(await store.ListFamiliesAsync(Item)).Blocks.Single().BaseId);
    }

    [Fact]
    public void The_writer_refuses_a_format_it_does_not_write_even_with_a_text_section()
    {
        var future = new ContentBundle(
            3, "seed", 0, Types(), [], Array.Empty<ContentFamily>(), Array.Empty<RemapRule>(), new ContentBundleTextState([], []));

        var refused = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Write(future));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
    }

    /// <summary>A seed carrying a family whose block holds one named row, beside a plain row and an empty language.</summary>
    static ContentBundle ImportSeed()
        => new(
            ContentBundle.TextFormatVersion,
            "seed",
            0,
            Types(),
            [Row("sword", 3, retired: false), new ContentBundleRow(Item, 16, new ContentKey("blade"), false, "swords", [TextAuthoringFixtures.Value(16)])],
            [new ContentFamily(7, Item, "swords", 16, false, 1, [new ContentFamilyBlock(7, 0, 16, 16, 17, 1)])],
            Array.Empty<RemapRule>(),
            new ContentBundleTextState(
                [Declare("en", "en"), Declare("fr", "fr")],
                [TextValue("sword", NameField, "en", "Sword"), TextValue("blade", NameField, "en", "Blade")]));

    /// <summary>A pack whose object writes fail while armed, so a publish fails after the import staged.</summary>
    sealed class RefusingPackStore(FileSystemPackStore inner) : IPackStore, IPackVersionPointerStore
    {
        /// <summary>Whether object writes fail.</summary>
        public bool Armed { get; set; }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
            => inner.GetAsync(hash, cancellationToken);

        /// <inheritdoc />
        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => Armed ? throw new IOException("pack write fault") : inner.PutAsync(hash, bytes, cancellationToken);

        /// <inheritdoc />
        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => inner.ListAsync(versionNumber, cancellationToken);

        /// <inheritdoc />
        public Task PutVersionPointerAsync(
            int versionNumber,
            string serverManifestHash,
            string clientManifestHash,
            CancellationToken cancellationToken = default)
            => inner.PutVersionPointerAsync(versionNumber, serverManifestHash, clientManifestHash, cancellationToken);

        /// <inheritdoc />
        public Task<PackVersionPointer?> GetVersionPointerAsync(
            int versionNumber,
            CancellationToken cancellationToken = default)
            => inner.GetVersionPointerAsync(versionNumber, cancellationToken);
    }
}
