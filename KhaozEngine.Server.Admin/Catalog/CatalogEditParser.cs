using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// One <c>catalog-edit</c> payload turned into the seam's own edits, CHECKED AT THE BOUNDARY (spec 10.5).
/// <para>
/// <b>It accumulates every finding rather than stopping at the first</b>, matching the validator's own
/// run-to-the-end rule, so a grid that saved three bad cells is told about three of them in one round trip
/// instead of three.
/// </para>
/// <para>
/// <b>No edit ever carries a localized text key.</b> The key is DERIVED from the type key, the row key and
/// the field name, so an edit that named one could only repeat the derivation or contradict it. A payload
/// carrying a marker field is refused with <c>KEC0004</c> and the message names the derived key, so an
/// operator sees what they were trying to set.
/// </para>
/// </summary>
internal static class CatalogEditParser
{
    /// <summary>A row carries a field the type's schema does not declare, or one it may not author.</summary>
    public const string UnknownFieldCode = "KEC0004";

    /// <summary>
    /// A key an edit INTRODUCES is malformed, which is the sweep's own code reported at the boundary. An add's
    /// key and a fork's copy key are the only two keys an edit invents, and neither exists as a row for the
    /// sweep to walk, so the boundary is the only place either can be caught before it enters the draft.
    /// </summary>
    public const string KeyShapeCode = "KEC0001";

    /// <summary>A replacement-policy retire names a key that resolves to no row.</summary>
    public const string ReplacementCode = "KEC0017";

    /// <summary>A fork's preconditions fail, which is one code covering all four of them.</summary>
    public const string ForkCode = "KEC0041";

    /// <summary>A key reference or a tag list names a row that is not live.</summary>
    public const string ReferenceCode = "KEC0006";

    /// <summary>The refusal every boundary check reports under.</summary>
    public const string Reason = "content-edit-refused";

    /// <summary>The sentence that heads a refused edit payload.</summary>
    public const string Refused = "content edit refused";

    /// <summary>
    /// Reads the <c>edits</c> array into the seam's edits, or into the findings that refuse it.
    /// </summary>
    /// <param name="store">The store keys and ids are resolved against.</param>
    /// <param name="registry">The registry the type keys and the schemas come from.</param>
    /// <param name="body">The request body.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The parsed edits, or the findings that refuse them.</returns>
    public static async Task<CatalogEditParse> ParseAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        JsonElement body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);

        if (!body.TryGetProperty("edits", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
        {
            return CatalogEditParse.Malformed(
                "'edits' is an ARRAY of edits, and this request carries none. Every edit in one request lands in one transaction or none of them does, so a save from a grid is atomic.");
        }

        if (array.GetArrayLength() == 0)
        {
            return CatalogEditParse.Malformed(
                "'edits' is empty, so this request would apply nothing. An empty save is a console defect rather than an intent.");
        }

        // Before the walk, because every entry other than an add costs a store round trip to resolve its
        // target, and refusing after the work is done would be refusing the answer rather than the request.
        // The refusal names ONE thing rather than a thousand findings, for the same reason.
        if (array.GetArrayLength() > CatalogRequest.MaxEditsPerRequest)
        {
            return CatalogEditParse.Malformed(FormattableString.Invariant(
                $"'edits' carries {array.GetArrayLength().ToString(CultureInfo.InvariantCulture)} entries and one request takes at most {CatalogRequest.MaxEditsPerRequest.ToString(CultureInfo.InvariantCulture)}. Every entry other than an add costs a store round trip, so the array is capped. An operator with more than that to change has a bundle import rather than one enormous edit."));
        }

        var edits = new List<ContentEdit>(array.GetArrayLength());
        var findings = new List<CatalogFindingPayload>();
        int ordinal = 0;
        foreach (JsonElement entry in array.EnumerateArray())
        {
            ContentEdit? edit = await OneAsync(store, registry, entry, ordinal, findings, cancellationToken)
                .ConfigureAwait(false);
            if (edit is not null)
            {
                edits.Add(edit);
            }

            ordinal++;
        }

        return findings.Count > 0 ? CatalogEditParse.Refuse(findings) : CatalogEditParse.Parsed(edits);
    }

    /// <summary>
    /// One edit entry, or null with its findings recorded. It never throws for a payload defect, because a
    /// throw would take the whole array down at the first bad entry and lose the other findings.
    /// </summary>
    static async Task<ContentEdit?> OneAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        JsonElement entry,
        int ordinal,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            findings.Add(Finding(UnknownFieldCode, string.Empty, 0, At(ordinal, "is not a JSON object.")));
            return null;
        }

        if (!CatalogRequest.TryOptionalString(entry, "op", out string? op, out string? refusal)
            || !CatalogRequest.TryOptionalString(entry, "typeKey", out string? typeKey, out refusal)
            || !CatalogRequest.TryOptionalString(entry, "key", out string? key, out refusal)
            || !CatalogRequest.TryCount(entry, "id", 0, out int id, out refusal))
        {
            findings.Add(Finding(UnknownFieldCode, string.Empty, 0, At(ordinal, refusal!)));
            return null;
        }

        if (typeKey is null
            || !CatalogRequest.TryType(registry, typeKey, out ContentTypeRegistration? type, out refusal))
        {
            findings.Add(Finding(
                UnknownFieldCode,
                typeKey ?? string.Empty,
                id,
                At(ordinal, refusal ?? "names no 'typeKey'.")));
            return null;
        }

        if (op is not ("add" or "update" or "retire" or "fork"))
        {
            findings.Add(Finding(UnknownFieldCode, type.TypeKey, id, At(
                ordinal,
                FormattableString.Invariant(
                    $"carries op '{op}', and the four operations are 'add', 'update', 'retire' and 'fork'. A fork is an op VALUE rather than an action of its own, because it is an edit against the open draft like the other three."))));
            return null;
        }

        // The TARGET first, because every operation other than an add names a row that has to be there and
        // its key is what the draft deduplicates on.
        ContentRow? target = null;
        if (!string.Equals(op, "add", StringComparison.Ordinal))
        {
            target = await ResolveAsync(store, type, id, key, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                findings.Add(Finding(ReferenceCode, type.TypeKey, id, At(
                    ordinal,
                    FormattableString.Invariant(
                        $"names no live row of type '{type.TypeKey}': id {id.ToString(CultureInfo.InvariantCulture)}, key '{key ?? string.Empty}'."))));
                return null;
            }
        }

        ContentKey rowKey = target?.Key ?? new ContentKey(key ?? string.Empty);
        if (target is null && string.IsNullOrEmpty(key))
        {
            findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(ordinal, "names no 'key'.")));
            return null;
        }

        // An ADD introduces its own key, so the shape rule runs HERE. Every other op takes its key from the
        // row the store already holds, which was checked when that row was published. A draft that accepted a
        // malformed key was wedged: the sweep reported it forever, the publish refused forever, and the only
        // removal on the seam is a discard, which takes every other pending edit in the draft with it.
        if (target is null && ContentKeyShape.Defect(key!) is string defect)
        {
            findings.Add(Finding(KeyShapeCode, type.TypeKey, 0, At(
                ordinal,
                FormattableString.Invariant(
                    $"names key '{key}', which is {defect}. {ContentKeyShape.Rule}"))));
            return null;
        }

        IReadOnlyList<ContentFieldEdit> fields = Fields(type, rowKey, entry, ordinal, findings);
        switch (op)
        {
            case "add":
                return await AddAsync(store, type, rowKey, fields, entry, ordinal, findings, cancellationToken)
                    .ConfigureAwait(false);

            case "update":
                return findings.Count > 0
                    ? null
                    : ContentEdit.Update(type.Type, target!.Id, rowKey, fields);

            case "retire":
                return await RetireAsync(store, type, target!, entry, ordinal, findings, cancellationToken)
                    .ConfigureAwait(false);

            default:
                return await ForkAsync(store, type, target!, rowKey, fields, entry, ordinal, findings, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>An add, whose id does not exist yet and whose family is named by KEY rather than by number.</summary>
    static async Task<ContentEdit?> AddAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration type,
        ContentKey key,
        IReadOnlyList<ContentFieldEdit> fields,
        JsonElement entry,
        int ordinal,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryOptionalString(entry, "family", out string? family, out string? refusal))
        {
            findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(ordinal, refusal!)));
            return null;
        }

        long? familyId = null;
        if (family is not null)
        {
            IReadOnlyList<ContentFamily> families = await store
                .ListFamiliesAsync(type.Type, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < families.Count; i++)
            {
                if (string.Equals(families[i].FamilyKey, family, StringComparison.Ordinal))
                {
                    familyId = families[i].FamilyId;
                    break;
                }
            }

            if (familyId is null)
            {
                findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(
                    ordinal,
                    FormattableString.Invariant(
                        $"names family '{family}', which type '{type.TypeKey}' does not declare."))));
                return null;
            }
        }

        return findings.Count > 0 ? null : ContentEdit.Add(type.Type, key, fields, familyId);
    }

    /// <summary>
    /// A retire, whose policy is placeholder or replacement (contracts 8.2), and whose replacement is named
    /// by KEY. A replacement with no resolvable key is <c>KEC0017</c>: the rule would name a destination that
    /// is not live, and appending it is how a reference walks onto nothing.
    /// </summary>
    static async Task<ContentEdit?> RetireAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration type,
        ContentRow target,
        JsonElement entry,
        int ordinal,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryOptionalString(entry, "policy", out string? policy, out string? refusal)
            || !CatalogRequest.TryOptionalString(entry, "replacementKey", out string? replacementKey, out refusal))
        {
            findings.Add(Finding(UnknownFieldCode, type.TypeKey, target.Id, At(ordinal, refusal!)));
            return null;
        }

        switch (policy)
        {
            case "placeholder":
                return findings.Count > 0
                    ? null
                    : ContentEdit.Retire(type.Type, target.Id, target.Key, ContentRetirePolicy.Placeholder, 0);

            case "replacement":
                if (replacementKey is null)
                {
                    findings.Add(Finding(ReplacementCode, type.TypeKey, target.Id, At(
                        ordinal,
                        "is a replacement retire and names no 'replacementKey'. The reference moves onto the replacement, so the rule cannot be appended without one.")));
                    return null;
                }

                ContentRow? replacement = await CatalogRequest
                    .FindByKeyAsync(store, type, replacementKey, cancellationToken).ConfigureAwait(false);
                if (replacement is null || replacement.IsRetired)
                {
                    findings.Add(Finding(ReplacementCode, type.TypeKey, target.Id, At(
                        ordinal,
                        FormattableString.Invariant(
                            $"names replacement key '{replacementKey}', which is not a live row of type '{type.TypeKey}'."))));
                    return null;
                }

                return findings.Count > 0
                    ? null
                    : ContentEdit.Retire(
                        type.Type, target.Id, target.Key, ContentRetirePolicy.Replacement, replacement.Id);

            default:
                findings.Add(Finding(UnknownFieldCode, type.TypeKey, target.Id, At(
                    ordinal,
                    FormattableString.Invariant(
                        $"carries policy '{policy}', and a retire names 'placeholder' or 'replacement'."))));
                return null;
        }
    }

    /// <summary>
    /// A fork, the keep-legacy copy. Its <c>fields</c> are the changes to the ORIGINAL row, which is the
    /// direction that reads correctly at a console: the author is editing the row they have open and the
    /// fork is how they say "keep what players already rolled". The copy's allocated id does NOT come back
    /// from an edit, because ids are allocated at publish.
    /// </summary>
    static async Task<ContentEdit?> ForkAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration type,
        ContentRow target,
        ContentKey key,
        IReadOnlyList<ContentFieldEdit> fields,
        JsonElement entry,
        int ordinal,
        List<CatalogFindingPayload> findings,
        CancellationToken cancellationToken)
    {
        if (!CatalogRequest.TryOptionalString(entry, "forkKey", out string? forkKey, out string? refusal)
            || !CatalogRequest.TryOptionalString(entry, "flagField", out string? flagField, out refusal))
        {
            findings.Add(Finding(ForkCode, type.TypeKey, target.Id, At(ordinal, refusal!)));
            return null;
        }

        if (string.IsNullOrEmpty(forkKey))
        {
            findings.Add(Finding(ForkCode, type.TypeKey, target.Id, At(
                ordinal,
                "names no 'forkKey'. A key is immutable once published and the engine will not invent one for the copy.")));
            return null;
        }

        // The COPY's key is the second key an edit invents, so it runs the same rule, before the store is
        // asked whether anything holds it. The publish-side fork precondition checks this too, and by then the
        // edit is already in the draft, which is exactly the state an operator cannot get out of piecemeal.
        if (ContentKeyShape.Defect(forkKey) is string defect)
        {
            findings.Add(Finding(KeyShapeCode, type.TypeKey, target.Id, At(
                ordinal,
                FormattableString.Invariant(
                    $"names fork key '{forkKey}', which is {defect}. {ContentKeyShape.Rule}"))));
            return null;
        }

        if (flagField is null || !type.Schema.TryGet(flagField, out ContentFieldEntry? flag)
            || flag.Kind != ContentFieldKind.Bool)
        {
            findings.Add(Finding(ForkCode, type.TypeKey, target.Id, At(
                ordinal,
                FormattableString.Invariant(
                    $"names flag field '{flagField ?? string.Empty}', and a fork sets a Bool field the type's schema declares. The field is the caller's: the engine checks only that it exists and is Bool."))));
            return null;
        }

        if (target.IsRetired)
        {
            findings.Add(Finding(ForkCode, type.TypeKey, target.Id, At(
                ordinal,
                FormattableString.Invariant($"forks row {target.Id.ToString(CultureInfo.InvariantCulture)} ('{target.Key}'), which is already retired."))));
            return null;
        }

        ContentRow? taken = await CatalogRequest
            .FindByKeyAsync(store, type, forkKey, cancellationToken).ConfigureAwait(false);
        if (taken is not null)
        {
            findings.Add(Finding(ForkCode, type.TypeKey, target.Id, At(
                ordinal,
                FormattableString.Invariant(
                    $"names fork key '{forkKey}', which row {taken.Id.ToString(CultureInfo.InvariantCulture)} of type '{type.TypeKey}' already holds."))));
            return null;
        }

        return findings.Count > 0
            ? null
            : ContentEdit.Fork(type.Type, target.Id, key, new ContentKey(forkKey), flagField, fields);
    }

    /// <summary>
    /// The <c>fields</c> object, read through each field's declared KIND, accumulating a finding per bad
    /// value rather than stopping at the first.
    /// </summary>
    static IReadOnlyList<ContentFieldEdit> Fields(
        ContentTypeRegistration type,
        ContentKey key,
        JsonElement entry,
        int ordinal,
        List<CatalogFindingPayload> findings)
    {
        if (!entry.TryGetProperty("fields", out JsonElement fields) || fields.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (fields.ValueKind != JsonValueKind.Object)
        {
            findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(ordinal, "carries a 'fields' that is not an object.")));
            return [];
        }

        var parsed = new List<ContentFieldEdit>();
        foreach (JsonProperty property in fields.EnumerateObject())
        {
            if (!type.Schema.TryGet(property.Name, out ContentFieldEntry? field))
            {
                findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(
                    ordinal,
                    FormattableString.Invariant($"no field '{property.Name}' on type '{type.TypeKey}'."))));
                continue;
            }

            if (field.IsDerivedMarker)
            {
                // The message names the DERIVED key, so an operator sees what they were trying to set rather
                // than a refusal about a field the console showed them read only.
                findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(
                    ordinal,
                    FormattableString.Invariant(
                        $"field '{property.Name}' of type '{type.TypeKey}' is a DERIVED localized text key, '{ContentTextKey.Derive(type.TypeKey, key.Utf8, property.Name)}', so no edit ever carries a value for it."))));
                continue;
            }

            if (!TryValue(field, property.Value, out ContentFieldValue value, out string? why))
            {
                findings.Add(Finding(UnknownFieldCode, type.TypeKey, 0, At(
                    ordinal,
                    FormattableString.Invariant($"field '{property.Name}': {why}"))));
                continue;
            }

            parsed.Add(new ContentFieldEdit(field.Name, value));
        }

        return parsed;
    }

    /// <summary>
    /// One JSON value through its field's kind, the exact inverse of how a read action renders it, so a
    /// console writes back what it was handed.
    /// </summary>
    static bool TryValue(ContentFieldEntry field, JsonElement value, out ContentFieldValue parsed, out string? why)
    {
        why = null;
        if (value.ValueKind == JsonValueKind.Null)
        {
            // Null is ABSENT rather than zero, which is what clears an optional field.
            parsed = ContentFieldValue.Absent(field.Kind);
            return true;
        }

        switch (field.Kind)
        {
            case ContentFieldKind.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    parsed = default;
                    why = "a Bool takes true or false.";
                    return false;
                }

                parsed = ContentFieldValue.OfNumber(ContentFieldKind.Bool, value.GetBoolean() ? 1 : 0);
                return true;

            case ContentFieldKind.TagList:
                return TryTagList(value, out parsed, out why);

            case ContentFieldKind.OpaqueBytes:
                if (value.ValueKind != JsonValueKind.String
                    || !TryHex(value.GetString() ?? string.Empty, out byte[] bytes))
                {
                    parsed = default;
                    why = "opaque bytes are LOWER HEX, the same rendering the audit ledger and every read use.";
                    return false;
                }

                parsed = ContentFieldValue.OfBytes(ContentFieldKind.OpaqueBytes, bytes);
                return true;

            default:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long number))
                {
                    parsed = default;
                    why = FormattableString.Invariant(
                        $"a {field.Kind} takes the STORED integer, which for a scaled int is the value times the schema's scale of {field.Scale.ToString(CultureInfo.InvariantCulture)}.");
                    return false;
                }

                parsed = ContentFieldValue.OfNumber(field.Kind, number);
                return true;
        }
    }

    /// <summary>A tag list, in AUTHORED order, which is the order it was written in and is never sorted.</summary>
    static bool TryTagList(JsonElement value, out ContentFieldValue parsed, out string? why)
    {
        parsed = default;
        why = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            why = "a tag list is an ARRAY of tag ids, in authored order.";
            return false;
        }

        var ids = new List<int>(value.GetArrayLength());
        foreach (JsonElement id in value.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int tag) || tag < 1)
            {
                why = "a tag list holds tag IDS, which are whole numbers of 1 or above.";
                return false;
            }

            ids.Add(tag);
        }

        parsed = ContentRowCodecBase.TagListValue(ids);
        return true;
    }

    /// <summary>
    /// A hex string as bytes. An odd length and a stray character are both a refusal rather than a throw the
    /// dispatch would turn into a 500.
    /// <para>
    /// The pair-at-a-time <c>byte.TryParse</c> this replaced allowed WHITESPACE, because
    /// <see cref="NumberStyles.HexNumber"/> carries <c>AllowLeadingWhite</c> and <c>AllowTrailingWhite</c>.
    /// So <c>" a 1"</c> parsed as the two bytes <c>0a01</c> and a corrupted field was stored as different
    /// bytes under a 200, which is the worst answer available for a field an author cannot read.
    /// </para>
    /// <para>
    /// Case is accepted and the rendering stays lower hex, which is what the refusal message means. That is
    /// the behaviour the pair parse had too.
    /// </para>
    /// </summary>
    static bool TryHex(string text, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(text);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>The row an edit targets, by id or by key, and null when neither reaches one.</summary>
    static async Task<ContentRow?> ResolveAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration type,
        int id,
        string? key,
        CancellationToken cancellationToken)
    {
        if (id > 0)
        {
            IReadOnlyList<ContentRowRevision> history = await store
                .GetRowHistoryAsync(type.Type, id, cancellationToken).ConfigureAwait(false);
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (history[i].ReplacedInVersion is null)
                {
                    return history[i].Row;
                }
            }

            return null;
        }

        return key is null
            ? null
            : await CatalogRequest.FindByKeyAsync(store, type, key, cancellationToken).ConfigureAwait(false);
    }

    static CatalogFindingPayload Finding(string code, string typeKey, int id, string message)
        => new(code, typeKey, id, message);

    static string At(int ordinal, string detail)
        => FormattableString.Invariant($"Edit {ordinal.ToString(CultureInfo.InvariantCulture)} {detail}");
}

/// <summary>
/// What reading an edit payload produced: the edits, or the findings that refuse them. One or the other, and
/// never a half-applied batch, because every edit in one request lands in ONE transaction or none of them
/// does.
/// </summary>
internal sealed class CatalogEditParse
{
    CatalogEditParse(
        IReadOnlyList<ContentEdit> edits,
        IReadOnlyList<CatalogFindingPayload> findings,
        string? malformed)
    {
        Edits = edits;
        Findings = findings;
        MalformedReason = malformed;
    }

    /// <summary>The parsed edits, in payload order, which is the order ids are allocated in at publish.</summary>
    public IReadOnlyList<ContentEdit> Edits { get; }

    /// <summary>Every finding the boundary check produced, empty when the payload was accepted.</summary>
    public IReadOnlyList<CatalogFindingPayload> Findings { get; }

    /// <summary>Why the payload was not an edit list at all, or null when it was.</summary>
    public string? MalformedReason { get; }

    /// <summary>True when the payload produced edits and no finding.</summary>
    public bool IsValid => MalformedReason is null && Findings.Count == 0;

    /// <summary>A payload that parsed.</summary>
    /// <param name="edits">The edits.</param>
    public static CatalogEditParse Parsed(IReadOnlyList<ContentEdit> edits) => new(edits, [], null);

    /// <summary>A payload the boundary check refused.</summary>
    /// <param name="findings">The findings.</param>
    public static CatalogEditParse Refuse(IReadOnlyList<CatalogFindingPayload> findings) => new([], findings, null);

    /// <summary>A payload that was not an edit list at all.</summary>
    /// <param name="reason">Why.</param>
    public static CatalogEditParse Malformed(string reason) => new([], [], reason);
}
