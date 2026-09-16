using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
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
/// The five READ actions of spec 10.3, 10.4 and 10.7, over the in-memory authoring store and the real
/// <see cref="ServerAdmin"/> registry: <c>catalog-schema</c>, <c>catalog-list</c>, <c>catalog-get</c>,
/// <c>catalog-draft</c> and <c>catalog-versions</c>.
/// <para>
/// <b>Every fact is asserted against the JSON a console would receive</b> rather than against the payload
/// object, because the payload shape IS the contract here: one generic editor renders a type it has never
/// heard of out of these bodies, so a renamed property is a broken console rather than a refactor.
/// </para>
/// <para>
/// The registry is per instance and the store is in memory, so nothing here is process global and no
/// collection attribute is needed.
/// </para>
/// </summary>
public sealed class CatalogReadActionTests : IDisposable
{
    const ushort ThingTypeId = 1024;
    const string ThingTypeKey = "thing";
    const ushort TagTypeId = 1025;
    const string TagTypeKey = "tag";
    const string NameField = "name";
    const string ValueField = "value";
    const string StackableField = "stackable";
    const string TagsField = "tags";
    const string IconField = "icon";
    const string SortField = "sort";
    const string Actor = "catalog-read-actions";
    const string Operator = "oid:reader";
    const int ValueScale = 100;

    static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-read-actions-" + Guid.NewGuid().ToString("n"));
    readonly ContentTypeRegistry _registry = BuildRegistry();
    readonly ServerAdmin _admin = new(new NullAdminControllable());
    readonly InMemoryContentAuthoringStore _store;

    public CatalogReadActionTests()
    {
        _store = new InMemoryContentAuthoringStore(_registry, new FileSystemPackStore(_root));
        CatalogAdminActions.Register(_admin, _store, _registry);
    }

    /// <summary>
    /// The five names are registered under exactly the spec's spelling. A console binds to the NAME, so a
    /// typo here is a 404 at the one moment an operator is trying to look at content. The COUNT of names
    /// the helper leaves behind is <c>CatalogActionInventoryTests</c>'s fact rather than this one's.
    /// </summary>
    [Fact]
    public void TheFiveReadActions_AreRegisteredUnderTheirSpecNames()
    {
        string[] names = _admin.ActionNames.OrderBy(static name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "catalog-draft", "catalog-get", "catalog-list", "catalog-schema", "catalog-versions" },
            names.Where(static name => name is "catalog-draft" or "catalog-get" or "catalog-list"
                or "catalog-schema" or "catalog-versions").ToArray());
    }

    /// <summary>
    /// <c>catalog-schema</c> returns the FULL registered schema, which is what makes one generic editor
    /// render a type the console has never heard of. The derived localization key is the fact that matters
    /// most: it comes back with <c>derived</c> true and no value anywhere, so a console renders it read only
    /// instead of offering a text box for a value that is not the console's to set.
    /// </summary>
    [Fact]
    public async Task Schema_ReturnsEveryTypeAndMarksTheDerivedKeyReadOnly()
    {
        JsonElement body = await OkAsync("catalog-schema", null);

        Assert.Equal(ContentPackFormat.Generation, body.GetProperty("generation").GetInt32());
        JsonElement[] types = body.GetProperty("types").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { ThingTypeId, TagTypeId }.Select(static id => (int)id).ToArray(),
            types.Select(static t => t.GetProperty("typeId").GetInt32()).ToArray());

        JsonElement thing = types[0];
        Assert.Equal(ThingTypeKey, thing.GetProperty("typeKey").GetString());
        Assert.Equal("Client", thing.GetProperty("visibility").GetString());
        Assert.Equal(256, thing.GetProperty("chunkSlots").GetInt32());

        JsonElement name = Field(thing, NameField);
        Assert.Equal("LocalizedTextKey", name.GetProperty("kind").GetString());
        Assert.True(name.GetProperty("derived").GetBoolean());
        Assert.True(name.GetProperty("required").GetBoolean());
        Assert.False(name.TryGetProperty("value", out _), "a schema field carries no value, derived or not.");

        // Every other kind is authored, and says so, so a console never has to special case a kind name.
        JsonElement value = Field(thing, ValueField);
        Assert.Equal("ScaledInt", value.GetProperty("kind").GetString());
        Assert.False(value.GetProperty("derived").GetBoolean());
        Assert.Equal(ValueScale, value.GetProperty("scale").GetInt32());

        JsonElement tags = Field(thing, TagsField);
        Assert.Equal("TagList", tags.GetProperty("kind").GetString());
        Assert.Equal(TagTypeKey, tags.GetProperty("target").GetString());
        Assert.False(tags.GetProperty("required").GetBoolean());
    }

    /// <summary>
    /// <c>catalog-list</c> carries the version it read at, the TOTAL before paging, and the page. The total
    /// is what makes a console page rather than show a screenful and say nothing, which is the defect this
    /// response shape exists to avoid repeating.
    /// </summary>
    [Fact]
    public async Task List_CarriesTheVersionTheTotalAndThePage()
    {
        await SeedTagsAsync();
        int version = await PublishThingsAsync("alpha", "beta", "gamma", "delta");

        JsonElement body = await OkAsync("catalog-list", $$"""
            { "typeKey": "{{ThingTypeKey}}", "version": 0, "skip": 1, "take": 2 }
            """);

        Assert.Equal(version, body.GetProperty("version").GetInt32());
        Assert.Equal(4, body.GetProperty("total").GetInt32());
        Assert.Equal(1, body.GetProperty("skip").GetInt32());
        Assert.Equal(2, body.GetProperty("take").GetInt32());

        JsonElement[] rows = body.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "beta", "gamma" }, rows.Select(static r => r.GetProperty("key").GetString()!).ToArray());
        Assert.All(rows, static r => Assert.False(r.GetProperty("retired").GetBoolean()));
    }

    /// <summary>
    /// <c>take</c> is CAPPED at 500 server side and the echoed <c>take</c> reports the cap, so a console can
    /// see that its ask was clamped instead of concluding the catalog holds what one page happened to carry.
    /// </summary>
    [Fact]
    public async Task List_CapsTakeAtFiveHundredAndSaysSo()
    {
        await SeedTagsAsync();
        await PublishThingsAsync("alpha", "beta");

        JsonElement body = await OkAsync("catalog-list", $$"""
            { "typeKey": "{{ThingTypeKey}}", "take": 10000 }
            """);

        Assert.Equal(CatalogAdminActions.MaxPageSize, body.GetProperty("take").GetInt32());
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        Assert.Equal(2, body.GetProperty("rows").GetArrayLength());
    }

    /// <summary>
    /// The key prefix and the retired flag both filter, and a retired row is out of the page unless it is
    /// asked for. An operator looking at live content should not have to filter the retired rows out by eye.
    /// </summary>
    [Fact]
    public async Task List_FiltersByKeyPrefixAndHidesRetiredRowsUntilAsked()
    {
        await SeedTagsAsync();
        await PublishThingsAsync("stone_sword", "stone_axe", "iron_sword");
        await PublishAsync(ContentEdit.Retire(
            Thing, await IdOfAsync("stone_axe"), new ContentKey("stone_axe"), ContentRetirePolicy.Placeholder, 0));

        JsonElement live = await OkAsync("catalog-list", $$"""
            { "typeKey": "{{ThingTypeKey}}", "keyPrefix": "stone" }
            """);
        Assert.Equal(new[] { "stone_sword" }, Keys(live));

        JsonElement withRetired = await OkAsync("catalog-list", $$"""
            { "typeKey": "{{ThingTypeKey}}", "keyPrefix": "stone", "includeRetired": true }
            """);
        Assert.Equal(new[] { "stone_sword", "stone_axe" }, Keys(withRetired));
        Assert.True(withRetired.GetProperty("rows")[1].GetProperty("retired").GetBoolean());
    }

    /// <summary>
    /// A row's <c>name</c> comes back DERIVED and read only, and it is not a stored value: the edit that
    /// created the row never named it, and the store holds the marker absent. Every other kind renders
    /// through its own rule, which is the half of the contract a generic editor's cell renderers are written
    /// against.
    /// </summary>
    [Fact]
    public async Task List_RendersTheDerivedNameKeyAndEveryOtherKindThroughItsOwnRule()
    {
        int[] tagIds = await SeedTagsAsync();
        await PublishAsync(ContentEdit.Add(
            Thing,
            new ContentKey("stone_sword"),
            [
                new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 4250)),
                new ContentFieldEdit(StackableField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0)),
                new ContentFieldEdit(TagsField, ContentRowCodecBase.TagListValue(tagIds)),
                new ContentFieldEdit(IconField, ContentFieldValue.OfBytes(
                    ContentFieldKind.OpaqueBytes, new byte[] { 0x0a, 0xff })),
            ]));

        JsonElement body = await OkAsync("catalog-list", $$"""
            { "typeKey": "{{ThingTypeKey}}" }
            """);
        JsonElement fields = body.GetProperty("rows")[0].GetProperty("fields");

        // Derived from the type key, the row key and the field name, which is the ONE derivation there is.
        Assert.Equal("thing.stone_sword.name", fields.GetProperty(NameField).GetString());
        ContentRowPage stored = await _store.ListRowsAsync(Thing, 0, null, false, 0, 10);
        Assert.True(
            stored.Rows[0].Fields[FieldIndex(NameField)].IsAbsent,
            "the derived key is read only and is never a stored value.");

        // The STORED integer, which is the value times the schema's scale. The scale is on the schema, so a
        // console divides rather than being handed a rounded number it cannot get back.
        Assert.Equal(4250, fields.GetProperty(ValueField).GetInt64());
        Assert.False(fields.GetProperty(StackableField).GetBoolean());
        Assert.Equal(tagIds, fields.GetProperty(TagsField).EnumerateArray().Select(static t => t.GetInt32()).ToArray());
        Assert.Equal("0aff", fields.GetProperty(IconField).GetString());
    }

    /// <summary>
    /// <c>catalog-get</c> returns one row plus its full version HISTORY, which is the temporal model's
    /// payoff: "when did this price change and what was it before" is answered out of the row table rather
    /// than reconstructed from an audit.
    /// </summary>
    [Fact]
    public async Task Get_ReturnsOneRowPlusItsFullHistory()
    {
        await SeedTagsAsync();
        int first = await PublishThingsAsync("stone_sword");
        int id = await IdOfAsync("stone_sword");
        int second = await PublishAsync(ContentEdit.Update(
            Thing,
            id,
            new ContentKey("stone_sword"),
            [new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 9900))]));

        JsonElement body = await OkAsync("catalog-get", $$"""
            { "typeKey": "{{ThingTypeKey}}", "id": {{id}} }
            """);

        Assert.Equal(ThingTypeKey, body.GetProperty("typeKey").GetString());
        Assert.Equal(id, body.GetProperty("id").GetInt32());
        Assert.Equal("stone_sword", body.GetProperty("key").GetString());
        Assert.Equal(9900, body.GetProperty("row").GetProperty("fields").GetProperty(ValueField).GetInt64());

        JsonElement[] history = body.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.Equal(first, history[0].GetProperty("validFrom").GetInt32());
        Assert.Equal(second, history[0].GetProperty("replacedIn").GetInt32());
        Assert.Equal(4200, history[0].GetProperty("fields").GetProperty(ValueField).GetInt64());
        Assert.Equal(second, history[1].GetProperty("validFrom").GetInt32());
        Assert.Equal(JsonValueKind.Null, history[1].GetProperty("replacedIn").ValueKind);
        Assert.Equal(9900, history[1].GetProperty("fields").GetProperty(ValueField).GetInt64());
    }

    /// <summary>
    /// A get by KEY answers with the same row a get by id does. An operator reads a key off a design
    /// document and an id off a log line, so both have to reach the row.
    /// </summary>
    [Fact]
    public async Task Get_ByKey_ResolvesTheSameRowAsById()
    {
        await SeedTagsAsync();
        await PublishThingsAsync("stone_sword", "stone_sword_two");
        int id = await IdOfAsync("stone_sword");

        JsonElement byKey = await OkAsync("catalog-get", $$"""
            { "typeKey": "{{ThingTypeKey}}", "key": "stone_sword" }
            """);
        JsonElement byId = await OkAsync("catalog-get", $$"""
            { "typeKey": "{{ThingTypeKey}}", "id": {{id}} }
            """);

        Assert.Equal(id, byKey.GetProperty("id").GetInt32());
        Assert.Equal("stone_sword", byKey.GetProperty("key").GetString());
        Assert.Equal(byId.GetProperty("row").ToString(), byKey.GetProperty("row").ToString());
    }

    /// <summary>
    /// The audit trail is OPT IN and it is FILTERED to the row asked about. The seam's audit read takes a
    /// type and a definition id with 0 meaning all, and an unfiltered trail on a row page would tell an
    /// operator what changed somewhere rather than what changed here.
    /// </summary>
    [Fact]
    public async Task Get_WithIncludeAudit_ReturnsOnlyThatRowsEntries()
    {
        await SeedTagsAsync();
        await PublishThingsAsync("stone_sword", "iron_sword");
        int stoneSword = await IdOfAsync("stone_sword");
        int ironSword = await IdOfAsync("iron_sword");

        // Draft edits against two different published rows, so the ledger holds entries for both ids.
        await _store.ApplyEditsAsync(
            [
                ContentEdit.Update(Thing, stoneSword, new ContentKey("stone_sword"),
                    [new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 5000))]),
                ContentEdit.Update(Thing, ironSword, new ContentKey("iron_sword"),
                    [new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 6000))]),
            ],
            Actor,
            Operator,
            "price pass");

        JsonElement without = await OkAsync("catalog-get", $$"""
            { "typeKey": "{{ThingTypeKey}}", "id": {{stoneSword}} }
            """);
        Assert.Equal(JsonValueKind.Null, without.GetProperty("audit").ValueKind);

        JsonElement body = await OkAsync("catalog-get", $$"""
            { "typeKey": "{{ThingTypeKey}}", "id": {{stoneSword}}, "includeAudit": true }
            """);
        JsonElement[] audit = body.GetProperty("audit").EnumerateArray().ToArray();

        Assert.NotEmpty(audit);
        Assert.All(audit, entry => Assert.Equal(stoneSword, entry.GetProperty("id").GetInt32()));
        Assert.All(audit, entry => Assert.Equal(ThingTypeKey, entry.GetProperty("typeKey").GetString()));
        JsonElement newest = audit[0];
        Assert.Equal(ContentAuditActions.DraftEdit, newest.GetProperty("action").GetString());
        Assert.Equal(ValueField, newest.GetProperty("field").GetString());
        Assert.Equal("5000", newest.GetProperty("after").GetString());
        Assert.Equal(Operator, newest.GetProperty("operator").GetString());
        Assert.Equal("price pass", newest.GetProperty("note").GetString());
    }

    /// <summary>
    /// <c>catalog-draft</c> returns the open draft with its edits EXPANDED, so a console can show a pending
    /// changes panel, and answers a null draft rather than a 404 when none is open.
    /// </summary>
    [Fact]
    public async Task Draft_ReturnsTheOpenDraftWithItsEditsExpanded()
    {
        await SeedTagsAsync();
        JsonElement empty = await OkAsync("catalog-draft", null);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("draft").ValueKind);
        Assert.Empty(empty.GetProperty("edits").EnumerateArray());

        int version = await PublishThingsAsync("stone_sword");
        int stoneSword = await IdOfAsync("stone_sword");
        await _store.ApplyEditsAsync(
            [
                ContentEdit.Update(Thing, stoneSword, new ContentKey("stone_sword"),
                    [new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, 4500))]),
                ContentEdit.Add(Thing, new ContentKey("iron_sword"), ThingFields()),
            ],
            Actor,
            Operator,
            "autumn price pass");

        JsonElement body = await OkAsync("catalog-draft", null);
        JsonElement draft = body.GetProperty("draft");
        Assert.Equal(version, draft.GetProperty("baseVersion").GetInt32());
        Assert.Equal(2, draft.GetProperty("editCount").GetInt32());
        // The store writes the ACTOR here, not the operator, which contradicts the seam's own doc comment and
        // the spec's oid: example. Asserted as it behaves, and the divergence is filed as #945.
        Assert.Equal(Actor, draft.GetProperty("openedBy").GetString());
        Assert.Equal("autumn price pass", draft.GetProperty("note").GetString());
        Assert.False(draft.GetProperty("frozen").GetBoolean());

        JsonElement[] edits = body.GetProperty("edits").EnumerateArray().ToArray();
        Assert.Equal(2, edits.Length);
        Assert.Equal(new[] { "update", "add" }, edits.Select(static e => e.GetProperty("op").GetString()!).ToArray());
        Assert.Equal(stoneSword, edits[0].GetProperty("id").GetInt32());
        Assert.Equal(4500, edits[0].GetProperty("fields").GetProperty(ValueField).GetInt64());
        // An add's id is allocated at PUBLISH, so the edit carries 0 and the console shows no number yet.
        Assert.Equal(0, edits[1].GetProperty("id").GetInt32());
        Assert.Equal("iron_sword", edits[1].GetProperty("key").GetString());
    }

    /// <summary>
    /// <c>catalog-versions</c> returns the active version, the pinned version and every version record. It
    /// is the read a pin and a rollback are both decided from, so it carries both manifest hashes and both
    /// minimum builds rather than a summary of them.
    /// </summary>
    [Fact]
    public async Task Versions_ReturnsTheActiveVersionThePinAndEveryRecord()
    {
        await SeedTagsAsync();
        await PublishThingsAsync("stone_sword");
        int second = await PublishThingsAsync("iron_sword");
        await _store.SetPinnedVersionAsync(second - 1, Actor, Operator);

        JsonElement body = await OkAsync("catalog-versions", null);

        Assert.Equal(second, body.GetProperty("activeVersion").GetInt32());
        Assert.Equal(second - 1, body.GetProperty("pinnedVersion").GetInt32());

        JsonElement[] versions = body.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal(second, versions.Length);
        // Newest first, which is the order the seam lists them in and the order a console shows them in.
        Assert.Equal(second, versions[0].GetProperty("version").GetInt32());
        Assert.Equal(second - 1, versions[0].GetProperty("baseVersion").GetInt32());
        Assert.Equal(64, versions[0].GetProperty("serverManifestHash").GetString()!.Length);
        Assert.Equal(64, versions[0].GetProperty("clientManifestHash").GetString()!.Length);
        Assert.Equal(ContentPackFormat.Generation, versions[0].GetProperty("formatGeneration").GetInt32());
        // The actor again rather than the operator, the other half of #945.
        Assert.Equal(Actor, versions[0].GetProperty("publishedBy").GetString());
    }

    /// <summary>
    /// An unpinned store answers a null pin rather than a zero. Zero is a version number nothing ever
    /// publishes, so returning it would read as a pin at a version that cannot exist.
    /// </summary>
    [Fact]
    public async Task Versions_OnAnUnpublishedStore_AnswersZeroAndNoPin()
    {
        JsonElement body = await OkAsync("catalog-versions", null);

        Assert.Equal(0, body.GetProperty("activeVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("pinnedVersion").ValueKind);
        Assert.Empty(body.GetProperty("versions").EnumerateArray());
    }

    /// <summary>
    /// Every refusal on these reads is a 400 carrying a reason, never a throw that dispatch turns into a 500.
    /// An operator typing a type key by hand is the common case, so the message names what was not found.
    /// </summary>
    [Theory]
    [InlineData("catalog-list", null, "body")]
    [InlineData("catalog-list", """{ "version": 0 }""", "typeKey")]
    [InlineData("catalog-list", """{ "typeKey": "nope" }""", "nope")]
    [InlineData("catalog-list", """{ "typeKey": 7 }""", "typeKey")]
    [InlineData("catalog-list", """{ "typeKey": "thing", "skip": -1 }""", "skip")]
    [InlineData("catalog-list", """{ "typeKey": "thing", "take": 0 }""", "take")]
    [InlineData("catalog-list", """{ "typeKey": "thing", "version": -3 }""", "version")]
    [InlineData("catalog-get", null, "body")]
    [InlineData("catalog-get", """{ "typeKey": "thing" }""", "id")]
    [InlineData("catalog-get", """{ "typeKey": "thing", "id": 0 }""", "id")]
    [InlineData("catalog-get", """{ "typeKey": "thing", "id": 99 }""", "99")]
    [InlineData("catalog-get", """{ "typeKey": "thing", "key": "absent" }""", "absent")]
    public async Task ARefusedRead_IsABadRequestNamingWhatWasWrong(string action, string? body, string mentions)
    {
        AdminActionResult result = await CallAsync(action, body);

        Assert.Equal(AdminActionStatus.BadRequest, result.Status);
        Assert.NotNull(result.Error);
        Assert.Contains(mentions, result.Error!, StringComparison.Ordinal);

        // The BARE STRING, which is what spec 10.2 assigns the reads and is the shape the endpoint turns into
        // { error }. The ELEVEN mutating actions carry the object body instead, and the two are pinned here so
        // the split stays a decision rather than something a later refusal drifts across.
        Assert.Null(result.Payload);
    }

    /// <summary>
    /// Registering twice throws, which is <see cref="ServerAdmin"/>'s own duplicate-name rule reaching the
    /// catalog helper. A game that calls the helper twice has a startup defect rather than a silently
    /// half-registered surface.
    /// </summary>
    [Fact]
    public void RegisteringTwice_Throws()
        => Assert.Throws<ArgumentException>(() => CatalogAdminActions.Register(_admin, _store, _registry));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }

    static ContentTypeId Thing => new(ThingTypeId);

    static ContentTypeId Tag => new(TagTypeId);

    static ContentTypeRegistry BuildRegistry()
    {
        var registry = new ContentTypeRegistry();
        ContentFieldSchema thing = new(new List<ContentFieldEntry>
        {
            new(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
            new(ValueField, ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, ValueScale),
            new(StackableField, ContentFieldKind.Bool, null, ContentVisibility.Client, true),
            new(TagsField, ContentFieldKind.TagList, TagTypeKey, ContentVisibility.Client, false),
            new(IconField, ContentFieldKind.OpaqueBytes, null, ContentVisibility.Client, false),
        });
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            ThingTypeId,
            ThingTypeKey,
            new CatalogFixtureCodec(Thing, thing),
            null,
            thing,
            ContentVisibility.Client,
            256);

        ContentFieldSchema tag = new(new List<ContentFieldEntry>
        {
            new(SortField, ContentFieldKind.Int, null, ContentVisibility.Client, false),
        });
        registry.RegisterContentType(
            ContentRegistrationBand.Game,
            TagTypeId,
            TagTypeKey,
            new CatalogFixtureCodec(Tag, tag),
            null,
            tag,
            ContentVisibility.Client,
            256);

        return registry;
    }

    int FieldIndex(string name)
    {
        ContentFieldSchema schema = _registry.ByTypeId[0].Schema;
        for (int i = 0; i < schema.Fields.Count; i++)
        {
            if (string.Equals(schema.Fields[i].Name, name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new InvalidOperationException("The fixture schema declares '" + name + "'.");
    }

    static JsonElement Field(JsonElement type, string name)
        => type.GetProperty("fields").EnumerateArray()
            .Single(field => string.Equals(field.GetProperty("name").GetString(), name, StringComparison.Ordinal));

    static string[] Keys(JsonElement listBody)
        => listBody.GetProperty("rows").EnumerateArray()
            .Select(static r => r.GetProperty("key").GetString()!)
            .ToArray();

    static ContentFieldEdit[] ThingFields(int value = 4200)
        =>
        [
            new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, value)),
            new ContentFieldEdit(StackableField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, 0)),
        ];

    /// <summary>Publishes two tag rows, so a tag list on a thing row names ids that are live.</summary>
    async Task<int[]> SeedTagsAsync()
    {
        await PublishAsync(
            ContentEdit.Add(Tag, new ContentKey("metal"), []),
            ContentEdit.Add(Tag, new ContentKey("two_handed"), []));
        ContentRowPage page = await _store.ListRowsAsync(Tag, 0, null, false, 0, 10);
        return page.Rows.Select(static row => row.Id).ToArray();
    }

    /// <summary>One thing row's allocated id, read back rather than assumed, since publish allocates them.</summary>
    async Task<int> IdOfAsync(string key)
    {
        ContentRowPage page = await _store.ListRowsAsync(Thing, 0, key, true, 0, 10);
        ContentRow row = page.Rows.Single(candidate => candidate.Key.Equals(new ContentKey(key)));
        return row.Id;
    }

    Task<int> PublishThingsAsync(params string[] keys)
        => PublishAsync(keys.Select(key => ContentEdit.Add(Thing, new ContentKey(key), ThingFields())).ToArray());

    async Task<int> PublishAsync(params ContentEdit[] edits)
    {
        int baseVersion = await _store.GetActiveVersionAsync();
        await _store.ApplyEditsAsync(edits, Actor, Operator, "read actions");
        ContentPublishResult result = await _store.PublishAsync(
            new ContentPublishRequest(Actor, Operator, "read actions", baseVersion));
        return result.VersionNumber;
    }

    /// <summary>Dispatches one action through the registry the way the endpoint does.</summary>
    async Task<AdminActionResult> CallAsync(string action, string? body)
    {
        bool found = _admin.TryGetAction(action, out var handler);
        Assert.True(found, "action '" + action + "' is registered.");
        using JsonDocument? document = body is null ? null : JsonDocument.Parse(body);
        JsonElement? payload = document?.RootElement;
        return await handler!(payload, CancellationToken.None);
    }

    /// <summary>The JSON body a 200 would carry, parsed, which is what every fact here asserts against.</summary>
    async Task<JsonElement> OkAsync(string action, string? body)
    {
        AdminActionResult result = await CallAsync(action, body);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        Assert.NotNull(result.Payload);

        // The endpoint serializes with the web defaults, so the property names a console sees are camelCase.
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload, WireOptions));
        return document.RootElement.Clone();
    }

    sealed class NullAdminControllable : IAdminControllable
    {
        public IReadOnlyList<OnlinePlayer> ListOnline() => Array.Empty<OnlinePlayer>();

        public void Teleport(PlayerRef target, Vector3 position)
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
