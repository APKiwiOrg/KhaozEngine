using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;
using KhaozEngine.Server.Admin.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// Catalog TEXT through the real registered admin actions: <c>textEdits</c> on <c>catalog-edit</c>, the
/// complete draft, diff, get, discard, rollback and bundle payloads, and the typed refusals a store without
/// the text companion answers with.
/// <para>
/// Every request lands through ONE atomic apply after every finding across rows and text was gathered, so
/// a refused request leaves the rows, the draft's text and introductions and the audit exactly as they were.
/// </para>
/// </summary>
public sealed class CatalogTextAdminActionTests : IDisposable
{
    /// <summary>A registered SERVER type carrying a localized text marker, which text may never target.</summary>
    const string SecretTypeKey = "secret";

    /// <summary>A CLIENT type whose second marker is SERVER visible, which text may never target either.</summary>
    const string LoreTypeKey = "lore";

    readonly CatalogActionHarness _harness = new(extraTypes: RegisterIneligibleTypes);

    /// <summary>
    /// An add and its name in ONE request: the row edit and the text intent land together, the language is
    /// introduced, and the audit records the captured actor, the forwarded operator and the language.
    /// </summary>
    [Fact]
    public async Task Edit_AddPlusNameInOneRequest_AppliesRowAndTextTogether()
    {
        JsonElement body = await _harness.OkAsync("catalog-edit", """
        {
          "operator": "oid:8f2c",
          "note": "new sword",
          "edits": [
            { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 4200, "stackable": false } }
          ],
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Sword" }
          ]
        }
        """);

        Assert.Equal(1, body.GetProperty("applied").GetInt32());
        Assert.Equal(1, body.GetProperty("textApplied").GetInt32());
        JsonElement header = body.GetProperty("draft");
        Assert.Equal(1, header.GetProperty("editCount").GetInt32());
        Assert.Equal(1, header.GetProperty("textEditCount").GetInt32());
        Assert.Equal(1, header.GetProperty("languageIntroductionCount").GetInt32());
        Assert.True(header.GetProperty("textRepresented").GetBoolean());

        JsonElement draft = await _harness.OkAsync("catalog-draft", null);
        JsonElement text = Assert.Single(draft.GetProperty("textEdits").EnumerateArray());
        Assert.Equal("set", text.GetProperty("op").GetString());
        Assert.Equal("thing", text.GetProperty("typeKey").GetString());
        Assert.Equal("stone_sword", text.GetProperty("key").GetString());
        Assert.Equal("name", text.GetProperty("field").GetString());
        Assert.Equal("en", text.GetProperty("language").GetString());
        Assert.Equal("Stone Sword", text.GetProperty("value").GetString());
        Assert.Equal("thing.stone_sword.name", text.GetProperty("derivedKey").GetString());
        JsonElement introduced = Assert.Single(draft.GetProperty("languageIntroductions").EnumerateArray());
        Assert.Equal("en", introduced.GetProperty("language").GetString());
        Assert.Equal("en", introduced.GetProperty("wireTag").GetString());
        Assert.Equal("add", Assert.Single(draft.GetProperty("edits").EnumerateArray()).GetProperty("op").GetString());

        ContentAuditEntry audited = (await _harness.Store.ListAuditAsync(default, 0, 0, 50))
            .Single(entry => entry.LanguageTag == "en");
        Assert.Equal(CatalogAdminActions.Actor, audited.Actor);
        Assert.Equal("oid:8f2c", audited.Operator);
        Assert.Equal("Stone Sword", audited.AfterValue);
        Assert.Equal("new sword", audited.Note);
    }

    /// <summary>
    /// A text-only request needs no <c>edits</c> at all, and a RETIRED row still takes its text without its
    /// retirement changing.
    /// </summary>
    [Fact]
    public async Task Edit_TextOnly_ReachesAPublishedAndARetiredRow()
    {
        await _harness.PublishThingsAsync("stone_sword", "oak_shield");
        int shield = await _harness.IdOfAsync("oak_shield");
        await _harness.PublishAsync(ContentEdit.Retire(
            CatalogActionHarness.Thing, shield, new ContentKey("oak_shield"), ContentRetirePolicy.Placeholder, 0));

        JsonElement body = await _harness.OkAsync("catalog-edit", """
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "EN-us", "value": "Stone Sword" },
            { "op": "set", "typeKey": "thing", "key": "oak_shield", "field": "name", "language": "en-us", "value": "" }
          ]
        }
        """);

        Assert.Equal(0, body.GetProperty("applied").GetInt32());
        Assert.Equal(2, body.GetProperty("textApplied").GetInt32());
        Assert.Equal(0, body.GetProperty("draft").GetProperty("editCount").GetInt32());

        JsonElement draft = await _harness.OkAsync("catalog-draft", null);
        string[] languages = draft.GetProperty("textEdits").EnumerateArray()
            .Select(static edit => edit.GetProperty("language").GetString()!).ToArray();
        Assert.Equal(new[] { "en-us", "en-us" }, languages);
        Assert.Equal(string.Empty, draft.GetProperty("textEdits")[1].GetProperty("value").GetString());
        Assert.True((await _harness.Store.ListRowsAsync(CatalogActionHarness.Thing, 0, "oak_shield", true, 0, 5))
            .Rows.Single().IsRetired);
    }

    /// <summary>
    /// A <c>textEdits</c> that is not a non-empty array, alone, is a malformed REQUEST carrying no finding,
    /// which is a different answer from content the boundary refused.
    /// </summary>
    [Theory]
    [InlineData("""{ "textEdits": 7 }""")]
    [InlineData("""{ "textEdits": {} }""")]
    [InlineData("""{ "textEdits": [] }""")]
    [InlineData("""{ "edits": [], "textEdits": [] }""")]
    public async Task Edit_WithMalformedTextEdits_IsAMalformedRequestRatherThanAFinding(string request)
    {
        JsonElement body = await _harness.RefusedAsync("catalog-edit", request);

        Assert.Equal(CatalogRequest.MalformedRequestReason, body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("findingCount").GetInt32());
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// Every finding across the row edits AND the text edits comes back in one refusal, in request order,
    /// and nothing at all is applied.
    /// </summary>
    [Fact]
    public async Task Edit_CarriesEveryFindingAcrossRowsAndText()
    {
        await _harness.PublishThingsAsync("stone_sword");
        int sword = await _harness.IdOfAsync("stone_sword");
        int audited = (await _harness.Store.ListAuditAsync(default, 0, 0, 500)).Count;

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        {
          "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "valeu": 45 } } ],
          "textEdits": [
            7,
            { "op": "set", "typeKey": "nope", "key": "stone_sword", "field": "name", "language": "en", "value": "x" },
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "value", "language": "en", "value": "x" },
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en_US", "value": "x" },
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en" },
            { "op": "set", "typeKey": "thing", "key": "ghost_sword", "field": "name", "language": "en", "value": "x" },
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "de",
              "value": "{{new string('a', ContentTextEdit.MaxValueBytes + 1)}}" }
          ]
        }
        """);

        Assert.Equal(CatalogEditParser.Reason, body.GetProperty("reason").GetString());
        Assert.Equal(
            new[]
            {
                "KEC0004", "KEC0004", "KEC0004",
                ContentAuthoringException.TextTargetIneligibleReason,
                CatalogTextEditParser.LanguageCode,
                "KEC0004", "KEC0006",
                ContentAuthoringException.TextBoundsReason,
            },
            CatalogActionHarness.Codes(body));
        string messages = CatalogActionHarness.Messages(body);
        Assert.Contains("Text edit 0", messages, StringComparison.Ordinal);
        Assert.Contains("ghost_sword", messages, StringComparison.Ordinal);
        Assert.Contains("en_US", messages, StringComparison.Ordinal);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
        Assert.Equal(audited, (await _harness.Store.ListAuditAsync(default, 0, 0, 500)).Count);
    }

    /// <summary>
    /// Two spellings of ONE canonical target in one request are refused rather than letting the batch depend
    /// on its own order.
    /// </summary>
    [Fact]
    public async Task Edit_NamingOneCanonicalTargetTwice_IsRefused()
    {
        await _harness.PublishThingsAsync("stone_sword");

        JsonElement body = await _harness.RefusedAsync("catalog-edit", """
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en-US", "value": "a" },
            { "op": "remove", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "EN-us" }
          ]
        }
        """);

        Assert.Equal(new[] { CatalogTextEditParser.DuplicateCode }, CatalogActionHarness.Codes(body));
        Assert.Contains("Text edit 0", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A Remove in a language neither the base nor the draft declares is refused, while a Remove in a
    /// language an earlier Set of the same request introduced is an idempotent no-op.
    /// </summary>
    [Fact]
    public async Task Edit_RemovingInAnUndeclaredLanguage_IsRefusedAndInADeclaredOneIsIdempotent()
    {
        await _harness.PublishThingsAsync("stone_sword", "oak_shield");

        JsonElement refused = await _harness.RefusedAsync("catalog-edit", """
        { "textEdits": [ { "op": "remove", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "fr" } ] }
        """);
        Assert.Equal(
            new[] { ContentAuthoringException.TextLanguageUndeclaredReason }, CatalogActionHarness.Codes(refused));
        Assert.Null(await _harness.Store.GetOpenDraftAsync());

        JsonElement accepted = await _harness.OkAsync("catalog-edit", """
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "fr", "value": "Epee" },
            { "op": "remove", "typeKey": "thing", "key": "oak_shield", "field": "name", "language": "fr" }
          ]
        }
        """);
        Assert.Equal(1, accepted.GetProperty("draft").GetProperty("textEditCount").GetInt32());
        Assert.Equal(1, accepted.GetProperty("draft").GetProperty("languageIntroductionCount").GetInt32());
    }

    /// <summary>Text needs a CLIENT type AND a CLIENT localized text marker, checked at both levels.</summary>
    [Theory]
    [InlineData(SecretTypeKey, "name", ContentAuthoringException.TextTargetIneligibleReason)]
    [InlineData(LoreTypeKey, "gm_note", ContentAuthoringException.TextTargetIneligibleReason)]
    [InlineData("thing", "value", ContentAuthoringException.TextTargetIneligibleReason)]
    [InlineData("thing", "no_such_field", "KEC0004")]
    public async Task Edit_TextNeedsAClientTypeAndAClientMarker(string typeKey, string field, string code)
    {
        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        { "textEdits": [ { "op": "set", "typeKey": "{{typeKey}}", "key": "anything", "field": "{{field}}", "language": "en", "value": "x" } ] }
        """);

        Assert.Equal(new[] { code }, CatalogActionHarness.Codes(body));
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// A JSON escape spelling an unpaired surrogate is valid JSON that decodes to no string. It is a finding
    /// filed with the rest, <c>text-bounds</c> in the value and <c>KEC0004</c> in a member naming the target,
    /// and never a server fault. Nothing is applied.
    /// </summary>
    [Theory]
    [InlineData("value", ContentAuthoringException.TextBoundsReason)]
    [InlineData("op", "KEC0004")]
    [InlineData("typeKey", "KEC0004")]
    [InlineData("key", "KEC0004")]
    [InlineData("field", "KEC0004")]
    [InlineData("language", "KEC0004")]
    public async Task Edit_WithAnUnpairedSurrogateInAMember_IsAFindingAndAppliesNothing(string member, string code)
    {
        await _harness.PublishThingsAsync("stone_sword");
        var entry = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "set",
            ["typeKey"] = "thing",
            ["key"] = "stone_sword",
            ["field"] = "name",
            ["language"] = "en",
            ["value"] = "Stone Sword",
        };
        entry[member] = @"half \ud800 pair";
        string members = string.Join(", ", entry.Select(static pair => $"\"{pair.Key}\": \"{pair.Value}\""));

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""{ "textEdits": [ { {{members}} } ] }""");

        Assert.Equal(CatalogEditParser.Reason, body.GetProperty("reason").GetString());
        Assert.Equal(new[] { code }, CatalogActionHarness.Codes(body));
        string messages = CatalogActionHarness.Messages(body);
        Assert.Contains("unpaired surrogate", messages, StringComparison.Ordinal);
        Assert.Contains("'" + member + "'", messages, StringComparison.Ordinal);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// The derived <c>type.key.field</c> key is bounded at 192 UTF-8 BYTES. A key deriving exactly 192 passes
    /// the bound and is refused only for naming no row, while a key of fewer characters but more bytes is
    /// <c>text-bounds</c>.
    /// </summary>
    [Fact]
    public async Task Edit_WhoseDerivedKeyExceeds192Utf8Bytes_IsTextBounds()
    {
        string atBound = new('a', ContentTextKey.MaxKeyLength - "thing..name".Length);
        string overInBytes = new('é', (atBound.Length + 2) / 2);

        JsonElement body = await _harness.RefusedAsync("catalog-edit", $$"""
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "{{atBound}}", "field": "name", "language": "en", "value": "x" },
            { "op": "set", "typeKey": "thing", "key": "{{overInBytes}}", "field": "name", "language": "en", "value": "x" }
          ]
        }
        """);

        Assert.Equal(new[] { "KEC0006", ContentAuthoringException.TextBoundsReason }, CatalogActionHarness.Codes(body));
        Assert.Contains("Text edit 1 derives a key of 193 UTF-8 bytes", CatalogActionHarness.Messages(body), StringComparison.Ordinal);
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
    }

    /// <summary>
    /// Text may name the copy key of a fork the open draft already holds, before publish gives the copy a row,
    /// just as it may name a pending add.
    /// </summary>
    [Fact]
    public async Task Edit_TextNamingAForkKeyPendingInTheOpenDraft_IsAccepted()
    {
        await _harness.PublishThingsAsync("fire_mod");
        int mod = await _harness.IdOfAsync("fire_mod");
        await _harness.OkAsync("catalog-edit", $$"""
        {
          "edits": [
            { "op": "fork", "typeKey": "thing", "id": {{mod}},
              "forkKey": "fire_mod_legacy", "flagField": "legacy", "fields": { "value": 6000 } }
          ]
        }
        """);

        JsonElement body = await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "fire_mod_legacy", "field": "name", "language": "en", "value": "Old Fire Mod" } ] }
        """);

        Assert.Equal(1, body.GetProperty("textApplied").GetInt32());
        Assert.Equal(1, body.GetProperty("draft").GetProperty("editCount").GetInt32());
        JsonElement text = Assert.Single((await _harness.OkAsync("catalog-draft", null)).GetProperty("textEdits").EnumerateArray());
        Assert.Equal("fire_mod_legacy", text.GetProperty("key").GetString());
        Assert.Equal("thing.fire_mod_legacy.name", text.GetProperty("derivedKey").GetString());
    }

    /// <summary>
    /// A request whose SECOND target fails, in the store or at the boundary, leaves the rows, the draft's
    /// row, text and introduction categories, and the audit exactly as they were.
    /// </summary>
    [Fact]
    public async Task Edit_WhoseSecondTargetFails_LeavesRowsDraftAndAuditUnchanged()
    {
        await _harness.PublishThingsAsync("stone_sword", "oak_shield");
        int sword = await _harness.IdOfAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", $$"""
        {
          "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone" } ]
        }
        """);
        string before = (await _harness.OkAsync("catalog-draft", null)).GetRawText();
        int audited = (await _harness.Store.ListAuditAsync(default, 0, 0, 500)).Count;

        // The store refuses the second target: the draft already holds an update for that row.
        JsonElement collision = await _harness.ConflictAsync("catalog-edit", $$"""
        {
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "oak_shield", "field": "name", "language": "fr", "value": "Bouclier" } ],
          "edits": [ { "op": "retire", "typeKey": "thing", "id": {{sword}}, "policy": "placeholder" } ]
        }
        """);
        Assert.Equal(ContentAuthoringException.EditTargetCollisionReason, collision.GetProperty("reason").GetString());

        // The boundary refuses the second target: no such row.
        await _harness.RefusedAsync("catalog-edit", """
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "oak_shield", "field": "name", "language": "fr", "value": "Bouclier" },
            { "op": "set", "typeKey": "thing", "key": "ghost", "field": "name", "language": "fr", "value": "x" }
          ]
        }
        """);

        Assert.Equal(before, (await _harness.OkAsync("catalog-draft", null)).GetRawText());
        Assert.Equal(audited, (await _harness.Store.ListAuditAsync(default, 0, 0, 500)).Count);
        Assert.Equal(2, (await _harness.Store.ListRowsAsync(CatalogActionHarness.Thing, 0, null, true, 0, 50)).Total);
    }

    /// <summary>A draft a publish is holding refuses text with the actionable 409, never a malformed refusal.</summary>
    [Fact]
    public async Task Edit_WhileAPublishHoldsTheDraft_IsTheFrozen409()
    {
        await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "a" } ] }
        """);
        await _harness.Store.FreezeChangesAsync(await _harness.Store.GetActiveVersionAsync());

        JsonElement body = await _harness.ConflictAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "b" } ] }
        """);

        Assert.Equal(ContentAuthoringException.PublishInProgressReason, body.GetProperty("reason").GetString());
    }

    /// <summary>
    /// A store without the text companion answers text with a TYPED capability refusal and applies nothing,
    /// while its row-only edits keep working exactly as before.
    /// </summary>
    [Fact]
    public async Task Edit_WithTextAgainstAStoreLackingTheCompanion_IsATypedCapabilityRefusal()
    {
        await _harness.PublishThingsAsync("stone_sword");
        var rowOnly = new RowOnlyStore(_harness.Store);
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, rowOnly, _harness.Registry);

        AdminActionResult refused = await CatalogActionHarness.DispatchAsync(admin, "catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "a" } ] }
        """);
        Assert.Equal(AdminActionStatus.BadRequest, refused.Status);
        JsonElement body = CatalogActionHarness.Wire(refused);
        Assert.Equal(ContentAuthoringException.TextOperationUnavailableReason, body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("findingCount").GetInt32());
        Assert.Null(await _harness.Store.GetOpenDraftAsync());

        int sword = await _harness.IdOfAsync("stone_sword");
        AdminActionResult rows = await CatalogActionHarness.DispatchAsync(admin, "catalog-edit", $$"""
        { "edits": [ { "op": "update", "typeKey": "thing", "id": {{sword}}, "fields": { "value": 4500 } } ] }
        """);
        Assert.Equal(AdminActionStatus.Ok, rows.Status);
    }

    /// <summary>
    /// A text-bearing draft is discarded through the atomic expected-draft discard, and the answer and the
    /// audit name every category that went with it.
    /// </summary>
    [Fact]
    public async Task Discard_OfATextBearingDraft_DiscardsItWhole()
    {
        await _harness.OkAsync("catalog-edit", """
        {
          "edits": [ { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 1, "stackable": false } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "a" } ]
        }
        """);

        JsonElement body = await _harness.OkAsync("catalog-discard", """{ "operator": "oid:8f2c" }""");

        Assert.True(body.GetProperty("discarded").GetBoolean());
        Assert.Equal(1, body.GetProperty("editCount").GetInt32());
        Assert.Equal(1, body.GetProperty("textEditCount").GetInt32());
        Assert.Equal(1, body.GetProperty("languageIntroductionCount").GetInt32());
        Assert.Null(await _harness.Store.GetOpenDraftAsync());
        string[] categories = (await _harness.Store.ListAuditAsync(default, 0, 0, 50))
            .Where(static entry => entry.Action == ContentAuditActions.DraftDiscard)
            .Select(static entry => entry.FieldName)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { string.Empty, "language-introductions", "text-edits" }, categories);
    }

    /// <summary>
    /// A rival translation landing between the read and the discard makes the expected-draft discard return
    /// false, which is a typed 409 that keeps the rival's text, never a 200 claiming the draft is gone.
    /// </summary>
    [Fact]
    public async Task Discard_RacedByARivalTranslation_IsA409AndKeepsTheRival()
    {
        await _harness.PublishThingsAsync("stone_sword");
        await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "a" } ] }
        """);
        var rival = new RivalTextStore(_harness.Store);
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, rival, _harness.Registry);

        AdminActionResult result = await CatalogActionHarness.DispatchAsync(admin, "catalog-discard", "{}");

        Assert.Equal(AdminActionStatus.Conflict, result.Status);
        Assert.Equal(
            ContentAuthoringException.TextStateMismatchReason,
            CatalogActionHarness.Wire(result).GetProperty("reason").GetString());
        ContentDraft? kept = await _harness.Store.GetOpenDraftAsync();
        Assert.NotNull(kept);
        Assert.Contains(kept!.TextState!.Edits, static edit => edit.Target.Language == "fr");
    }

    /// <summary>
    /// The published history reads back its text: the row's live values with their derived keys, and the
    /// language each audited text change was in.
    /// </summary>
    [Fact]
    public async Task Get_ShowsTheRowsTextAndTheLanguageOfItsAudit()
    {
        await PublishNamedSwordAsync("Stone Sword");

        JsonElement body = await _harness.OkAsync(
            "catalog-get", """{ "typeKey": "thing", "key": "stone_sword", "includeAudit": true }""");

        Assert.True(body.GetProperty("textKnown").GetBoolean());
        JsonElement value = Assert.Single(body.GetProperty("text").EnumerateArray());
        Assert.Equal("name", value.GetProperty("field").GetString());
        Assert.Equal("en", value.GetProperty("language").GetString());
        Assert.Equal("Stone Sword", value.GetProperty("value").GetString());
        Assert.Equal("thing.stone_sword.name", value.GetProperty("derivedKey").GetString());
        Assert.Contains(
            body.GetProperty("audit").EnumerateArray(),
            static entry => entry.GetProperty("language").GetString() == "en");
    }

    /// <summary>
    /// The diff shows text beside the rows, against the draft-applied candidate and between two published
    /// versions, including the language a publish introduced.
    /// </summary>
    [Fact]
    public async Task Diff_ShowsTextAgainstTheCandidateAndBetweenVersions()
    {
        int first = await PublishNamedSwordAsync("Stone Sword");
        await _harness.OkAsync("catalog-edit", """
        {
          "textEdits": [
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Blade" },
            { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "fr", "value": "Epee" }
          ]
        }
        """);

        JsonElement provisional = await _harness.OkAsync("catalog-diff", """{ "to": 0 }""");
        AssertTextDiff(provisional);

        JsonElement published = await _harness.OkAsync(
            "catalog-publish", $$"""{ "expectedBaseVersion": {{first}} }""");
        int second = published.GetProperty("version").GetInt32();
        JsonElement between = await _harness.OkAsync(
            "catalog-diff", $$"""{ "from": {{first}}, "to": {{second}} }""");
        AssertTextDiff(between);
        Assert.Empty(between.GetProperty("changes").EnumerateArray());
    }

    /// <summary>
    /// A candidate diff from an OLDER version compares that version's text with the active text plus the
    /// draft, so a string published in between is shown just as the row half shows a row published in
    /// between, and a pending value replaces the published one.
    /// </summary>
    [Fact]
    public async Task Diff_FromAnOlderVersionToTheCandidate_IncludesTextPublishedSince()
    {
        int first = await PublishNamedSwordAsync("Stone Sword");
        await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "fr", "value": "Epee" } ] }
        """);
        await _harness.OkAsync("catalog-publish", $$"""{ "expectedBaseVersion": {{first}} }""");
        await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Blade" } ] }
        """);

        AssertTextDiff(await _harness.OkAsync("catalog-diff", $$"""{ "from": {{first}}, "to": 0 }"""));
    }

    /// <summary>
    /// A rollback over text dispatches through the companion: the draft it builds restores the earlier
    /// value, and the answer counts the text it staged.
    /// </summary>
    [Fact]
    public async Task Rollback_OverText_RestoresTheEarlierValue()
    {
        int first = await PublishNamedSwordAsync("Stone Sword");
        await _harness.OkAsync("catalog-edit", """
        { "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": "Stone Blade" } ] }
        """);
        await _harness.OkAsync("catalog-publish", $$"""{ "expectedBaseVersion": {{first}} }""");

        JsonElement body = await _harness.OkAsync(
            "catalog-rollback", $$"""{ "operator": "oid:8f2c", "toVersion": {{first}} }""");

        Assert.True(body.GetProperty("draftCreated").GetBoolean());
        Assert.Equal(1, body.GetProperty("textEditCount").GetInt32());
        JsonElement draft = await _harness.OkAsync("catalog-draft", null);
        Assert.Equal("Stone Sword", Assert.Single(draft.GetProperty("textEdits").EnumerateArray()).GetProperty("value").GetString());
    }

    /// <summary>
    /// Export writes the text section and import carries it into an EMPTY store through the companion, with
    /// every language and value intact.
    /// </summary>
    [Fact]
    public async Task ExportThenImport_CarriesTextIntoAnEmptyStore()
    {
        int version = await PublishNamedSwordAsync("Stone Sword");

        JsonElement exported = await _harness.OkAsync("catalog-export", $$"""{ "version": {{version}} }""");
        Assert.Equal(ContentBundle.TextFormatVersion, exported.GetProperty("formatVersion").GetInt32());
        Assert.Equal(1, exported.GetProperty("languageCount").GetInt32());
        Assert.Equal(1, exported.GetProperty("textValueCount").GetInt32());

        using var fresh = new CatalogActionHarness(extraTypes: RegisterIneligibleTypes);
        JsonElement imported = await fresh.OkAsync(
            "catalog-import", $$"""{ "note": "seed", "bundle": {{exported.GetProperty("bundle").GetRawText()}} }""");
        Assert.Equal(1, imported.GetProperty("languagesImported").GetInt32());
        Assert.Equal(1, imported.GetProperty("textValuesImported").GetInt32());

        ContentVersionTextSnapshot text = await fresh.Store.ReadTextSnapshotAsync(imported.GetProperty("version").GetInt32());
        Assert.Equal("Stone Sword", Assert.Single(text.Revisions).Value);
        Assert.Equal("en", Assert.Single(text.Languages).WireTag);
    }

    /// <summary>
    /// A text-bearing bundle into a store without the companion is a typed capability refusal that stages
    /// nothing, never a row-only import that drops the text.
    /// </summary>
    [Fact]
    public async Task Import_OfATextBundleIntoAStoreLackingTheCompanion_IsATypedCapabilityRefusal()
    {
        int version = await PublishNamedSwordAsync("Stone Sword");
        string bundle = (await _harness.OkAsync("catalog-export", $$"""{ "version": {{version}} }"""))
            .GetProperty("bundle").GetRawText();
        using var fresh = new CatalogActionHarness(extraTypes: RegisterIneligibleTypes);
        ServerAdmin admin = CatalogActionHarness.NewSurface();
        CatalogAdminActions.Register(admin, new RowOnlyStore(fresh.Store), fresh.Registry);

        AdminActionResult result = await CatalogActionHarness.DispatchAsync(
            admin, "catalog-import", $$"""{ "bundle": {{bundle}} }""");

        Assert.Equal(AdminActionStatus.BadRequest, result.Status);
        Assert.Equal(
            ContentAuthoringException.TextOperationUnavailableReason,
            CatalogActionHarness.Wire(result).GetProperty("reason").GetString());
        Assert.Equal(0, await fresh.Store.GetActiveVersionAsync());
    }

    /// <inheritdoc />
    public void Dispose() => _harness.Dispose();

    /// <summary>Publishes stone_sword with an English name through the real actions, and answers the version.</summary>
    async Task<int> PublishNamedSwordAsync(string name)
    {
        await _harness.OkAsync("catalog-edit", $$"""
        {
          "edits": [ { "op": "add", "typeKey": "thing", "key": "stone_sword", "fields": { "value": 4200, "stackable": false } } ],
          "textEdits": [ { "op": "set", "typeKey": "thing", "key": "stone_sword", "field": "name", "language": "en", "value": {{JsonSerializer.Serialize(name)}} } ]
        }
        """);
        JsonElement published = await _harness.OkAsync("catalog-publish", """{ "expectedBaseVersion": 0 }""");
        return published.GetProperty("version").GetInt32();
    }

    static void AssertTextDiff(JsonElement diff)
    {
        Assert.True(diff.GetProperty("textKnown").GetBoolean());
        JsonElement[] changes = diff.GetProperty("textChanges").EnumerateArray()
            .OrderBy(static change => change.GetProperty("language").GetString(), StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("en", changes[0].GetProperty("language").GetString());
        Assert.Equal("stone_sword", changes[0].GetProperty("key").GetString());
        Assert.Equal("Stone Sword", changes[0].GetProperty("before").GetString());
        Assert.Equal("Stone Blade", changes[0].GetProperty("after").GetString());
        Assert.Equal("fr", changes[1].GetProperty("language").GetString());
        Assert.Equal(JsonValueKind.Null, changes[1].GetProperty("before").ValueKind);
        Assert.Equal("Epee", changes[1].GetProperty("after").GetString());
        Assert.Equal(
            "fr",
            Assert.Single(diff.GetProperty("languagesIntroduced").EnumerateArray()).GetProperty("language").GetString());
    }

    static void RegisterIneligibleTypes(ContentTypeRegistry registry)
    {
        var secretType = new ContentTypeId(1026);
        ContentFieldSchema secret = new(new List<ContentFieldEntry>
        {
            new("name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, true),
        });
        registry.RegisterContentType(
            ContentRegistrationBand.Game, 1026, SecretTypeKey, new CatalogFixtureCodec(secretType, secret),
            null, secret, ContentVisibility.ServerOnly, 256);

        var loreType = new ContentTypeId(1027);
        ContentFieldSchema lore = new(new List<ContentFieldEntry>
        {
            new("name", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
            new("gm_note", ContentFieldKind.LocalizedTextKey, null, ContentVisibility.ServerOnly, true),
        });
        registry.RegisterContentType(
            ContentRegistrationBand.Game, 1027, LoreTypeKey, new CatalogFixtureCodec(loreType, lore),
            null, lore, ContentVisibility.Client, 256);
    }

    /// <summary>A store that forwards every row-only member and implements NO text companion.</summary>
    sealed class RowOnlyStore(IContentAuthoringStore inner) : ForwardingContentAuthoringStore(inner);

    /// <summary>
    /// A complete text store whose draft read lets a rival translation land right after it, which is the
    /// window between a discard's read and its atomic expected-draft comparison.
    /// </summary>
    sealed class RivalTextStore(InMemoryContentAuthoringStore inner)
        : ForwardingContentAuthoringStore(inner), IContentTextAuthoringStore
    {
        readonly InMemoryContentAuthoringStore _text = inner;

        public override async Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default)
        {
            ContentDraft? read = await _text.GetOpenDraftAsync(cancellationToken);
            var target = new ContentTextTarget(CatalogActionHarness.Thing, new ContentKey("stone_sword"), "name", "fr");
            await _text.ApplyChangesAsync(
                new ContentAuthoringChanges([], [ContentTextEdit.Set(target, "rival")]), "rival", "oid:rival", string.Empty, cancellationToken);
            return read;
        }

        public Task<ContentDraft> ApplyChangesAsync(
            ContentAuthoringChanges changes, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
            => _text.ApplyChangesAsync(changes, actor, operatorId, note, cancellationToken);

        public Task<ContentTextPublishSnapshot> FreezeChangesAsync(int expectedBaseVersion, CancellationToken cancellationToken = default)
            => _text.FreezeChangesAsync(expectedBaseVersion, cancellationToken);

        public Task<ContentVersionTextSnapshot> ReadTextSnapshotAsync(int versionNumber, CancellationToken cancellationToken = default)
            => _text.ReadTextSnapshotAsync(versionNumber, cancellationToken);

        public Task<ContentVersionRecord> CommitTextPublishAsync(
            ContentTextPublishPlan plan, ContentPublishRequest request, IPackVersionPointerStore? pointers, CancellationToken cancellationToken = default)
            => _text.CommitTextPublishAsync(plan, request, pointers, cancellationToken);

        public Task<bool> TryDiscardChangesAsync(
            ContentDraft expected, string actor, string operatorId, CancellationToken cancellationToken = default)
            => _text.TryDiscardChangesAsync(expected, actor, operatorId, cancellationToken);

        public Task<ContentPublishResult> ImportTextBundleAsync(
            ContentBundle bundle, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
            => _text.ImportTextBundleAsync(bundle, actor, operatorId, note, cancellationToken);

        public Task<ContentDraft> RollbackTextToAsync(
            int targetVersion, string actor, string operatorId, string note, CancellationToken cancellationToken = default)
            => _text.RollbackTextToAsync(targetVersion, actor, operatorId, note, cancellationToken);
    }
}
