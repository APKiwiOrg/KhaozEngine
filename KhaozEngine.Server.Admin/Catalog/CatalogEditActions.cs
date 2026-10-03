using System;
using System.Collections.Generic;
using System.Globalization;
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
/// <c>catalog-edit</c> takes row <c>edits</c> and text <c>textEdits</c> together, so an add and its name are
/// one request. Text input against a store without the text companion is a typed refusal, never a row-only
/// apply that drops it.
/// </para>
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
        admin.RegisterAction(CatalogAdminActions.EditAction, EditAsync, mutating: true);
        admin.RegisterAction(CatalogAdminActions.DiscardAction, DiscardAsync, mutating: true);
    }

    /// <summary>
    /// Applies a batch of row and text edits to the open draft, opening one against the active version when
    /// none is open. Every finding across both arrays is gathered first, and the whole batch then lands in ONE
    /// atomic apply: the complete <see cref="IContentTextAuthoringStore.ApplyChangesAsync"/> on a store with
    /// the text companion, the row-only apply on a store without it, which refuses text input by type.
    /// </summary>
    async Task<AdminActionResult> EditAsync(JsonElement? payload, CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryBody(
            payload, CatalogAdminActions.EditAction, "'edits' or 'textEdits'", out JsonElement body, out AdminActionResult refused))
        {
            return refused;
        }

        if (!CatalogRequest.TryOperator(body, out string operatorId, out string? refusal)
            || !CatalogRequest.TryNote(body, out string note, out refusal))
        {
            return CatalogRefusal.Malformed(refusal!);
        }

        if (!TryTextArray(body, out JsonElement? text, out string? malformed))
        {
            return CatalogRefusal.Malformed(malformed!);
        }

        IContentTextAuthoringStore? textStore = store as IContentTextAuthoringStore;
        if (text is not null && textStore is null)
        {
            return CatalogRefusal.TextUnsupported(CatalogAdminActions.EditAction + " with 'textEdits'");
        }

        CatalogEditParse parse = await CatalogEditParser
            .ParseAsync(store, registry, body, text is not null, cancellationToken).ConfigureAwait(false);
        if (parse.MalformedReason is string rowsMalformed)
        {
            return CatalogRefusal.Malformed(rowsMalformed);
        }

        var findings = new List<CatalogFindingPayload>(parse.Findings);
        IReadOnlyList<ContentTextEdit> texts = text is JsonElement array
            ? await CatalogTextEditParser
                .ParseAsync(textStore!, registry, array, Rows(body), findings, cancellationToken)
                .ConfigureAwait(false)
            : [];
        if (findings.Count > 0)
        {
            return CatalogRefusal.Findings(CatalogEditParser.Refused, CatalogEditParser.Reason, findings);
        }

        try
        {
            ContentDraft draft = textStore is not null
                ? await textStore
                    .ApplyChangesAsync(
                        new ContentAuthoringChanges(parse.Edits, texts),
                        CatalogAdminActions.Actor,
                        operatorId,
                        note,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await store
                    .ApplyEditsAsync(parse.Edits, CatalogAdminActions.Actor, operatorId, note, cancellationToken)
                    .ConfigureAwait(false);

            return AdminActionResult.Ok(new CatalogEditPayload(CatalogDraftHeader.Of(draft), parse.Edits.Count)
            {
                TextApplied = texts.Count,
            });
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
    /// <para>
    /// On a store with the text companion the delete is the ATOMIC expected-draft discard: the draft this
    /// request read, rows, text intents and introductions, is deleted only when it is still exactly what the
    /// store holds. A rival change in between answers a 409 that deletes nothing, never a 200 over text the
    /// request did not see.
    /// </para>
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
        var answer = new CatalogDiscardPayload(draft is not null, draft?.EditCount ?? 0)
        {
            TextEditCount = draft?.TextEditCount ?? 0,
            LanguageIntroductionCount = draft?.LanguageIntroductionCount ?? 0,
        };

        try
        {
            if (store is IContentTextAuthoringStore text && draft is { IsFrozen: false, TextState: not null })
            {
                bool discarded = await text
                    .TryDiscardChangesAsync(draft, CatalogAdminActions.Actor, operatorId, cancellationToken)
                    .ConfigureAwait(false);
                if (!discarded)
                {
                    return AdminActionResult.Conflict(new CatalogConflictPayload(
                        "The open draft changed after this discard read it, so nothing was discarded.",
                        ContentAuthoringException.TextStateMismatchReason,
                        CatalogRefusal.TextStateMismatchRemedy));
                }

                return AdminActionResult.Ok(answer);
            }

            // A frozen draft, a draft a row-only route reported and a store without the companion take the
            // row-only discard, which refuses a publish in flight and any text the store actually holds.
            await store
                .DiscardDraftAsync(CatalogAdminActions.Actor, operatorId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ContentAuthoringException failure)
        {
            return CatalogRefusal.From(failure, registry);
        }

        return AdminActionResult.Ok(answer);
    }

    /// <summary>
    /// The <c>textEdits</c> array, null when the request carries none, or a refusal for one that is not a
    /// non-empty array within the per-request cap. An empty array is a console defect rather than an intent.
    /// </summary>
    static bool TryTextArray(JsonElement body, out JsonElement? array, out string? refusal)
    {
        array = null;
        refusal = null;
        if (!body.TryGetProperty(CatalogTextEditParser.Property, out JsonElement text)
            || text.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (text.ValueKind != JsonValueKind.Array)
        {
            refusal = "'textEdits' is an ARRAY of text edits, each naming typeKey, key, field, language and op 'set' with a value or 'remove'.";
            return false;
        }

        if (text.GetArrayLength() == 0)
        {
            refusal = "'textEdits' is empty, so it would apply nothing. Omit it for a row-only save.";
            return false;
        }

        if (text.GetArrayLength() > CatalogRequest.MaxEditsPerRequest)
        {
            refusal = FormattableString.Invariant(
                $"'textEdits' carries {text.GetArrayLength().ToString(CultureInfo.InvariantCulture)} entries and one request takes at most {CatalogRequest.MaxEditsPerRequest.ToString(CultureInfo.InvariantCulture)}. Every entry costs a store round trip, so the array is capped.");
            return false;
        }

        array = text;
        return true;
    }

    static JsonElement? Rows(JsonElement body)
        => body.TryGetProperty("edits", out JsonElement rows) ? rows : null;
}
