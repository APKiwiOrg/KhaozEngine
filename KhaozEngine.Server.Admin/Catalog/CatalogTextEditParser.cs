using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// The <c>textEdits</c> array of one <c>catalog-edit</c> payload turned into the companion's text intents,
/// CHECKED AT THE BOUNDARY beside the row edits, so one refusal carries every finding across both.
/// <para>
/// An entry names its string by type key, content key, localized text marker field and language, never by a
/// raw localization key, because the key is derived. A <c>set</c> carries a complete value, empty allowed,
/// and a <c>remove</c> carries none. Every entry is checked for CLIENT eligibility at both levels, the
/// language grammar, the strict UTF-8 bounds, a row live at the active version or added in this request or
/// the open draft, a Remove in an undeclared language and two spellings of one canonical target. The store
/// checks all of it again inside its own atomic apply, which stays the authority.
/// </para>
/// </summary>
internal static class CatalogTextEditParser
{
    /// <summary>The request property the text intents are read from.</summary>
    public const string Property = "textEdits";

    /// <summary>A language identity outside the authored grammar.</summary>
    public const string LanguageCode = "text-language-invalid";

    /// <summary>Two entries naming one canonical target in one request.</summary>
    public const string DuplicateCode = "text-target-duplicate";

    /// <summary>
    /// Reads every entry of <paramref name="array"/>, appending one finding per refused entry to
    /// <paramref name="findings"/>, and answers the intents that parsed, in request order.
    /// </summary>
    /// <param name="store">The text store targets are resolved against.</param>
    /// <param name="registry">The registry the type keys and the schemas come from.</param>
    /// <param name="array">The <c>textEdits</c> array.</param>
    /// <param name="rows">The request's row edit array, whose adds and forks text may name, or null.</param>
    /// <param name="findings">Where every finding is appended.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public static async Task<IReadOnlyList<ContentTextEdit>> ParseAsync(
        IContentTextAuthoringStore store,
        ContentTypeRegistry registry,
        JsonElement array,
        JsonElement? rows,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(findings);

        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        var scope = new Scope(
            PendingKeys(registry, draft, rows),
            await DeclaredAsync(store, draft, cancellationToken).ConfigureAwait(false),
            new Dictionary<ContentTextTarget, int>());

        var edits = new List<ContentTextEdit>(array.GetArrayLength());
        int ordinal = 0;
        foreach (JsonElement entry in array.EnumerateArray())
        {
            ContentTextEdit? edit = await OneAsync(store, registry, entry, ordinal, scope, findings, cancellationToken)
                .ConfigureAwait(false);
            if (edit is not null)
            {
                edits.Add(edit);
            }

            ordinal++;
        }

        return edits;
    }

    /// <summary>One entry, or null with its ONE finding recorded. It never throws for a payload defect.</summary>
    static async Task<ContentTextEdit?> OneAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        JsonElement entry,
        int ordinal,
        Scope scope,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, string.Empty, 0, ordinal, "is not a JSON object.");
        }

        if (!CatalogRequest.TryOptionalString(entry, "op", out string? op, out string? refusal)
            || !CatalogRequest.TryOptionalString(entry, "typeKey", out string? typeKey, out refusal)
            || !CatalogRequest.TryOptionalString(entry, "key", out string? key, out refusal)
            || !CatalogRequest.TryOptionalString(entry, "field", out string? field, out refusal)
            || !CatalogRequest.TryOptionalString(entry, "language", out string? language, out refusal)
            || !CatalogRequest.TryOptionalString(entry, "value", out string? value, out refusal))
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, string.Empty, 0, ordinal, refusal!);
        }

        if (typeKey is null || !CatalogRequest.TryType(registry, typeKey, out ContentTypeRegistration? type, out refusal))
        {
            return Refuse(
                findings, CatalogEditParser.UnknownFieldCode, typeKey ?? string.Empty, 0, ordinal, refusal ?? "names no 'typeKey'.");
        }

        if (op is not ("set" or "remove"))
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"carries op '{op}', and a text edit is 'set' with a complete value or 'remove' with none."));
        }

        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(field) || language is null)
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, type.TypeKey, 0, ordinal,
                "names its string by 'key', 'field' and 'language', and this entry is missing one.");
        }

        if (!type.Schema.TryGet(field, out ContentFieldEntry? marker))
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"names no field '{field}' on type '{type.TypeKey}'."));
        }

        if (type.DefaultVisibility != ContentVisibility.Client
            || !marker.IsDerivedMarker
            || marker.Visibility != ContentVisibility.Client)
        {
            return Refuse(findings, ContentAuthoringException.TextTargetIneligibleReason, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"names type '{type.TypeKey}' field '{field}', and text needs a CLIENT-visible type and a CLIENT-visible localized text marker. A shared manifest carries every language chunk."));
        }

        if (!ContentTextLanguageTag.TryNormalize(language, out string? canonical))
        {
            return Refuse(findings, LanguageCode, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"names language '{language}', which is not ASCII letters, digits and hyphens of 1 to {ContentTextLanguageTag.MaxBytes} bytes with no empty segment and a first segment starting with a letter. Underscores and other aliases are refused rather than converted."));
        }

        bool set = string.Equals(op, "set", StringComparison.Ordinal);
        if (set == value is null)
        {
            return Refuse(findings, CatalogEditParser.UnknownFieldCode, type.TypeKey, 0, ordinal, set
                ? "is a set and carries no string 'value'. An empty string is a present empty value, and a remove is how a string is made absent."
                : "is a remove and carries a 'value'. A remove makes the string absent, so a reader falls back.");
        }

        var rowKey = new ContentKey(key);
        int keyBytes = Encoding.UTF8.GetByteCount(type.TypeKey) + 2 + rowKey.Utf8.Length + Encoding.UTF8.GetByteCount(field);
        if (keyBytes > ContentTextKey.MaxKeyLength)
        {
            return Refuse(findings, ContentAuthoringException.TextBoundsReason, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"derives a key of {keyBytes.ToString(CultureInfo.InvariantCulture)} UTF-8 bytes, over the {ContentTextKey.MaxKeyLength.ToString(CultureInfo.InvariantCulture)} byte bound."));
        }

        var target = new ContentTextTarget(type.Type, rowKey, field, canonical);
        ContentTextEdit edit;
        try
        {
            edit = set ? ContentTextEdit.Set(target, value!) : ContentTextEdit.Remove(target);
        }
        catch (ArgumentException failure)
        {
            return Refuse(findings, ContentAuthoringException.TextBoundsReason, type.TypeKey, 0, ordinal, failure.Message);
        }

        ContentRow? row = await CatalogRequest.FindByKeyAsync(store, type, key, cancellationToken).ConfigureAwait(false);
        if (row is null && !scope.Pending.Contains((type.Type.Value, rowKey)))
        {
            return Refuse(findings, CatalogEditParser.ReferenceCode, type.TypeKey, 0, ordinal, FormattableString.Invariant(
                $"names key '{key}', which is no row of type '{type.TypeKey}' at the active version and is not added by this request or the open draft."));
        }

        int id = row?.Id ?? 0;
        if (scope.Targets.TryGetValue(target, out int first))
        {
            return Refuse(findings, DuplicateCode, type.TypeKey, id, ordinal, FormattableString.Invariant(
                $"names the string Text edit {first.ToString(CultureInfo.InvariantCulture)} already names (type '{type.TypeKey}', key '{key}', field '{field}', language '{canonical}'). One request holds one intent per string, so it cannot depend on its own order."));
        }

        scope.Targets[target] = ordinal;
        if (set)
        {
            scope.Declared?.Add(canonical);
        }
        else if (scope.Declared is not null && !scope.Declared.Contains(canonical))
        {
            return Refuse(findings, ContentAuthoringException.TextLanguageUndeclaredReason, type.TypeKey, id, ordinal, FormattableString.Invariant(
                $"removes in language '{canonical}', which neither the active version nor the open draft declares, so there is nothing to remove in it. A set is what declares a language."));
        }

        return edit;
    }

    /// <summary>
    /// The rows text may name before they have ids: every add and fork copy of the open draft and of this
    /// request's row edits, read straight off the request so a refused add does not also refuse its text.
    /// </summary>
    static HashSet<(ushort, ContentKey)> PendingKeys(ContentTypeRegistry registry, ContentDraft? draft, JsonElement? rows)
    {
        var keys = new HashSet<(ushort, ContentKey)>();
        if (draft is not null)
        {
            foreach (ContentEdit edit in draft.Changes.Edits)
            {
                if (edit.Operation == ContentEditOperation.Add)
                {
                    keys.Add((edit.Type.Value, edit.Key));
                }
                else if (edit.Operation == ContentEditOperation.Fork)
                {
                    keys.Add((edit.Type.Value, edit.ForkKey));
                }
            }
        }

        if (rows is not JsonElement array || array.ValueKind != JsonValueKind.Array)
        {
            return keys;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object
                && CatalogRequest.TryOptionalString(entry, "op", out string? op, out _)
                && CatalogRequest.TryOptionalString(entry, "typeKey", out string? typeKey, out _)
                && typeKey is not null
                && CatalogRequest.TryType(registry, typeKey, out ContentTypeRegistration? type, out _)
                && CatalogRequest.TryOptionalString(entry, op == "fork" ? "forkKey" : "key", out string? key, out _)
                && op is ("add" or "fork")
                && !string.IsNullOrEmpty(key))
            {
                keys.Add((type.Type.Value, new ContentKey(key)));
            }
        }

        return keys;
    }

    /// <summary>
    /// The canonical languages the active version declares plus the draft's introductions, or null when the
    /// active version's text cannot be proven, in which case the store's own apply decides.
    /// </summary>
    static async Task<HashSet<string>?> DeclaredAsync(
        IContentTextAuthoringStore store,
        ContentDraft? draft,
        CancellationToken cancellationToken)
    {
        int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
        ContentVersionTextSnapshot? snapshot = await CatalogTextReads
            .SnapshotAsync(store, active, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return null;
        }

        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContentTextLanguage language in snapshot.Languages)
        {
            declared.Add(language.Language);
        }

        foreach (ContentTextLanguageDeclaration introduction in draft?.TextState?.Introductions ?? [])
        {
            declared.Add(introduction.Language);
        }

        return declared;
    }

    static ContentTextEdit? Refuse(
        List<CatalogFindingPayload> findings, string code, string typeKey, int id, int ordinal, string detail)
    {
        findings.Add(new CatalogFindingPayload(
            code,
            typeKey,
            id,
            FormattableString.Invariant($"Text edit {ordinal.ToString(CultureInfo.InvariantCulture)} {detail}")));
        return null;
    }

    /// <summary>What every entry of one request is checked against.</summary>
    /// <param name="Pending">The rows the draft or this request adds or forks into, by key.</param>
    /// <param name="Declared">The declared languages, growing with each set, or null when unknown.</param>
    /// <param name="Targets">Each canonical target already named, with the ordinal that named it first.</param>
    sealed record Scope(
        HashSet<(ushort, ContentKey)> Pending,
        HashSet<string>? Declared,
        Dictionary<ContentTextTarget, int> Targets);
}
