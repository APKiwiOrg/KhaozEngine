using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The row-only rollback over versions committed before schema 4. Current and target must each be complete
/// and text free, or unknown and proved empty by the read-only proof, which records nothing. A version that
/// holds text, or is unknown with no proof, refuses before the rollback writes a draft or an audit row.
/// </summary>
public sealed class SqliteTextLegacyRollbackTests
{
    const string UnknownVersions = "SELECT COUNT(*) FROM catalog_version WHERE text_snapshot_complete IS NULL;";

    [Fact]
    public async Task A_migrated_text_free_catalog_rolls_back_to_a_pre_migration_version_and_publishes_complete()
    {
        using var database = new TemporaryCatalogDatabase();
        await SqliteTextSchemaMigrationTests.WriteMigratedTextFreeAsync(database);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        ContentDraft draft = await store.RollbackToAsync(1, Actor, Operator, "rollback");
        Assert.Equal(ContentEditOperation.Update, Assert.Single(draft.Changes.Edits).Operation);
        Assert.Equal(ContentAuditActions.Rollback, (await store.ListAuditAsync(default, 0, 0, 500))[0].Action);

        // The proof read both legacy versions and recorded nothing.
        Assert.Equal(2L, database.Scalar(UnknownVersions));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_text_chunk;"));

        Assert.Equal(3, (await store.PublishAsync(Request(2))).VersionNumber);
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 3;"));
        ContentVersionTextSnapshot published = await ((IContentTextAuthoringStore)store).ReadTextSnapshotAsync(3);
        Assert.Empty(published.Languages);
        Assert.Empty(published.Revisions);
        Assert.Equal(2L, database.Scalar(UnknownVersions));
    }

    [Fact]
    public async Task A_migrated_catalog_whose_legacy_versions_are_not_provable_refuses_rollback_with_nothing_written()
    {
        using var database = new TemporaryCatalogDatabase();
        await SqliteTextSchemaMigrationTests.WriteMigratedTextFreeAsync(database);

        // No stored manifest left, and a registry that gained a type no longer rebuilds the recorded ones.
        Directory.Delete(database.PackRoot, recursive: true);
        ContentTypeRegistry moved = TextRegistry();
        PublishFixtures.Register(moved, PublishFixtures.Other);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, moved, database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal(2L, database.Scalar(UnknownVersions));
    }

    [Fact]
    public async Task A_publish_landing_between_the_legacy_rollback_plan_and_its_apply_refuses_with_nothing_written()
    {
        using var database = new TemporaryCatalogDatabase();
        await SqliteTextSchemaMigrationTests.WriteMigratedTextFreeAsync(database);
        using var rival = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await rival.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        // The plan proves the legacy current version empty through the pack after it read the active version.
        // The rival publishes from its own connection inside that first pack read, so it lands after the plan
        // fixed its starting version and before the transaction that would apply the rollback edits.
        int audits = -1;
        var pack = new LandingPackStore(database.Pack(), async () =>
        {
            await RepriceAndPublishAsync(rival);
            audits = await AuditCountAsync(rival);
        });
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), pack);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.True(pack.Landed);
        Assert.Equal(ContentAuthoringException.BaseVersionMovedReason, refused.Reason);
        Assert.Equal(3, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal(1L, database.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 3;"));
        Assert.Equal(2L, database.Scalar(UnknownVersions));
    }

    [Fact]
    public async Task A_complete_target_holding_text_still_refuses_rollback_with_nothing_written()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await ApplyAsync(store, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await store.PublishAsync(Request(0));
        await RepriceAndPublishAsync(store);
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
    }

    [Fact]
    public async Task A_legacy_target_whose_stored_manifests_name_a_language_refuses_rollback_with_nothing_written()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var seeded = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack()))
        {
            await seeded.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await ApplyAsync(seeded, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
            await seeded.PublishAsync(Request(0));
            await RepriceAndPublishAsync(seeded);
        }

        database.Execute(SqliteTextSchemaMigrationTests.UndoVersionFour);
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.RollbackToAsync(1, Actor, Operator, "rollback"));
        Assert.Equal(ContentAuthoringException.TextProvenanceUnknownReason, refused.Reason);
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Equal(audits, await AuditCountAsync(store));
        Assert.Equal(2L, database.Scalar(UnknownVersions));
    }

    /// <summary>A row-only publish that changes the sword's value, so the next version differs from the first.</summary>
    static async Task RepriceAndPublishAsync(SqliteContentAuthoringStore store)
    {
        int active = await store.GetActiveVersionAsync();
        int sword = (await store.ListRowsAsync(Item, 0, null, true, 0, 10)).Rows.Single().Id;
        await store.ApplyEditsAsync(
            new[] { ContentEdit.Update(Item, sword, Sword, new[] { Value(7) }) }, Actor, Operator, "reprice");
        await store.PublishAsync(Request(active));
    }

    /// <summary>A pack whose first object read runs one callback to completion before it answers.</summary>
    sealed class LandingPackStore(IPackStore inner, Func<Task> land) : IPackStore
    {
        Func<Task>? _land = land;

        /// <summary>Whether the callback ran.</summary>
        public bool Landed { get; private set; }

        /// <inheritdoc />
        public Task<bool> ExistsAsync(string hash, CancellationToken cancellationToken = default)
            => inner.ExistsAsync(hash, cancellationToken);

        /// <inheritdoc />
        public async Task<ReadOnlyMemory<byte>?> GetAsync(string hash, CancellationToken cancellationToken = default)
        {
            if (_land is Func<Task> once)
            {
                _land = null;
                await once();
                Landed = true;
            }

            return await inner.GetAsync(hash, cancellationToken);
        }

        /// <inheritdoc />
        public Task PutAsync(string hash, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
            => inner.PutAsync(hash, bytes, cancellationToken);

        /// <inheritdoc />
        public IAsyncEnumerable<string> ListAsync(int versionNumber, CancellationToken cancellationToken = default)
            => inner.ListAsync(versionNumber, cancellationToken);
    }
}
