using System;
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
/// The two draft-writing actions of spec 10.5, <c>catalog-edit</c> and <c>catalog-discard</c>, over the
/// in-memory authoring store and the real <see cref="ServerAdmin"/> registry.
/// <para>
/// The facts that matter most here are the two the whole boundary exists for: a batch lands WHOLE or not
/// at all, and a refusal carries EVERY finding rather than the first, so an operator fixes three problems
/// in one round trip instead of three.
/// </para>
/// </summary>
public sealed class CatalogEditActionTests : IDisposable
{
    readonly CatalogActionHarness _harness = new();

    /// <summary>
    /// The four operations in ONE request, applied together. A fork is an <c>op</c> VALUE rather than a
    /// seventeenth action, because it is an edit against the open draft like the other three and it is
    /// saved, validated, diffed and published through the same path.
    /// </summary>
    [Fact]
    public async Task Edit_AppliesTheFourOperationsInOneRequest()
    {
        await _harness.PublishThingsAsync("stone_sword", "oak_shield", "iron_sword", "fire_mod");
        int sword = await _harness.IdOfAsync("stone_sword");
        int shield = await _harness.IdOfAsync("oak_shield");
        int mod = await _harness.IdOfAsync("fire_mod");

        JsonElement body = await _harness.OkAsync("catalog-edit", $$"""
        {
          "operator": "oid:8f2c",
          "note": "autumn price pass",
          "edits": [
            { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } },
            { "op": "add", "typeKey": "thing", "key": "steel_sword",
              "fields": { "value": 12000, "stackable": false, "icon": "0a1b" } },
            { "op": "retire", "typeKey": "thing", "id": {{shield}},
              "policy": "replacement", "replacementKey": "iron_sword" },
            { "op": "fork", "typeKey": "thing", "id": {{mod}},
              "forkKey": "fire_mod_legacy", "flagField": "legacy", "fields": { "value": 6000 } }
          ]
        }
        """);

        Assert.Equal(4, body.GetProperty("applied").GetInt32());
        JsonElement draft = body.GetProperty("draft");
        Assert.Equal(4, draft.GetProperty("editCount").GetInt32());
        Assert.Equal("autumn price pass", draft.GetProperty("note").GetString());
        Assert.False(draft.GetProperty("frozen").GetBoolean());

        // The COPY's allocated id does not appear anywhere in the answer, because ids are allocated at
        // publish and reporting one from an edit would be reporting a number that does not exist yet.
        JsonElement pending = await _harness.OkAsync("catalog-draft", null);
        string[] ops = pending.GetProperty("edits").EnumerateArray()
            .Select(static edit => edit.GetProperty("op").GetString()!)
            .ToArray();
        Assert.Equal(new[] { "update", "add", "retire", "fork" }, ops);
        JsonElement fork = pending.GetProperty("edits")[3];
        Assert.Equal("fire_mod_legacy", fork.GetProperty("forkKey").GetString());
        Assert.Equal("legacy", fork.GetProperty("flagField").GetString());
    }

    /// <summary>
    /// A payload naming a field the schema does not declare is a 400 with <c>KEC0004</c>, and the response
    /// carries EVERY finding rather than the first. That is the direct answer to an upsert with no gate
    /// letting an operator save a row the server then rejects at boot while the console reports success.
    /// </summary>
    [Fact]
    public async Task Edit_NamingUnknownFields_CarriesEveryFindingInOneRoundTrip()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        {
          "edits": [
            { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "valeu": 45 } },
            { "op": "add", "typeKey": "thing", "key": "iron_sword",
              "fields": { "value": 120, "tradable": true } }
          ]
        }
        """);

        Assert.Equal("content edit refused", body.GetProperty("error").GetString());
        Assert.Equal(2, body.GetProperty("findingCount").GetInt32());
        Assert.Equal(new[] { "KEC0004", "KEC0004" }, CatalogActionHarness.Codes(body));
        Assert.Contains("valeu", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
        Assert.Contains("tradable", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
        Assert.Equal("thing", body.GetProperty("findings")[0].GetProperty("type").GetString());
    }

    /// <summary>
    /// No edit ever carries a localized text key, and the refusal names the DERIVED key so an operator sees
    /// what they were trying to set rather than a refusal about a field their console showed them.
    /// </summary>
    [Fact]
    public async Task Edit_CarryingAMarkerField_NamesTheDerivedKey()
    {
        JsonElement body = await _harness.RefusedAsync("catalog-edit", """
        {
          "edits": [
            { "op": "add", "typeKey": "thing", "key": "stone_sword",
              "fields": { "value": 4200, "stackable": false, "name": "Stone Sword" } }
          ]
        }
        """);

        Assert.Equal(new[] { "KEC0004" }, CatalogActionHarness.Codes(body));
        Assert.Contains(
            ContentTextKey.Derive("thing", new ContentKey("stone_sword").Utf8, "name"),
            CatalogActionHarness.Messages(body),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A batch is ATOMIC at the boundary too: one bad edit refuses the whole request and the draft is left
    /// exactly as it was, which is what makes a grid's save whole rather than partly applied.
    /// </summary>
    [Fact]
    public async Task Edit_WithOneBadEntry_AppliesNoneOfThem()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");

        await _harness.RefusedAsync("catalog-edit", $$"""
        {
          "edits": [
            { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } },
            { "op": "update", "typeKey": "thing", "id": {{sword + 900}}, "fields": { "value": 1 } }
          ]
        }
        """);

        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A replacement retire with no resolvable key is a 400 with <c>KEC0017</c>. The rule moves references
    /// onto the replacement, so appending one that names nothing live is how a reference walks onto nothing.
    /// </summary>
    [Theory]
    [InlineData("\"replacement\", \"replacementKey\": \"no_such_row\"", "KEC0017")]
    [InlineData("\"replacement\"", "KEC0017")]
    [InlineData("\"melted\"", "KEC0004")]
    public async Task Edit_RetiringBadly_IsRefusedWithItsOwnCode(string policy, string code)
    {
        await _harness.PublishThingsAsync("oak_shield");
        int shield = await _harness.IdOfAsync("oak_shield");

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        { "edits": [ { "op": "retire", "typeKey": "thing", "id": {{shield}}, "policy": {{policy}} } ] }
        """);

        Assert.Equal(new[] { code }, CatalogActionHarness.Codes(body));
    }

    /// <summary>
    /// A fork whose key is taken, whose flag field is absent or is not <c>Bool</c>, or whose source is
    /// already retired, is a 400 with <c>KEC0041</c>. The flag field is the CALLER's: the engine checks only
    /// that it exists and is Bool, because it has no opinion about which boolean means superseded on a type
    /// it did not define.
    /// </summary>
    [Theory]
    [InlineData("\"stone_sword\"", "\"legacy\"")]
    [InlineData("\"fire_mod_legacy\"", "\"value\"")]
    [InlineData("\"fire_mod_legacy\"", "\"no_such_field\"")]
    [InlineData("\"fire_mod_legacy\"", "null")]
    public async Task Edit_ForkingBadly_IsKec0041(string forkKey, string flagField)
    {
        await _harness.PublishThingsAsync("stone_sword", "fire_mod");
        int mod = await _harness.IdOfAsync("fire_mod");

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        {
          "edits": [
            { "op": "fork", "typeKey": "thing", "id": {{mod}},
              "forkKey": {{forkKey}}, "flagField": {{flagField}} }
          ]
        }
        """);

        Assert.Equal(new[] { "KEC0041" }, CatalogActionHarness.Codes(body));
    }

    /// <summary>
    /// A tag list is an array of ids in AUTHORED order, and opaque bytes are lower hex, which is the exact
    /// inverse of how every read renders them. A console writes back what it was handed.
    /// </summary>
    [Fact]
    public async Task Edit_ReadsEveryKindBackTheWayAReadRendersIt()
    {
        await _harness.PublishAsync(
            ContentEdit.Add(CatalogActionHarness.Tag, new ContentKey("metal"), []),
            ContentEdit.Add(CatalogActionHarness.Tag, new ContentKey("two_handed"), []));
        ContentRowPage tags = await _harness.Store.ListRowsAsync(CatalogActionHarness.Tag, 0, null, false, 0, 10);
        int[] ids = tags.Rows.Select(static row => row.Id).ToArray();

        await _harness.OkAsync("catalog-edit", $$"""
        {
          "edits": [
            { "op": "add", "typeKey": "thing", "key": "steel_sword",
              "fields": { "value": 12000, "stackable": false,
                          "tags": [{{ids[1]}}, {{ids[0]}}], "icon": "0a1bff" } }
          ]
        }
        """);

        JsonElement draft = await _harness.OkAsync("catalog-draft", null);
        JsonElement fields = draft.GetProperty("edits")[0].GetProperty("fields");
        Assert.Equal(
            new[] { ids[1], ids[0] },
            fields.GetProperty("tags").EnumerateArray().Select(static tag => tag.GetInt32()).ToArray());
        Assert.Equal("0a1bff", fields.GetProperty("icon").GetString());
        Assert.Equal(12000, fields.GetProperty("value").GetInt32());
    }

    /// <summary>
    /// A value of the wrong SHAPE for its kind is a finding rather than a throw, and the message says what
    /// the kind takes. A stored scaled int is the authored value times the schema's scale, which is the one
    /// thing a console cannot guess from the number alone.
    /// </summary>
    [Fact]
    public async Task Edit_WithAValueOfTheWrongKind_IsRefusedNamingWhatTheKindTakes()
    {
        JsonElement body = await _harness.RefusedAsync("catalog-edit", """
        {
          "edits": [
            { "op": "add", "typeKey": "thing", "key": "steel_sword",
              "fields": { "value": "twelve", "stackable": "yes" } }
          ]
        }
        """);

        Assert.Equal(new[] { "KEC0004", "KEC0004" }, CatalogActionHarness.Codes(body));
        Assert.Contains("ScaledInt", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
        Assert.Contains("true or false", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
    }

    /// <summary>
    /// A draft a publish holds takes no edit and no discard: both are a 409 naming the reason and the way
    /// out, because the change set the pipeline read at step 1 is the one the commit deletes.
    /// </summary>
    [Theory]
    [InlineData("catalog-edit")]
    [InlineData("catalog-discard")]
    public async Task ADraftAPublishHolds_RefusesEveryWriteWithA409(string action)
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.Store.ApplyEditsAsync(
            [ContentEdit.Update(CatalogActionHarness.Thing, sword, new ContentKey("stone_sword"),
                CatalogActionHarness.Fields(4500))],
            CatalogAdminActions.Actor,
            CatalogActionHarness.Operator,
            "pending");
        await _harness.Store.FreezeDraftAsync(await _harness.Store.GetActiveVersionAsync());

        JsonElement body = await _harness.ConflictAsync(action, $$"""
        { "operator": "oid:8f2c", "edits": [
            { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4600 } } ] }
        """);

        Assert.Equal(ContentAuthoringException.PublishInProgressReason, body.GetProperty("reason").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("remedy").GetString()));
    }

    /// <summary>
    /// A second edit naming a target the draft already holds under a DIFFERENT operation is a 409, because
    /// a draft holds ONE pending intent per row and flipping the first edit's operation would silently drop
    /// its fields.
    /// </summary>
    [Fact]
    public async Task Edit_CollidingWithAnotherIntentForTheSameRow_IsA409()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);

        JsonElement body = await _harness.ConflictAsync("catalog-edit", $$"""
        { "edits": [ { "op": "retire", "typeKey": "thing", "id": {{sword}}, "policy": "placeholder" } ] }
        """);

        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, body.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A discard writes one audit row carrying the EDIT COUNT, so a discarded draft leaves a trace rather
    /// than vanishing.
    /// </summary>
    [Fact]
    public async Task Discard_LeavesNoDraftAndAnAuditRowCarryingTheEditCount()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);

        JsonElement body = await _harness.OkAsync("catalog-discard", """{ "operator": "oid:8f2c" }""");

        Assert.True(body.GetProperty("discarded").GetBoolean());
        Assert.Equal(1, body.GetProperty("editCount").GetInt32());
        Assert.Null(await _harness.Store.GetOpenDraftAsync());

        ContentAuditEntry discard = (await _harness.Store.ListAuditAsync(default, 0, 0, 50))
            .First(entry => entry.Action == ContentAuditActions.DraftDiscard);
        Assert.Equal("1", discard.BeforeValue);
        Assert.Equal("oid:8f2c", discard.Operator);
    }

    /// <summary>
    /// A discard against a store with no draft open is a 200 that says so. "Nothing pending" is an answer,
    /// and a console showing a refusal for it would be showing an error for the state it wanted.
    /// </summary>
    [Fact]
    public async Task Discard_WithNoDraftOpen_SaysSoRatherThanRefusing()
    {
        JsonElement body = await _harness.OkAsync("catalog-discard", null);

        Assert.False(body.GetProperty("discarded").GetBoolean());
        Assert.Equal(0, body.GetProperty("editCount").GetInt32());
    }

    /// <summary>Every request-shaped refusal is the OBJECT error payload rather than a bare string.</summary>
    [Theory]
    [InlineData("catalog-edit", null)]
    [InlineData("catalog-edit", """{ "edits": [] }""")]
    [InlineData("catalog-edit", """{ "edits": 7 }""")]
    [InlineData("catalog-edit", """{ "edits": [ { "op": "add", "typeKey": "nope", "key": "x" } ] }""")]
    [InlineData("catalog-edit", """{ "edits": [ { "op": "delete", "typeKey": "thing", "key": "x" } ] }""")]
    [InlineData("catalog-edit", """{ "edits": [ { "op": "add", "typeKey": "thing" } ] }""")]
    public async Task ARefusedEdit_CarriesTheObjectErrorPayload(string action, string? body)
    {
        JsonElement refusal = await _harness.RefusedAsync(action, body);

        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("error").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(refusal.GetProperty("reason").GetString()));
        Assert.Equal(
            refusal.GetProperty("findingCount").GetInt32(),
            refusal.GetProperty("findings").GetArrayLength());
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();
}
