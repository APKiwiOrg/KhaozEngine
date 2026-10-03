using System;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Catalog TEXT through the real registered admin actions over a real <see cref="SqliteContentAuthoringStore"/>,
/// so every text route dispatches through the provider's own companion rather than through the in-memory
/// reference or a row-only proxy.
/// <para>
/// Each store holds one <c>Data Source=:memory:</c> connection for its life, so nothing touches disk but the
/// harness pack directories, and nothing here is process global.
/// </para>
/// </summary>
public sealed class CatalogTextSqliteAdminTests : IDisposable
{
    readonly CatalogActionHarness _source = new();
    readonly CatalogActionHarness _target = new();
    readonly SqliteContentAuthoringStore _store;
    readonly SqliteContentAuthoringStore _fresh;
    readonly ServerAdmin _admin = CatalogActionHarness.NewSurface();
    readonly ServerAdmin _freshAdmin = CatalogActionHarness.NewSurface();

    /// <summary>Opens both SQLite stores over the harness registries and packs, and registers the actions.</summary>
    public CatalogTextSqliteAdminTests()
    {
        _store = new SqliteContentAuthoringStore("Data Source=:memory:", _source.Registry, _source.Pack);
        _fresh = new SqliteContentAuthoringStore("Data Source=:memory:", _target.Registry, _target.Pack);
        CatalogAdminActions.Register(_admin, _store, _source.Registry);
        CatalogAdminActions.Register(_freshAdmin, _fresh, _target.Registry);
    }

    /// <summary>
    /// An add plus its name, a text-only edit, a rollback restoring the first name, a discard, an export and
    /// an import into a fresh SQLite store, all through the admin actions, with the literal value read back
    /// out of the imported store's text snapshot.
    /// </summary>
    [Fact]
    public async Task TextRoutesOverSqlite_EditPublishRollbackDiscardExportAndImport()
    {
        await _store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await _fresh.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        JsonElement added = await OkAsync(_admin, "catalog-edit", """
        {
          "operator": "oid:8f2c",
          "edits": [ { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 4200, "stackable": false } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Sword" } ]
        }
        """);
        Assert.Equal(1, added.GetProperty("applied").GetInt32());
        Assert.Equal(1, added.GetProperty("textApplied").GetInt32());
        Assert.Equal(1, (await OkAsync(_admin, "catalog-publish", """{ "expectedBaseVersion": 0 }""")).GetProperty("version").GetInt32());

        JsonElement renamed = await OkAsync(_admin, "catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Blade" } ] }
        """);
        Assert.Equal(0, renamed.GetProperty("applied").GetInt32());
        Assert.Equal(1, renamed.GetProperty("textApplied").GetInt32());
        Assert.Equal(2, (await OkAsync(_admin, "catalog-publish", """{ "expectedBaseVersion": 1 }""")).GetProperty("version").GetInt32());

        JsonElement rolledBack = await OkAsync(_admin, "catalog-rollback", """{ "operator": "oid:8f2c", "toVersion": 1 }""");
        Assert.True(rolledBack.GetProperty("draftCreated").GetBoolean());
        Assert.Equal(0, rolledBack.GetProperty("editCount").GetInt32());
        Assert.Equal(1, rolledBack.GetProperty("textEditCount").GetInt32());
        Assert.Equal(0, rolledBack.GetProperty("languageIntroductionCount").GetInt32());
        JsonElement pending = Assert.Single((await OkAsync(_admin, "catalog-draft", null)).GetProperty("textEdits").EnumerateArray());
        Assert.Equal("Stone Sword", pending.GetProperty("value").GetString());

        JsonElement discarded = await OkAsync(_admin, "catalog-discard", """{ "operator": "oid:8f2c" }""");
        Assert.True(discarded.GetProperty("discarded").GetBoolean());
        Assert.Equal(1, discarded.GetProperty("textEditCount").GetInt32());
        Assert.Null(await _store.GetOpenDraftAsync());

        JsonElement exported = await OkAsync(_admin, "catalog-export", null);
        Assert.Equal(2, exported.GetProperty("version").GetInt32());
        Assert.Equal(2, exported.GetProperty("formatVersion").GetInt32());
        Assert.Equal(1, exported.GetProperty("languageCount").GetInt32());
        Assert.Equal(1, exported.GetProperty("textValueCount").GetInt32());

        JsonElement imported = await OkAsync(
            _freshAdmin, "catalog-import", $$"""{ "note": "seed", "bundle": {{exported.GetProperty("bundle").GetRawText()}} }""");
        Assert.Equal(1, imported.GetProperty("languagesImported").GetInt32());
        Assert.Equal(1, imported.GetProperty("textValuesImported").GetInt32());

        ContentVersionTextSnapshot text = await _fresh.ReadTextSnapshotAsync(imported.GetProperty("version").GetInt32());
        ContentTextRevision value = Assert.Single(text.Revisions);
        Assert.Equal("Stone Blade", value.Value);
        Assert.Equal("name", value.FieldName);
        Assert.Equal("en", Assert.Single(text.Languages).WireTag);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Dispose();
        _fresh.Dispose();
        _source.Dispose();
        _target.Dispose();
    }

    static async Task<JsonElement> OkAsync(ServerAdmin admin, string action, string? body)
    {
        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, action, body);
        Assert.True(
            result.Status == AdminActionStatus.Ok,
            action + " answered " + result.Status + ": " + JsonSerializer.Serialize(result.Payload));
        return CatalogActionHarness.Wire(result);
    }
}
