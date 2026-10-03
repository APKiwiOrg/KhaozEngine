using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// Bundle format 2 through the SQLite store over FILE databases a test reopens: the companion import lands
/// rows, every declared language with its wire spelling and every value as one complete version 1, the export
/// writes format 1 exactly when the version declares no language, refusals land before anything is staged,
/// and a failure after staging leaves the database empty and importable again.
/// </summary>
public sealed partial class SqliteTextBundleTests
{
    const string SwordName = "item.sword.name";
    const string ShieldName = "item.shield.name";

    const string FormatOneFixture = """
        {
          "formatVersion": 1,
          "storeEpoch": "seed",
          "sourceVersion": 0,
          "types": [
            {
              "typeId": 1024, "typeKey": "item", "defaultVisibility": 0, "chunkSlots": 256, "maxDefinitionId": null,
              "fields": [
                { "name": "value", "kind": 0, "referenceTarget": null, "visibility": 0, "required": true, "scale": 1 },
                { "name": "legacy", "kind": 2, "referenceTarget": null, "visibility": 0, "required": false, "scale": 1 },
                { "name": "name", "kind": 5, "referenceTarget": null, "visibility": 0, "required": false, "scale": 1 },
                { "name": "description", "kind": 5, "referenceTarget": null, "visibility": 0, "required": false, "scale": 1 },
                { "name": "secret_name", "kind": 5, "referenceTarget": null, "visibility": 1, "required": false, "scale": 1 },
              ],
            },
          ],
          "families": [],
          "rows": [
            { "typeId": 1024, "id": 5, "key": "sword", "retired": false, "familyKey": null,
              "fields": [ { "name": "value", "kind": 0, "number": 7 } ] },
          ],
          "rules": [],
        }
        """;

    /// <summary>Two declarations whose languages share one canonical identity.</summary>
    const string AliasDocument = """
        {
          "formatVersion": 2, "storeEpoch": "seed", "sourceVersion": 0,
          "types": [], "families": [], "rows": [], "rules": [],
          "text": {
            "languages": [ { "language": "en-us", "wireTag": "en-US" }, { "language": "en-us", "wireTag": "en-us" } ],
            "values": []
          }
        }
        """;

    /// <summary>A value carrying a lone surrogate, which is not valid text.</summary>
    const string InvalidTextDocument = """
        {
          "formatVersion": 2, "storeEpoch": "seed", "sourceVersion": 0,
          "types": [], "families": [], "rows": [], "rules": [],
          "text": {
            "languages": [ { "language": "en", "wireTag": "en" } ],
            "values": [ { "typeId": 1024, "key": "sword", "field": "name", "language": "en", "value": "\ud800" } ]
          }
        }
        """;

    static async Task<SqliteContentAuthoringStore> OpenAsync(TemporaryCatalogDatabase database)
    {
        var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    [Fact]
    public async Task A_complete_bundle_round_trips_retired_rows_historical_spelling_and_empty_languages()
    {
        using var source = new TemporaryCatalogDatabase();
        ContentBundle seed = Seed(
            new[] { Row("sword", 3, retired: false), Row("shield", 4, retired: true) },
            new[] { Declare("en-us", "en-US"), Declare("fr", "fr"), Declare("de", "de") },
            new[]
            {
                TextValue("sword", NameField, "en-US", "Sword"),
                TextValue("shield", NameField, "en-us", "Old Shield"),
                TextValue("shield", DescriptionField, "de", "Alter Schild"),
            });
        using (SqliteContentAuthoringStore store = await OpenAsync(source))
        {
            Assert.Equal(1, (await store.ImportTextBundleAsync(seed, Actor, Operator, "seed")).VersionNumber);
        }

        using SqliteContentAuthoringStore reopened = await OpenAsync(source);
        ContentVersionTextSnapshot snapshot = await reopened.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "de", "en-US", "fr" }, snapshot.Languages.Select(language => language.WireTag));
        Assert.Equal(Hash("fr"), snapshot.Languages.Single(language => language.Language == "fr").Hash);
        Assert.Equal(
            Hash("en-US", (ShieldName, "Old Shield"), (SwordName, "Sword")),
            snapshot.Languages.Single(language => language.Language == "en-us").Hash);
        Assert.Equal(3, snapshot.Revisions.Count);
        Assert.True((await reopened.ListRowsAsync(Item, 1, "shield", true, 0, 1)).Rows.Single().IsRetired);
        Assert.Equal(1L, source.Scalar("SELECT text_snapshot_complete FROM catalog_version WHERE version_number = 1;"));
        Assert.Equal(0L, source.Scalar(
            "SELECT (SELECT COUNT(*) FROM catalog_draft_text_edit) + (SELECT COUNT(*) FROM catalog_draft_text_language);"));
        Assert.Equal(
            new[] { "de", "en-us", "en-us" },
            (await reopened.ListAuditAsync(default, 0, 0, 500))
                .Where(entry => entry.Action == ContentAuditActions.DraftEdit && entry.LanguageTag is not null)
                .Select(entry => entry.LanguageTag)
                .Order(StringComparer.Ordinal));

        ContentBundle exported = await reopened.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.TextFormatVersion, exported.FormatVersion);
        Assert.Equal(new[] { Declare("de", "de"), Declare("en-us", "en-US"), Declare("fr", "fr") }, exported.TextState!.Languages);
        Assert.Equal(seed.TextState!.Values.OrderBy(Describe), exported.TextState.Values.OrderBy(Describe));

        string json = ContentBundleJson.Write(exported);
        using var destination = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore adopted = await OpenAsync(destination);
        await adopted.ImportTextBundleAsync(ContentBundleJson.Read(json), Actor, Operator, "adopt");
        ContentBundle again = await adopted.ExportBundleAsync(1);
        Assert.Equal(WithoutEpoch(json, exported), WithoutEpoch(ContentBundleJson.Write(again), again));
        Assert.Equal(snapshot.Languages, (await adopted.ReadTextSnapshotAsync(1)).Languages);
    }

    [Fact]
    public async Task A_text_free_version_exports_format_one_byte_identically_and_a_declaring_one_format_two()
    {
        using var companionFiles = new TemporaryCatalogDatabase();
        using var rowOnlyFiles = new TemporaryCatalogDatabase();
        ContentBundle fixture = ContentBundleJson.Read(FormatOneFixture);
        using SqliteContentAuthoringStore companion = await OpenAsync(companionFiles);
        await companion.ImportTextBundleAsync(fixture, Actor, Operator, "seed");
        using SqliteContentAuthoringStore rowOnly = await OpenAsync(rowOnlyFiles);
        await rowOnly.ImportBundleAsync(fixture, Actor, Operator, "seed");

        ContentVersionTextSnapshot empty = await companion.ReadTextSnapshotAsync(1);
        Assert.Empty(empty.Languages);
        Assert.Empty(empty.Revisions);
        ContentBundle textFree = await companion.ExportBundleAsync(1);
        ContentBundle fromRowOnly = await rowOnly.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.CurrentFormatVersion, textFree.FormatVersion);
        Assert.Null(textFree.TextState);
        string written = ContentBundleJson.Write(textFree);
        Assert.DoesNotContain("\"text\"", written, StringComparison.Ordinal);
        Assert.Equal(WithoutEpoch(ContentBundleJson.Write(fromRowOnly), fromRowOnly), WithoutEpoch(written, textFree));
        Assert.Equal(
            written,
            ContentBundleJson.Write(new ContentBundle(
                1, textFree.StoreEpoch, 1, textFree.Types, textFree.Rows, textFree.Families, textFree.Rules)));

        await ApplyAsync(companion, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        await ApplyAsync(companion, null, ContentTextEdit.Remove(Target(NameField, "fr")));
        await companion.PublishAsync(Request(1));

        ContentBundle declaring = await companion.ExportBundleAsync(2);
        Assert.Equal(ContentBundle.TextFormatVersion, declaring.FormatVersion);
        Assert.Equal(new[] { Declare("fr", "fr") }, declaring.TextState!.Languages);
        Assert.Empty(declaring.TextState.Values);
        Assert.Equal(written, ContentBundleJson.Write(await companion.ExportBundleAsync(1)));
    }

    [Fact]
    public async Task Refusals_land_before_anything_is_reset_or_staged()
    {
        using var emptyFiles = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore empty = await OpenAsync(emptyFiles);
        ContentBundle ghost = Seed(
            new[] { Row("sword", 3, retired: false) }, new[] { Declare("en", "en") }, new[] { TextValue("ghost", NameField, "en", "Nobody") });
        var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(() => empty.ImportTextBundleAsync(ghost, Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.UnknownRowReason, unknown.Reason);
        ContentBundle hidden = Seed(
            new[] { Row("sword", 3, retired: false) }, new[] { Declare("en", "en") }, new[] { TextValue("sword", SecretField, "en", "Hidden") });
        var ineligible = await Assert.ThrowsAsync<ContentAuthoringException>(() => empty.ImportTextBundleAsync(hidden, Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, ineligible.Reason);
        var future = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => empty.ImportTextBundleAsync(Rebuild(Seed([], [], []), 3), Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, future.Reason);
        Assert.Empty(await empty.ListVersionsAsync());
        Assert.Equal(0, await AuditCountAsync(empty));
        Assert.Equal(0, (await empty.ReadHighWaterAsync(Item)).ReservedThrough);
        Assert.Null(await empty.GetOpenDraftAsync());

        using var liveFiles = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore live = await OpenAsync(liveFiles);
        await ApplyAsync(live, new[] { Add() }, ContentTextEdit.Set(Target(NameField, "en"), "Sword"));
        await live.PublishAsync(Request(0));
        int audits = await AuditCountAsync(live);
        ContentBundle lost = Rebuild(await live.ExportBundleAsync(1), ContentBundle.TextFormatVersion);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => live.ImportTextBundleAsync(lost, Actor, Operator, "again"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        var rowOnly = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => live.ImportBundleAsync(lost, Actor, Operator, "again"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rowOnly.Reason);
        foreach (string document in new[] { AliasDocument, InvalidTextDocument })
        {
            var malformed = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Read(document));
            Assert.Equal(ContentAuthoringException.BundleFormatReason, malformed.Reason);
        }

        Assert.Equal(1, await live.GetActiveVersionAsync());
        Assert.Equal(audits, await AuditCountAsync(live));
        Assert.Equal("Sword", (await live.ReadTextSnapshotAsync(1)).Revisions.Single().Value);
        Assert.NotEqual(0, (await live.ReadHighWaterAsync(Item)).ReservedThrough);
    }

    [Fact]
    public async Task An_open_draft_holding_any_work_refuses_a_companion_import_with_nothing_staged()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        ContentDraft pending = await ApplyAsync(
            store, new[] { Add("axe") }, ContentTextEdit.Set(Target(NameField, "fr", "axe"), "Hache"));
        int audits = await AuditCountAsync(store);

        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.ImportTextBundleAsync(FamilySeed(), Actor, Operator, "seed"));

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
    public async Task A_failure_after_staging_leaves_the_database_empty_and_a_retry_imports_whole()
    {
        using var database = new TemporaryCatalogDatabase();
        using SqliteContentAuthoringStore store = await OpenAsync(database);
        ContentBundle seed = FamilySeed();
        database.Execute(
            """
            CREATE TRIGGER catalog_text_chunk_fault BEFORE INSERT ON catalog_text_chunk
            BEGIN
                SELECT RAISE(ABORT, 'text chunk fault');
            END;
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => store.ImportTextBundleAsync(seed, Actor, Operator, "seed"));

        Assert.Empty(await store.ListVersionsAsync());
        Assert.Null(await store.GetOpenDraftAsync());
        Assert.Empty(await store.ListFamiliesAsync(Item));
        Assert.Equal(0, (await store.ReadHighWaterAsync(Item)).ReservedThrough);
        Assert.Equal(0L, database.Scalar(
            """
            SELECT (SELECT COUNT(*) FROM catalog_draft_edit) + (SELECT COUNT(*) FROM catalog_draft_text_edit)
                 + (SELECT COUNT(*) FROM catalog_draft_text_language) + (SELECT COUNT(*) FROM catalog_text)
                 + (SELECT COUNT(*) FROM catalog_text_chunk) + (SELECT COUNT(*) FROM catalog_row);
            """));

        database.Execute("DROP TRIGGER catalog_text_chunk_fault;");
        Assert.Equal(1, (await store.ImportTextBundleAsync(seed, Actor, Operator, "retry")).VersionNumber);
        ContentVersionTextSnapshot text = await store.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "en", "fr" }, text.Languages.Select(language => language.WireTag));
        Assert.Equal("Blade", text.Revisions.Single(revision => revision.DefinitionId == 16).Value);
        Assert.Equal(16, Assert.Single(await store.ListFamiliesAsync(Item)).Blocks.Single().BaseId);
    }

    /// <summary>A seed carrying a family whose block holds one named row, beside a plain row and an empty language.</summary>
    static ContentBundle FamilySeed()
        => new(
            ContentBundle.TextFormatVersion,
            "seed",
            0,
            Types(),
            [Row("sword", 3, retired: false), new ContentBundleRow(Item, 16, new ContentKey("blade"), false, "swords", [Value(16)])],
            [new ContentFamily(7, Item, "swords", 16, false, 1, [new ContentFamilyBlock(7, 0, 16, 16, 17, 1)])],
            Array.Empty<RemapRule>(),
            new ContentBundleTextState(
                [Declare("en", "en"), Declare("fr", "fr")],
                [TextValue("sword", NameField, "en", "Sword"), TextValue("blade", NameField, "en", "Blade")]));

    static ContentBundle Seed(
        IReadOnlyList<ContentBundleRow> rows,
        IReadOnlyList<ContentTextLanguageDeclaration> languages,
        IReadOnlyList<ContentBundleTextValue> values)
        => new(
            ContentBundle.TextFormatVersion,
            "seed",
            0,
            Types(),
            rows,
            Array.Empty<ContentFamily>(),
            Array.Empty<RemapRule>(),
            new ContentBundleTextState(languages, values));

    /// <summary>The same rows through the OLD constructor, which is how a row-only wrapper loses the section.</summary>
    static ContentBundle Rebuild(ContentBundle bundle, int formatVersion)
        => new(formatVersion, bundle.StoreEpoch, bundle.SourceVersion, bundle.Types, bundle.Rows, bundle.Families, bundle.Rules);

    static IReadOnlyList<ContentBundleType> Types()
        => TextRegistry().ByTypeId
            .Select(registration => new ContentBundleType(
                registration.Type,
                registration.TypeKey,
                registration.DefaultVisibility,
                registration.ChunkSlots,
                registration.MaxDefinitionId,
                registration.Schema))
            .ToArray();

    static ContentBundleRow Row(string key, int id, bool retired)
        => new(Item, id, new ContentKey(key), retired, null, new[] { Value(id) });

    static ContentTextLanguageDeclaration Declare(string language, string wireTag) => new(language, wireTag);

    static ContentBundleTextValue TextValue(string key, string field, string language, string value)
        => new(Target(field, language, key), value);

    static string Describe(ContentBundleTextValue value)
        => value.Target.Key + "/" + value.Target.FieldName + "/" + value.Target.Language;

    /// <summary>The literal KECT hash of one language's entries, from literal derived keys in ordinal order.</summary>
    static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(
            tag,
            entries
                .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToArray());

    /// <summary>A document with the store epoch taken out, which differs between stores.</summary>
    static string WithoutEpoch(string json, ContentBundle bundle)
        => json.Replace(bundle.StoreEpoch, "epoch", StringComparison.Ordinal);
}
