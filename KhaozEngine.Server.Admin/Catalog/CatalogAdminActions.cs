using System;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The content authoring API as REGISTERED ACTIONS on the existing admin surface (spec 10.1). There is no new
/// transport, no new listener, no new auth and no new package: a game calls
/// <see cref="Register(ServerAdmin, IContentAuthoringStore, ContentTypeRegistry)"/> once beside its own
/// registrations and its console reaches the whole catalog through
/// <c>GET</c> / <c>POST /admin/actions/{name}</c>.
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
/// </summary>
public static class CatalogAdminActions
{
    /// <summary>The full registered schema, which is what a generic editor renders an unknown type from.</summary>
    public const string SchemaAction = "catalog-schema";

    /// <summary>One page of a type's rows at a version.</summary>
    public const string ListAction = "catalog-list";

    /// <summary>One row plus its full version history.</summary>
    public const string GetAction = "catalog-get";

    /// <summary>The open draft with its edits expanded.</summary>
    public const string DraftAction = "catalog-draft";

    /// <summary>The active version, the operator's hold and every version record.</summary>
    public const string VersionsAction = "catalog-versions";

    /// <summary>
    /// The SERVER-side page cap. A console that asks for more is given this many and told so through the
    /// response's own <c>take</c>, which is what makes it page rather than silently show one screenful of a
    /// larger set.
    /// </summary>
    public const int MaxPageSize = 500;

    /// <summary>The page size a request that names no <c>take</c> is answered with.</summary>
    public const int DefaultPageSize = 100;

    /// <summary>
    /// Registers the catalog actions on <paramref name="admin"/>. Call it ONCE, at startup, before the
    /// endpoint starts.
    /// </summary>
    /// <param name="admin">The admin surface the actions are registered on.</param>
    /// <param name="store">The authoring store every action reads and writes through.</param>
    /// <param name="registry">The content type registry. Per instance, never ambient.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">An action name is already registered, which a second call is.</exception>
    public static void Register(ServerAdmin admin, IContentAuthoringStore store, ContentTypeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);

        new CatalogReadActions(store, registry).Register(admin);
    }
}
