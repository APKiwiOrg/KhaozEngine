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
/// One registered <see cref="ServerAdmin"/> over one in-memory authoring store and one file-system pack,
/// which is what every catalog action suite in this project dispatches through.
/// <para>
/// <b>Every fact is asserted against the JSON a console would receive</b> rather than against the payload
/// object, because the payload shape IS the contract: one generic editor renders a type it has never heard
/// of out of these bodies, so a renamed property is a broken console rather than a refactor.
/// </para>
/// <para>
/// The registry is per instance and the store is in memory, so nothing here is process global and no
/// collection attribute is needed.
/// </para>
/// </summary>
internal sealed class CatalogActionHarness : IDisposable
{
    /// <summary>The ordinary type, carrying one field of every kind an editor has to render.</summary>
    public const ushort ThingTypeId = 1024;

    /// <summary>The ordinary type's key.</summary>
    public const string ThingTypeKey = "thing";

    /// <summary>The tag type a tag list points at.</summary>
    public const ushort TagTypeId = 1025;

    /// <summary>The tag type's key.</summary>
    public const string TagTypeKey = "tag";

    /// <summary>The derived localized text key field, which no edit ever carries a value for.</summary>
    public const string NameField = "name";

    /// <summary>The required scaled int, whose stored value is the authored one times the scale.</summary>
    public const string ValueField = "value";

    /// <summary>The required bool.</summary>
    public const string StackableField = "stackable";

    /// <summary>The optional bool a fork flags its copy with.</summary>
    public const string LegacyField = "legacy";

    /// <summary>The optional tag list.</summary>
    public const string TagsField = "tags";

    /// <summary>The optional opaque bytes, rendered and read as lower hex.</summary>
    public const string IconField = "icon";

    /// <summary>The tag type's one optional int.</summary>
    public const string SortField = "sort";

    /// <summary>The operator identity the fixture forwards, which is a STABLE identity rather than a name.</summary>
    public const string Operator = "oid:catalog-actions";

    /// <summary>The scale of the scaled int, so a stored 4,200 is an authored 42.</summary>
    public const int ValueScale = 100;

    static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    readonly string _root = Path.Combine(
        Path.GetTempPath(), "kec-actions-" + Guid.NewGuid().ToString("n"));

    /// <summary>Builds the harness, registering the sixteen actions once.</summary>
    /// <param name="options">The server-side options the pin action reads, or null for a host that pins nothing.</param>
    public CatalogActionHarness(CatalogAdminActionOptions? options = null)
    {
        Registry = BuildRegistry();
        Pack = new FileSystemPackStore(_root);
        Store = new InMemoryContentAuthoringStore(Registry, Pack);
        Admin = new ServerAdmin(new NullAdminControllable());
        CatalogAdminActions.Register(Admin, Store, Registry, options);
    }

    /// <summary>The registry the fixture's two types are declared in.</summary>
    public ContentTypeRegistry Registry { get; }

    /// <summary>The pack target, reachable so a test can corrupt one object on purpose.</summary>
    public FileSystemPackStore Pack { get; }

    /// <summary>The authoring store under the actions.</summary>
    public InMemoryContentAuthoringStore Store { get; }

    /// <summary>The admin surface the actions are registered on.</summary>
    public ServerAdmin Admin { get; }

    /// <summary>The ordinary type as a type id.</summary>
    public static ContentTypeId Thing => new(ThingTypeId);

    /// <summary>The tag type as a type id.</summary>
    public static ContentTypeId Tag => new(TagTypeId);

    /// <summary>Dispatches one action through the registry the way the endpoint does.</summary>
    /// <param name="action">The action name.</param>
    /// <param name="body">The JSON body, or null for a call with none.</param>
    public Task<AdminActionResult> CallAsync(string action, string? body) => DispatchAsync(Admin, action, body);

    /// <summary>A fresh admin surface with nothing registered, for a fact that registers its own set.</summary>
    public static ServerAdmin NewSurface() => new(new NullAdminControllable());

    /// <summary>Dispatches one action through any surface's registry the way the endpoint does.</summary>
    /// <param name="admin">The surface to dispatch through.</param>
    /// <param name="action">The action name.</param>
    /// <param name="body">The JSON body, or null for a call with none.</param>
    public static async Task<AdminActionResult> DispatchAsync(ServerAdmin admin, string action, string? body)
    {
        bool found = admin.TryGetAction(action, out var handler);
        Assert.True(found, "action '" + action + "' is registered.");
        using JsonDocument? document = body is null ? null : JsonDocument.Parse(body);
        JsonElement? payload = document?.RootElement;
        return await handler!(payload, CancellationToken.None);
    }

    /// <summary>The JSON body a 200 would carry, parsed, which is what every fact asserts against.</summary>
    /// <param name="action">The action name.</param>
    /// <param name="body">The JSON body, or null.</param>
    public async Task<JsonElement> OkAsync(string action, string? body)
    {
        AdminActionResult result = await CallAsync(action, body);
        Assert.Equal(AdminActionStatus.Ok, result.Status);
        return Wire(result);
    }

    /// <summary>The JSON body a 400 would carry, which is the object error payload rather than a string.</summary>
    /// <param name="action">The action name.</param>
    /// <param name="body">The JSON body, or null.</param>
    public async Task<JsonElement> RefusedAsync(string action, string? body)
    {
        AdminActionResult result = await CallAsync(action, body);
        Assert.Equal(AdminActionStatus.BadRequest, result.Status);
        Assert.NotNull(result.Payload);
        return Wire(result);
    }

    /// <summary>The JSON body a 409 would carry.</summary>
    /// <param name="action">The action name.</param>
    /// <param name="body">The JSON body, or null.</param>
    public async Task<JsonElement> ConflictAsync(string action, string? body)
    {
        AdminActionResult result = await CallAsync(action, body);
        Assert.Equal(AdminActionStatus.Conflict, result.Status);
        Assert.NotNull(result.Payload);
        return Wire(result);
    }

    /// <summary>One result's payload as the endpoint would serialize it, which is camelCase.</summary>
    /// <param name="result">The dispatched result.</param>
    public static JsonElement Wire(AdminActionResult result)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(result.Payload, WireOptions));
        return document.RootElement.Clone();
    }

    /// <summary>Every finding code in a refusal body, in the order the body carries them.</summary>
    /// <param name="body">The refusal body.</param>
    public static string[] Codes(JsonElement body)
        => body.GetProperty("findings").EnumerateArray()
            .Select(static finding => finding.GetProperty("code").GetString()!)
            .ToArray();

    /// <summary>Every message in a refusal body, joined, so a test asserts on what an operator would read.</summary>
    /// <param name="body">The refusal body.</param>
    public static string Messages(JsonElement body)
        => string.Join(" | ", body.GetProperty("findings").EnumerateArray()
            .Select(static finding => finding.GetProperty("message").GetString()!));

    /// <summary>Publishes the rows one or more adds describe, and answers the version it published.</summary>
    /// <param name="edits">The edits.</param>
    public async Task<int> PublishAsync(params ContentEdit[] edits)
    {
        int baseVersion = await Store.GetActiveVersionAsync();
        await Store.ApplyEditsAsync(edits, CatalogAdminActions.Actor, Operator, "fixture");
        ContentPublishResult result = await Store.PublishAsync(
            new ContentPublishRequest(CatalogAdminActions.Actor, Operator, "fixture", baseVersion));
        return result.VersionNumber;
    }

    /// <summary>Publishes one ordinary row per key, which is what most facts here stand on.</summary>
    /// <param name="keys">The row keys.</param>
    public Task<int> PublishThingsAsync(params string[] keys)
        => PublishAsync(keys.Select(key => ContentEdit.Add(Thing, new ContentKey(key), Fields())).ToArray());

    /// <summary>The field set an ordinary fixture row carries.</summary>
    /// <param name="value">The stored scaled value.</param>
    /// <param name="stackable">The required bool.</param>
    public static ContentFieldEdit[] Fields(int value = 4200, bool stackable = false)
        =>
        [
            new ContentFieldEdit(ValueField, ContentFieldValue.OfNumber(ContentFieldKind.ScaledInt, value)),
            new ContentFieldEdit(StackableField, ContentFieldValue.OfNumber(ContentFieldKind.Bool, stackable ? 1 : 0)),
        ];

    /// <summary>One row's allocated id, read back rather than assumed, since publish allocates them.</summary>
    /// <param name="key">The row key.</param>
    public async Task<int> IdOfAsync(string key)
    {
        ContentRowPage page = await Store.ListRowsAsync(Thing, 0, key, true, 0, 50);
        return page.Rows.Single(row => row.Key.Equals(new ContentKey(key))).Id;
    }

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

    static ContentTypeRegistry BuildRegistry()
    {
        var registry = new ContentTypeRegistry();
        ContentFieldSchema thing = new(new List<ContentFieldEntry>
        {
            new(NameField, ContentFieldKind.LocalizedTextKey, null, ContentVisibility.Client, true),
            new(ValueField, ContentFieldKind.ScaledInt, null, ContentVisibility.Client, true, ValueScale),
            new(StackableField, ContentFieldKind.Bool, null, ContentVisibility.Client, true),
            new(LegacyField, ContentFieldKind.Bool, null, ContentVisibility.Client, false),
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
