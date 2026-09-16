using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The bundle pair of spec 10.9: <c>catalog-import</c> and <c>catalog-export</c>.
/// <para>
/// <b>Import works into an EMPTY database ONLY and is refused otherwise</b>, empty meaning the version
/// table holds no rows, with a 409 carrying the active version and no partial write. That single rule is
/// the answer to a whole class of seeding defect: an insert-if-absent seed that runs repeatedly against
/// live data ends up carrying guarded corrections that knowingly revert an operator's value. A deployed
/// database's values change through <c>catalog-edit</c> and <c>catalog-publish</c> and through nothing
/// else, ever.
/// </para>
/// <para>
/// <b>Export at N then import into an empty store reproduces the same rows, keys and IDS.</b> A bundle
/// row's id is OPTIONAL and a bundle may MIX the two, because the id is per row and there is one import
/// path.
/// </para>
/// </summary>
/// <param name="store">The authoring store the bundle is imported into and exported from.</param>
/// <param name="registry">The registry both sides' types are declared in.</param>
internal sealed class CatalogBundleActions(IContentAuthoringStore store, ContentTypeRegistry registry)
{
    /// <summary>The property an import reads the bundle document out of.</summary>
    public const string BundleProperty = "bundle";

    /// <summary>Registers the two names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.ImportAction, ImportAsync);
        admin.RegisterAction(CatalogAdminActions.ExportAction, ExportAsync);
    }

    /// <summary>Imports a whole bundle into an empty database, publishing it as version 1.</summary>
    async Task<AdminActionResult> ImportAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload, CatalogAdminActions.ImportAction, "'bundle'", out JsonElement body, out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNote(body, out string note, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (!body.TryGetProperty(BundleProperty, out JsonElement document)
            || document.ValueKind != JsonValueKind.Object)
        {
            return CatalogRefusal.Malformed(
                "'bundle' is the whole catalog as ONE JSON document, and this request carries none.");
        }

        ContentBundle bundle;
        try
        {
            bundle = ContentBundleJson.Read(document.GetRawText());
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }

        try
        {
            ContentPublishResult result = await store
                .ImportBundleAsync(bundle, CatalogAdminActions.Actor, operatorId, note, cancellationToken)
                .ConfigureAwait(false);

            return AdminActionResult.Ok(new CatalogImportPayload(
                result.VersionNumber,
                bundle.Rows.Count,
                result.ServerManifestHash,
                result.ClientManifestHash,
                result.ChunksWritten,
                result.BytesWritten,
                result.ElapsedMilliseconds));
        }
        catch (ContentAuthoringException failure)
            when (failure.Reason == ContentAuthoringException.CatalogNotEmptyReason)
        {
            // The 409 carries the version the store stands at, which is the fact an operator needs: an
            // import is for seeding a NEW database, and this one already has a history.
            int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
            return AdminActionResult.Conflict(new CatalogNotEmptyPayload(
                "catalog is not empty",
                ContentAuthoringException.CatalogNotEmptyReason,
                active));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }

    /// <summary>Exports one version as a bundle, ids included, which is what makes it lossless.</summary>
    async Task<AdminActionResult> ExportAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        int version = 0;
        if (payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
        {
            if (!CatalogRequest.TryVersion(body, "version", out version, out string? refusal))
            {
                return CatalogRefusal.Malformed(refusal!);
            }
        }

        if (version == 0)
        {
            version = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        }

        if (version == 0)
        {
            return CatalogRefusal.BadRequest(
                "This store has published no version, so there is nothing to export.",
                ContentAuthoringException.UnknownVersionReason);
        }

        try
        {
            ContentBundle bundle = await store
                .ExportBundleAsync(version, cancellationToken).ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(ContentBundleJson.Write(bundle));
            return AdminActionResult.Ok(new CatalogExportPayload(
                version, bundle.Rows.Count, document.RootElement.Clone()));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }
}
