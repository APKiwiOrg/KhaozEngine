using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The draft-writing actions of spec 10.5: <c>catalog-edit</c> and <c>catalog-discard</c>.
/// <para>
/// <b>Every edit in one request applies in ONE database transaction or none of them does</b>, so a batch
/// save from a grid is atomic, and the edits are checked against the schema AT THE BOUNDARY rather than at
/// publish. The response carries EVERY finding rather than the first, which is the direct answer to a
/// console reporting success on a row the server then rejects at boot.
/// </para>
/// <para>
/// <b>Neither returns 202.</b> A content edit completes INSIDE the request against the database, so the
/// operator gets the real answer rather than an optimistic one.
/// </para>
/// </summary>
/// <param name="store">The authoring store every edit is applied through.</param>
/// <param name="registry">The registry the type keys and the schemas come from. Per instance, never ambient.</param>
internal sealed class CatalogEditActions(IContentAuthoringStore store, ContentTypeRegistry registry)
{
    /// <summary>Registers the two names on the admin surface.</summary>
    /// <param name="admin">The surface to register on.</param>
    public void Register(ServerAdmin admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        admin.RegisterAction(CatalogAdminActions.EditAction, EditAsync);
        admin.RegisterAction(CatalogAdminActions.DiscardAction, DiscardAsync);
    }

    /// <summary>
    /// Applies a batch of edits to the open draft, opening one against the active version when none is open.
    /// </summary>
    async Task<AdminActionResult> EditAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload, CatalogAdminActions.EditAction, "'edits'", out JsonElement body, out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNote(body, out string note, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        CatalogEditParse parse = await CatalogEditParser
            .ParseAsync(store, registry, body, cancellationToken).ConfigureAwait(false);
        if (parse.MalformedReason is string malformed)
        {
            return CatalogRefusal.Malformed(malformed);
        }

        if (!parse.IsValid)
        {
            return CatalogRefusal.Findings(
                CatalogEditParser.Refused, CatalogEditParser.Reason, parse.Findings);
        }

        try
        {
            ContentDraft draft = await store
                .ApplyEditsAsync(parse.Edits, CatalogAdminActions.Actor, operatorId, note, cancellationToken)
                .ConfigureAwait(false);

            return AdminActionResult.Ok(new CatalogEditPayload(CatalogDraftHeader.Of(draft), parse.Edits.Count));
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }
    }

    /// <summary>
    /// Deletes the open draft and its edits, writing one audit row carrying the edit count, so a discarded
    /// draft leaves a trace. It is a 409 while a publish is in flight, because the change set the pipeline
    /// read at step 1 is the one the commit deletes.
    /// </summary>
    async Task<AdminActionResult> DiscardAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        string operatorId = string.Empty;
        if (payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
        {
            if (!CatalogRequest.TryOperator(body, out operatorId, out string? refusal))
            {
                return CatalogRefusal.Malformed(refusal!);
            }
        }

        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        int editCount = draft?.EditCount ?? 0;

        try
        {
            await store
                .DiscardDraftAsync(CatalogAdminActions.Actor, operatorId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }

        return AdminActionResult.Ok(new CatalogDiscardPayload(draft is not null, editCount));
    }
}
