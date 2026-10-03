using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Tests.Catalog.Sqlite;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Authoring;

/// <summary>
/// Bundle format 2 through the reference store: the companion import, the format-aware export, the JSON text
/// section and every refusal that has to land before an import resets or stages anything.
/// </summary>
public sealed partial class TextBundleTests
{
    const string FormatOneFixture = """
        {
          // A committed format 1 seed, hand authored, which predates text.
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

    [Fact]
    public async Task A_complete_bundle_round_trips_retired_rows_historical_spelling_and_empty_languages()
    {
        using var files = new TemporaryCatalogDatabase();
        var source = TextStore(PackAt(files, "source"));
        IContentTextAuthoringStore text = source;
        ContentBundle seed = Seed(
            new[] { Row("sword", 3, retired: false), Row("shield", 4, retired: true) },
            new[] { Declare("en-us", "en-US"), Declare("fr", "fr"), Declare("de", "de") },
            new[]
            {
                TextValue("sword", NameField, "en-US", "Sword"),
                TextValue("shield", NameField, "en-us", "Old Shield"),
                TextValue("shield", DescriptionField, "de", "Alter Schild"),
            });

        ContentPublishResult imported = await text.ImportTextBundleAsync(seed, Actor, Operator, "seed");

        Assert.Equal(1, imported.VersionNumber);
        ContentVersionTextSnapshot snapshot = await text.ReadTextSnapshotAsync(1);
        Assert.Equal(new[] { "de", "en-US", "fr" }, snapshot.Languages.Select(language => language.WireTag));
        Assert.Equal(Hash("fr"), snapshot.Languages.Single(language => language.Language == "fr").Hash);
        Assert.Equal(
            Hash("en-US", (Key("sword", NameField), "Sword"), (Key("shield", NameField), "Old Shield")),
            snapshot.Languages.Single(language => language.Language == "en-us").Hash);
        Assert.Equal(new[] { "de", "en-US", "fr" }, (await source.ReadPublishBaselineAsync()).Languages.Select(l => l.Tag));
        Assert.True((await source.ListRowsAsync(Item, 1, "shield", true, 0, 1)).Rows.Single().IsRetired);
        Assert.Equal(3, snapshot.Revisions.Count);

        ContentBundle exported = await source.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.TextFormatVersion, exported.FormatVersion);
        Assert.Equal(new[] { Declare("de", "de"), Declare("en-us", "en-US"), Declare("fr", "fr") }, exported.TextState!.Languages);
        Assert.Equal(seed.TextState!.Values.OrderBy(Describe), exported.TextState.Values.OrderBy(Describe));

        string json = ContentBundleJson.Write(exported);
        ContentBundle read = ContentBundleJson.Read(json);
        Assert.Equal(exported.TextState.Languages, read.TextState!.Languages);
        Assert.Equal(exported.TextState.Values, read.TextState.Values);

        var destination = TextStore(PackAt(files, "destination"));
        await ((IContentTextAuthoringStore)destination).ImportTextBundleAsync(read, Actor, Operator, "adopt");
        ContentBundle again = await destination.ExportBundleAsync(1);
        Assert.Equal(WithoutEpoch(json, exported), WithoutEpoch(ContentBundleJson.Write(again), again));
        Assert.Equal(
            snapshot.Languages,
            (await ((IContentTextAuthoringStore)destination).ReadTextSnapshotAsync(1)).Languages);
    }

    [Fact]
    public async Task A_version_declaring_only_an_empty_language_exports_format_two_and_a_text_free_one_format_one()
    {
        using var files = new TemporaryCatalogDatabase();
        var store = TextStore(files.Pack());
        IContentTextAuthoringStore text = store;
        await store.ApplyEditsAsync(new[] { Add() }, Actor, Operator, "rows");
        await store.PublishAsync(Request(0));
        ContentBundle textFree = await store.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.CurrentFormatVersion, textFree.FormatVersion);
        Assert.Null(textFree.TextState);
        Assert.DoesNotContain("\"text\"", ContentBundleJson.Write(textFree), StringComparison.Ordinal);

        await ApplyAsync(text, null, ContentTextEdit.Set(Target(NameField, "fr"), "Epee"));
        await ApplyAsync(text, null, ContentTextEdit.Remove(Target(NameField, "fr")));
        await store.PublishAsync(Request(1));
        ContentBundle empty = await store.ExportBundleAsync(2);

        Assert.Equal(ContentBundle.TextFormatVersion, empty.FormatVersion);
        Assert.Equal(new[] { Declare("fr", "fr") }, empty.TextState!.Languages);
        Assert.Empty(empty.TextState.Values);
        Assert.Null((await store.ExportBundleAsync(1)).TextState);
    }

    [Fact]
    public async Task The_format_one_fixture_imports_as_empty_text_and_exports_byte_identically()
    {
        using var files = new TemporaryCatalogDatabase();
        ContentBundle fixture = ContentBundleJson.Read(FormatOneFixture);
        Assert.Null(fixture.TextState);

        var companion = TextStore(PackAt(files, "companion"));
        await ((IContentTextAuthoringStore)companion).ImportTextBundleAsync(fixture, Actor, Operator, "seed");
        var rowOnly = TextStore(PackAt(files, "row-only"));
        await rowOnly.ImportBundleAsync(fixture, Actor, Operator, "seed");

        ContentVersionTextSnapshot text = await ((IContentTextAuthoringStore)companion).ReadTextSnapshotAsync(1);
        Assert.Empty(text.Languages);
        Assert.Empty(text.Revisions);
        ContentBundle fromCompanion = await companion.ExportBundleAsync(1);
        ContentBundle fromRowOnly = await rowOnly.ExportBundleAsync(1);
        Assert.Equal(ContentBundle.CurrentFormatVersion, fromCompanion.FormatVersion);
        string written = ContentBundleJson.Write(fromCompanion);
        Assert.Equal(WithoutEpoch(ContentBundleJson.Write(fromRowOnly), fromRowOnly), WithoutEpoch(written, fromCompanion));
        Assert.Equal(
            written,
            ContentBundleJson.Write(new ContentBundle(
                1, fromCompanion.StoreEpoch, 1, fromCompanion.Types, fromCompanion.Rows, fromCompanion.Families, fromCompanion.Rules)));
    }

    [Theory]
    [InlineData("alias-declarations")]
    [InlineData("alias-values")]
    [InlineData("missing-section")]
    [InlineData("lone-surrogate")]
    [InlineData("oversized-value")]
    [InlineData("bad-language")]
    [InlineData("empty-key")]
    [InlineData("text-in-format-one")]
    [InlineData("future-version")]
    public void A_malformed_text_document_is_refused_whole(string defect)
    {
        string json = Document(defect);

        ContentAuthoringException refused = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Read(json));

        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
    }

    [Fact]
    public async Task Refusals_land_before_anything_is_reset_or_staged()
    {
        using var files = new TemporaryCatalogDatabase();
        var empty = TextStore(PackAt(files, "empty"));
        IContentTextAuthoringStore text = empty;
        ContentBundle ghost = Seed(
            new[] { Row("sword", 3, retired: false) }, new[] { Declare("en", "en") }, new[] { TextValue("ghost", NameField, "en", "Nobody") });
        var unknown = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ImportTextBundleAsync(ghost, Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.UnknownRowReason, unknown.Reason);
        ContentBundle hidden = Seed(
            new[] { Row("sword", 3, retired: false) }, new[] { Declare("en", "en") }, new[] { TextValue("sword", SecretField, "en", "Hidden") });
        var ineligible = await Assert.ThrowsAsync<ContentAuthoringException>(() => text.ImportTextBundleAsync(hidden, Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.TextTargetIneligibleReason, ineligible.Reason);
        var future = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => text.ImportTextBundleAsync(Rebuild(Seed([], [], []), 3), Actor, Operator, "seed"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, future.Reason);
        Assert.Empty(await empty.ListVersionsAsync());
        Assert.Equal(0, await AuditCountAsync(empty));
        Assert.Equal(0, (await empty.ReadHighWaterAsync(Item)).ReservedThrough);

        var live = TextStore(PackAt(files, "live"));
        await PublishNamedRowAsync(live, "sword", "Sword");
        int audits = await AuditCountAsync(live);
        ContentBundle lost = Rebuild(await live.ExportBundleAsync(1), ContentBundle.TextFormatVersion);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ((IContentTextAuthoringStore)live).ImportTextBundleAsync(lost, Actor, Operator, "again"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, refused.Reason);
        Assert.Equal(1, await live.GetActiveVersionAsync());
        Assert.Equal(audits, await AuditCountAsync(live));
        Assert.Equal("Sword", (await ((IContentTextAuthoringStore)live).ReadTextSnapshotAsync(1)).Revisions.Single().Value);
    }

    [Fact]
    public async Task An_old_constructor_rebuilt_from_a_text_bearing_bundle_is_refused_rather_than_imported_row_only()
    {
        using var files = new TemporaryCatalogDatabase();
        var source = TextStore(PackAt(files, "source"));
        await PublishNamedRowAsync(source, "sword", "Sword");
        ContentBundle stripped = Rebuild(await source.ExportBundleAsync(1), ContentBundle.TextFormatVersion);

        var destination = TextStore(PackAt(files, "destination"));
        var rowOnly = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => destination.ImportBundleAsync(stripped, Actor, Operator, "import"));
        Assert.Equal(ContentAuthoringException.TextUnrepresentedReason, rowOnly.Reason);
        var companion = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => ((IContentTextAuthoringStore)destination).ImportTextBundleAsync(stripped, Actor, Operator, "import"));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, companion.Reason);
        var written = Assert.Throws<ContentAuthoringException>(() => ContentBundleJson.Write(stripped));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, written.Reason);
        var context = Assert.Throws<ContentAuthoringException>(() => new ContentUpgradeContext(1, stripped, TextRegistry()));
        Assert.Equal(ContentAuthoringException.BundleFormatReason, context.Reason);
        Assert.Empty(await destination.ListVersionsAsync());
        Assert.Equal(0, await AuditCountAsync(destination));
    }

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
        => new(Item, id, new ContentKey(key), retired, null, new[] { TextAuthoringFixtures.Value(id) });

    static FileSystemPackStore PackAt(TemporaryCatalogDatabase files, string name)
        => new(System.IO.Path.Combine(files.Root, name));

    static ContentTextLanguageDeclaration Declare(string language, string wireTag) => new(language, wireTag);

    static ContentBundleTextValue TextValue(string key, string field, string language, string value)
        => new(Target(field, language, key), value);

    static string Describe(ContentBundleTextValue value)
        => value.Target.Key + "/" + value.Target.FieldName + "/" + value.Target.Language;

    static string Key(string key, string field) => ContentTextKey.Derive("item", key, field);

    static string Hash(string tag, params (string Key, string Value)[] entries)
        => ContentTextChunkCodec.Hash(
            tag,
            entries
                .Select(entry => new KeyValuePair<string, string>(entry.Key, entry.Value))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .ToArray());

    /// <summary>A document with the store epoch and source version taken out, which differ between stores.</summary>
    static string WithoutEpoch(string json, ContentBundle bundle)
        => json.Replace(bundle.StoreEpoch, "epoch", StringComparison.Ordinal);

    static string Document(string defect)
    {
        string languages = """[ { "language": "en-us", "wireTag": "en-US" } ]""";
        string value = "Sword";
        string language = "en-us";
        string key = "sword";
        int format = 2;
        string extra = string.Empty;
        switch (defect)
        {
            case "alias-declarations":
                languages = """[ { "language": "en-us", "wireTag": "en-US" }, { "language": "en-us", "wireTag": "en-us" } ]""";
                break;
            case "alias-values":
                extra = """, { "typeId": 1024, "key": "sword", "field": "name", "language": "EN-us", "value": "Again" }""";
                break;
            case "lone-surrogate":
                value = "\\ud800";
                break;
            case "oversized-value":
                value = new string('a', ContentTextEdit.MaxValueBytes + 1);
                break;
            case "bad-language":
                language = "en_us";
                break;
            case "empty-key":
                key = string.Empty;
                break;
            case "text-in-format-one":
                format = 1;
                break;
            case "future-version":
                format = 3;
                break;
        }

        string section = defect == "missing-section"
            ? string.Empty
            : FormattableString.Invariant($$"""
                , "text": {
                  "languages": {{languages}},
                  "values": [ { "typeId": 1024, "key": "{{key}}", "field": "name", "language": "{{language}}", "value": "{{value}}" }{{extra}} ]
                }
                """);
        return FormattableString.Invariant($$"""
            {
              "formatVersion": {{format}}, "storeEpoch": "seed", "sourceVersion": 0,
              "types": [], "families": [], "rows": [], "rules": []{{section}}
            }
            """);
    }
}
