using System;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// What the catalog actions need to know about the SERVER they are registered on, none of which the
/// authoring store can answer.
/// <para>
/// Both properties exist for <c>catalog-pin</c>. Which version a server boots has one order of precedence,
/// config first, and an action that could not see the config pin would answer a bare 200 to a call with no
/// effect on the next restart.
/// </para>
/// </summary>
public sealed record CatalogAdminActionOptions
{
    /// <summary>
    /// The version this server's OWN CONFIG pins, or null when it pins none. Config wins over the
    /// operator's hold, always, and a pin written against a server carrying one says so in its answer.
    /// </summary>
    public int? ConfiguredVersion { get; init; }

    /// <summary>
    /// The running server's build ordinal, or 0 when the host does not declare one. A pin naming a version
    /// whose minimum server build exceeds it is ACCEPTED with a warning, because the operator may be pinning
    /// ahead of an upgrade on purpose and the boot check is the real gate.
    /// </summary>
    public int ServerBuild { get; init; }
}

/// <summary>
/// The content authoring API as REGISTERED ACTIONS on the existing admin surface (spec 10.1). There is no new
/// transport, no new listener, no new auth and no new package: a game calls
/// <see cref="Register(ServerAdmin, IContentAuthoringStore, ContentTypeRegistry, CatalogAdminActionOptions)"/>
/// once beside its own registrations and its console reaches the whole catalog through
/// <c>GET</c> / <c>POST /admin/actions/{name}</c>.
/// <para>
/// <b>There is ONE action count in this spec and it is SIXTEEN</b>, the number of names a
/// <see cref="ServerAdmin"/> holds after <c>Register</c> returns. Earlier drafts counted eleven in one place
/// and fourteen in another by collapsing pairs a caller still has to know the names of, so the count is
/// pinned by a test rather than by prose.
/// </para>
/// <para>
/// <b>It lives in this package rather than in the authoring one, deliberately.</b> The helper needs
/// <see cref="ServerAdmin"/> and the authoring store together. Putting it in
/// <c>KhaozEngine.Catalog.Authoring</c> would give every opt-in SQL provider a transitive edge to the whole
/// netcode stack, and a package of its own was refused by the spec's own package count. This package already
/// references NetWorld, already owns the dispatch, and is deliberately outside the <c>KhaozEngine.Server</c>
/// umbrella, so nothing inherits the cost. The price is that a game registering these on a
/// <see cref="ServerAdmin"/> with no HTTP endpoint cannot reach the helper, which is theoretical while there
/// is one transport and the status codes are HTTP.
/// </para>
/// <para>
/// <b>Every handler runs on the HTTP request thread and touches only the authoring store</b>, never the
/// simulation, which is what keeps the threading contract of <see cref="ServerAdmin.RegisterAction(string, System.Func{System.Text.Json.JsonElement?, System.Threading.CancellationToken, System.Threading.Tasks.Task{AdminActionResult}})"/>
/// satisfied by construction.
/// </para>
/// <para>
/// <b>None of them returns 202.</b> A content edit completes INSIDE the request against the database, so an
/// operator gets the real answer rather than an optimistic one, which is the direct answer to a console
/// reporting success on a row the server then rejects at boot.
/// </para>
/// </summary>
public static class CatalogAdminActions
{
    /// <summary>The full registered schema, which is what a generic editor renders an unknown type from.</summary>
    public const string SchemaAction = "catalog-schema";

    /// <summary>One page of a type's rows at a version.</summary>
    public const string ListAction = "catalog-list";

    /// <summary>One row plus its full version history.</summary>
    public const string GetAction = "catalog-get";

    /// <summary>A batch of edits against the open draft, applied whole or not at all.</summary>
    public const string EditAction = "catalog-edit";

    /// <summary>The open draft with its edits expanded.</summary>
    public const string DraftAction = "catalog-draft";

    /// <summary>The open draft deleted, leaving one audit row carrying the edit count.</summary>
    public const string DiscardAction = "catalog-discard";

    /// <summary>The full sweep over the draft-applied candidate, allocating nothing and writing nothing.</summary>
    public const string ValidateAction = "catalog-validate";

    /// <summary>The field-level diff, plus the chunk summary that says what publishing would cost.</summary>
    public const string DiffAction = "catalog-diff";

    /// <summary>The draft published as a new immutable version, under required optimistic concurrency.</summary>
    public const string PublishAction = "catalog-publish";

    /// <summary>The active version, the operator's hold and every version record.</summary>
    public const string VersionsAction = "catalog-versions";

    /// <summary>The operator's version hold, written or cleared.</summary>
    public const string PinAction = "catalog-pin";

    /// <summary>A draft that would restore an earlier version's field values.</summary>
    public const string RollbackAction = "catalog-rollback";

    /// <summary>A whole bundle imported into an EMPTY database, and refused into any other.</summary>
    public const string ImportAction = "catalog-import";

    /// <summary>One version as a bundle, ids included, which is what makes the export lossless.</summary>
    public const string ExportAction = "catalog-export";

    /// <summary>Publish step 11 alone, for an operator cleaning up after a crashed publish.</summary>
    public const string SweepAction = "catalog-sweep";

    /// <summary>Every object a version's manifests name, fetched and rehashed. Read only, and never a repair.</summary>
    public const string VerifyAction = "catalog-verify";

    /// <summary>
    /// What the engine AUTHENTICATED, recorded on every audit row this surface writes. The bearer token is
    /// ONE token and is not an identity, so the operator identity a console forwards is recorded BESIDE it
    /// rather than instead of it.
    /// </summary>
    public const string Actor = "admin-endpoint";

    /// <summary>
    /// The SERVER-side page cap. A console that asks for more is given this many and told so through the
    /// response's own <c>take</c>, which is what makes it page rather than silently show one screenful of a
    /// larger set.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>The page size a request that names no <c>take</c> is answered with.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    /// The sixteen names, in registration order: the reads, the draft writes, the publish trio, the version
    /// line, the bundle pair and the operational pair. It is a count the spec states once and means, so it
    /// is stated once here too and asserted by a test.
    /// </summary>
    public static readonly string[] ActionNames =
    [
        SchemaAction,
        ListAction,
        GetAction,
        EditAction,
        DraftAction,
        DiscardAction,
        ValidateAction,
        DiffAction,
        PublishAction,
        VersionsAction,
        PinAction,
        RollbackAction,
        ImportAction,
        ExportAction,
        SweepAction,
        VerifyAction,
    ];

    /// <summary>
    /// Registers all SIXTEEN catalog actions on <paramref name="admin"/>. Call it ONCE, at startup, before
    /// the endpoint starts.
    /// </summary>
    /// <param name="admin">The admin surface the actions are registered on.</param>
    /// <param name="store">The authoring store every action reads and writes through.</param>
    /// <param name="registry">The content type registry. Per instance, never ambient.</param>
    /// <param name="options">What the actions need to know about the server, or null for a host that pins no version in config.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">An action name is already registered, which a second call is.</exception>
    public static void Register(
        ServerAdmin admin,
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        CatalogAdminActionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);

        CatalogAdminActionOptions resolved = options ?? new CatalogAdminActionOptions();
        new CatalogReadActions(store, registry).Register(admin);
        new CatalogEditActions(store, registry).Register(admin);
        new CatalogPublishActions(store, registry).Register(admin);
        new CatalogVersionActions(store, registry, resolved).Register(admin);
        new CatalogBundleActions(store, registry).Register(admin);
        new CatalogOperationalActions(store, registry).Register(admin);
    }
}
