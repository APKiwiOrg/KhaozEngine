using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The bundle pair of spec 10.9, the operator identity of 10.10 and the operational pair of 10.11:
/// <c>catalog-import</c>, <c>catalog-export</c>, <c>catalog-sweep</c> and <c>catalog-verify</c>.
/// <para>
/// The fact the whole import rule exists for is here: a bundle goes into an EMPTY database only. A seed that
/// runs repeatedly against live data either reverts an operator's value on the next deploy or is beaten
/// forever by a stored row, and an import that can only run once cannot have either defect.
/// </para>
/// </summary>
public sealed class CatalogBundleActionTests : IDisposable
{
    readonly CatalogActionHarness _harness = new();

    /// <summary>
    /// Export at N then import into an empty store reproduces the same rows, the same keys and the SAME
    /// IDS, because the export carries them and the import keeps them. That is what makes the bundle a
    /// lossless export rather than an approximation, and it is what makes an adoption a no-op for stored
    /// player data.
    /// </summary>
    [Fact]
    public async Task ExportThenImportIntoAnEmptyStore_ReproducesTheRowsTheKeysAndTheIds()
    {
        await _harness.PublishThingsAsync("stone_sword", "oak_shield");
        int published = await _harness.PublishThingsAsync("iron_sword");
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string key in new[] { "stone_sword", "oak_shield", "iron_sword" })
        {
            ids[key] = await _harness.IdOfAsync(key);
        }

        JsonElement exported = await _harness.OkAsync("catalog-export", $$"""{ "version": {{published}} }""");
        Assert.Equal(published, exported.GetProperty("version").GetInt32());
        Assert.Equal(3, exported.GetProperty("rowCount").GetInt32());
        string bundle = exported.GetProperty("bundle").GetRawText();

        using var fresh = new CatalogActionHarness();
        JsonElement imported = await fresh.OkAsync(
            "catalog-import",
            $$"""{ "operator": "oid:8f2c", "note": "seed", "bundle": {{bundle}} }""");

        // The import republishes at version 1: a lossless export is not a backup, and the difference is the
        // version LINE.
        Assert.Equal(1, imported.GetProperty("version").GetInt32());
        Assert.Equal(3, imported.GetProperty("rowsImported").GetInt32());

        JsonElement rows = await fresh.OkAsync("catalog-list", """{ "typeKey": "thing", "take": 50 }""");
        foreach (JsonElement row in rows.GetProperty("rows").EnumerateArray())
        {
            string key = row.GetProperty("key").GetString()!;
            Assert.Equal(ids[key], row.GetProperty("id").GetInt32());
            Assert.Equal(4200, row.GetProperty("fields").GetProperty("value").GetInt32());
        }

        Assert.Equal(3, rows.GetProperty("total").GetInt32());
    }

    /// <summary>
    /// An import into a database that already holds a published version is a 409 carrying the active
    /// version, with NO partial write. A deployed database's values change through an edit and a publish and
    /// through nothing else, ever.
    /// </summary>
    [Fact]
    public async Task Import_IntoANonEmptyStore_IsA409CarryingTheActiveVersion()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        JsonElement exported = await _harness.OkAsync("catalog-export", null);
        string bundle = exported.GetProperty("bundle").GetRawText();

        JsonElement body = await _harness.ConflictAsync(
            "catalog-import", $$"""{ "bundle": {{bundle}} }""");

        Assert.Equal("catalog is not empty", body.GetProperty("error").GetString());
        Assert.Equal(ContentAuthoringException.CatalogNotEmptyReason, body.GetProperty("reason").GetString());
        Assert.Equal(published, body.GetProperty("activeVersion").GetInt32());

        // Nothing moved, which is the "no partial write" half of the rule.
        Assert.Equal(published, await _harness.Store.GetActiveVersionAsync());
        Assert.Equal(1, (await _harness.Store.ListRowsAsync(CatalogActionHarness.Thing, 0, null, true, 0, 50)).Total);
    }

    /// <summary>A bundle this build cannot read is a 400 naming what was wrong, never a half-built store.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("""{ "bundle": 7 }""")]
    [InlineData("""{ "bundle": { "formatVersion": 99 } }""")]
    public async Task Import_OfAnUnreadableBundle_IsRefused(string? body)
    {
        JsonElement refusal = await _harness.RefusedAsync("catalog-import", body);

        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("error").GetString()));
        Assert.Equal(0, await _harness.Store.GetActiveVersionAsync());
    }

    /// <summary>An export from a store that has published nothing is a 400 rather than an empty document.</summary>
    [Fact]
    public async Task Export_FromAStoreThatHasPublishedNothing_IsRefused()
    {
        JsonElement body = await _harness.RefusedAsync("catalog-export", null);

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, body.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The operator identity is a FORWARDED, unverified field and it is recorded BESIDE the actor. The actor
    /// is what the engine authenticated, which is the bearer token's holder, and the operator is what the
    /// console asserted. Keeping both columns is what lets an audit row say which is which.
    /// </summary>
    [Fact]
    public async Task AMutation_RecordsTheForwardedOperatorBesideTheAuthenticatedActor()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");

        await _harness.OkAsync("catalog-edit", $$"""
        { "operator": "oid:8f2c...", "edits": [
            { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);

        ContentAuditEntry edit = (await _harness.Store.ListAuditAsync(CatalogActionHarness.Thing, sword, 0, 50))
            .First(entry => entry.Action == ContentAuditActions.DraftEdit);
        Assert.Equal("oid:8f2c...", edit.Operator);
        Assert.Equal(CatalogAdminActions.Actor, edit.Actor);
    }

    /// <summary>
    /// A request with NO operator is ACCEPTED and audited with an empty one, because refusing it would break
    /// a scripted maintenance call that has no human behind it.
    /// </summary>
    [Fact]
    public async Task AMutationWithNoOperator_IsAcceptedAndAuditedWithAnEmptyOne()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");

        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);

        ContentAuditEntry edit = (await _harness.Store.ListAuditAsync(CatalogActionHarness.Thing, sword, 0, 50))
            .First(entry => entry.Action == ContentAuditActions.DraftEdit);
        Assert.Equal(string.Empty, edit.Operator);
        Assert.Equal(CatalogAdminActions.Actor, edit.Actor);
    }

    /// <summary>
    /// An operator over 128 characters is a 400. The field takes a STABLE identity such as an object id
    /// rather than a display name, and the refusal says so, because a display name breaks the audit trail
    /// the day someone renames themselves.
    /// </summary>
    [Fact]
    public async Task AnOperatorOver128Characters_IsRefused()
    {
        string tooLong = new('o', 129);

        JsonElement body = await _harness.RefusedAsync(
            "catalog-edit",
            $$"""{ "operator": "{{tooLong}}", "edits": [ { "op": "add", "typeKey": "thing", "key": "x" } ] }""");

        Assert.Contains("128", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Contains("STABLE", body.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sweep deletes what no version references and keeps everything every version does, because a
    /// pinned server and a rollback both need older versions to stay fetchable.
    /// </summary>
    [Fact]
    public async Task Sweep_DeletesAnOrphanAndKeepsEveryVersionsObjects()
    {
        await _harness.PublishThingsAsync("stone_sword");
        await _harness.PublishThingsAsync("iron_sword");
        string orphan = new('a', 64);
        string path = _harness.Pack.PathFor(orphan);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);

        JsonElement body = await _harness.OkAsync("catalog-sweep", """{ "operator": "oid:8f2c" }""");

        Assert.True(body.GetProperty("ran").GetBoolean());
        Assert.Equal(1, body.GetProperty("deleted").GetInt32());
        Assert.True(body.GetProperty("kept").GetInt32() > 0);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("skipReason").ValueKind);
        Assert.False(File.Exists(path));

        // Every object both versions name is still fetchable, which a verify of each is the proof of.
        JsonElement verified = await _harness.OkAsync("catalog-verify", """{ "version": 1 }""");
        Assert.True(verified.GetProperty("healthy").GetBoolean());
    }

    /// <summary>A store with no pack target refuses both operational actions rather than throwing.</summary>
    [Theory]
    [InlineData("catalog-sweep")]
    [InlineData("catalog-verify")]
    public async Task TheOperationalPair_WithNoPackTarget_IsRefused(string action)
    {
        var registry = new ContentTypeRegistry();
        var store = new InMemoryContentAuthoringStore(registry);
        var admin = new ServerAdmin(new SweepOnlyControllable());
        CatalogAdminActions.Register(admin, store, registry);

        Assert.True(admin.TryGetAction(action, out var handler));
        AdminActionResult result = await handler!(null, default);

        Assert.Equal(AdminActionStatus.BadRequest, result.Status);
        JsonElement body = CatalogActionHarness.Wire(result);
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("reason").GetString()));
    }

    /// <summary>
    /// Verify walks a version's manifests, fetches every object they name and rehashes it. A healthy pack
    /// reports no mismatch, and the count it checked says it actually walked one.
    /// </summary>
    [Fact]
    public async Task Verify_OnAHealthyPack_ReportsNoMismatch()
    {
        int published = await _harness.PublishThingsAsync("stone_sword", "iron_sword");

        JsonElement body = await _harness.OkAsync("catalog-verify", null);

        Assert.Equal(published, body.GetProperty("version").GetInt32());
        Assert.True(body.GetProperty("healthy").GetBoolean());
        Assert.True(body.GetProperty("objectsChecked").GetInt32() >= 3);
        Assert.Empty(body.GetProperty("mismatches").EnumerateArray());
    }

    /// <summary>
    /// An object whose bytes no longer digest to the address it is filed under is REPORTED and never
    /// repaired, because a repair means deciding which copy is right and only a republish can know that.
    /// </summary>
    [Fact]
    public async Task Verify_OnACorruptChunk_ReportsItAndRepairsNothing()
    {
        int published = await _harness.PublishThingsAsync("stone_sword");
        ContentVersionRecord record = (await _harness.Store.GetVersionAsync(published))!;
        string chunk = await FirstChunkAsync(record);
        string path = _harness.Pack.PathFor(chunk);
        byte[] before = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, [.. before.Select(static b => (byte)~b)]);

        JsonElement body = await _harness.OkAsync("catalog-verify", $$"""{ "version": {{published}} }""");

        Assert.False(body.GetProperty("healthy").GetBoolean());
        JsonElement mismatch = body.GetProperty("mismatches").EnumerateArray()
            .Single(entry => entry.GetProperty("hash").GetString() == chunk);
        Assert.False(string.IsNullOrWhiteSpace(mismatch.GetProperty("reason").GetString()));

        // Read only: the corrupt bytes are exactly where they were.
        Assert.Equal(before.Length, (await File.ReadAllBytesAsync(path)).Length);
        Assert.NotEqual(before, await File.ReadAllBytesAsync(path));
    }

    /// <summary>A verify naming a version the store does not hold is a 400 rather than a throw.</summary>
    [Fact]
    public async Task Verify_NamingAVersionThatDoesNotExist_IsRefused()
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-verify", """{ "version": 99 }""");

        Assert.Equal(ContentAuthoringException.UnknownVersionReason, body.GetProperty("reason").GetString());
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    /// <summary>One chunk address out of a version's server manifest, which is a real object to corrupt.</summary>
    async Task<string> FirstChunkAsync(ContentVersionRecord record)
    {
        ContentManifestRead read = await ContentPackReader.ReadManifestAsync(
            _harness.Pack, record.ServerManifestHash, ContentManifestSide.Server, _harness.Registry);
        Assert.True(read.Success, "the server manifest reads back.");
        return read.Manifest!.Types.SelectMany(static type => type.Chunks).First().Hash;
    }

    sealed class SweepOnlyControllable : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();

        public void Teleport(PlayerRef target, System.Numerics.Vector3 position)
        {
        }

        public void Kick(PlayerRef target, string reason)
        {
        }

        public void Broadcast(string text)
        {
        }
    }
}
