using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.NetWorld;

namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// How every catalog action reads its request body, in ONE place, so a type key, a page bound and an
/// operator identity are parsed the same way whichever action received them.
/// <para>
/// <b>A refusal is a reason rather than a throw.</b> An operator typing a payload by hand is the ordinary
/// case here, so a property of the wrong shape names itself and the action turns that into a 400, instead
/// of a cast exception the dispatch would turn into a 500 with a stack trace behind it.
/// </para>
/// <para>
/// Property names are camelCase, matching the responses, because the endpoint serializes with the web
/// defaults and a console that reads <c>expectedBaseVersion</c> out of a body writes it back under the same
/// spelling.
/// </para>
/// </summary>
internal static class CatalogRequest
{
    /// <summary>The longest operator identity the engine records (spec 10.10), beyond which it is a 400.</summary>
    public const int MaxOperatorLength = 128;

    /// <summary>The longest note the authoring store accepts.</summary>
    public const int MaxNoteLength = 1024;

    /// <summary>The reason token a body that is absent, or a property of the wrong shape, is refused under.</summary>
    public const string MalformedRequestReason = "malformed-request";

    /// <summary>
    /// The operator identity the console FORWARDED, or empty when it forwarded none.
    /// <para>
    /// <b>It is unverified and it is not the actor.</b> The bearer token is one token and is not an
    /// identity, so the engine records what it authenticated beside what the console asserted. A request
    /// with NO operator is accepted, because refusing it would break a scripted maintenance call with no
    /// human behind it, and one over <see cref="MaxOperatorLength"/> characters is refused.
    /// </para>
    /// </summary>
    public static bool TryOperator(JsonElement body, out string operatorId, out string? refusal)
    {
        operatorId = string.Empty;
        if (!TryOptionalString(body, "operator", out string? value, out refusal))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        if (value.Length > MaxOperatorLength)
        {
            refusal = FormattableString.Invariant(
                $"'operator' is at most {MaxOperatorLength} characters and this request carries {value.Length}. It takes a STABLE identity such as an object id, never a display name, because a display name breaks the audit trail the day someone renames themselves.");
            return false;
        }

        operatorId = value;
        return true;
    }

    /// <summary>The operator's note, empty when none, refused over <see cref="MaxNoteLength"/> characters.</summary>
    public static bool TryNote(JsonElement body, out string note, out string? refusal)
    {
        note = string.Empty;
        if (!TryOptionalString(body, "note", out string? value, out refusal))
        {
            return false;
        }

        if (value is null)
        {
            return true;
        }

        if (value.Length > MaxNoteLength)
        {
            refusal = FormattableString.Invariant(
                $"'note' is at most {MaxNoteLength} characters and this request carries {value.Length}.");
            return false;
        }

        note = value;
        return true;
    }

    /// <summary>The registered type a request names, or a refusal naming the key that did not resolve.</summary>
    public static bool TryType(
        ContentTypeRegistry registry,
        JsonElement body,
        out ContentTypeRegistration type,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(registry);

        type = null!;
        if (!TryOptionalString(body, "typeKey", out string? typeKey, out refusal))
        {
            return false;
        }

        if (typeKey is null)
        {
            refusal = "'typeKey' names the content type to read, and this request carries none.";
            return false;
        }

        return TryType(registry, typeKey, out type, out refusal);
    }

    /// <summary>The registered type one key names, which an edit entry resolves per entry rather than per request.</summary>
    public static bool TryType(
        ContentTypeRegistry registry,
        string typeKey,
        out ContentTypeRegistration type,
        out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(registry);

        refusal = null;
        if (!registry.TryGetByKey(typeKey, out ContentTypeRegistration? found))
        {
            type = null!;
            refusal = FormattableString.Invariant($"No content type is registered under the type key '{typeKey}'.");
            return false;
        }

        type = found;
        return true;
    }

    /// <summary>A version number, where 0 is the caller asking for the current live set rather than an error.</summary>
    public static bool TryVersion(JsonElement body, string name, out int version, out string? refusal)
        => TryCount(body, name, 0, out version, out refusal);

    /// <summary>A non-negative integer property, its default when absent, or a refusal naming it.</summary>
    public static bool TryCount(JsonElement body, string name, int fallback, out int value, out string? refusal)
    {
        refusal = null;
        value = fallback;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            value = fallback;
            refusal = FormattableString.Invariant($"'{name}' is a whole number, and this request carries something else.");
            return false;
        }

        if (value < 0)
        {
            refusal = FormattableString.Invariant($"'{name}' may not be negative.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// An integer property that may be explicitly NULL, which is how a pin says "clear the hold" and how a
    /// publish leaves a minimum build to carry forward. Absent and null are told apart, because they mean
    /// different things on those two actions.
    /// </summary>
    public static bool TryNullableCount(
        JsonElement body,
        string name,
        out int? value,
        out bool present,
        out string? refusal)
    {
        refusal = null;
        value = null;
        present = body.TryGetProperty(name, out JsonElement property);
        if (!present || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int number))
        {
            refusal = FormattableString.Invariant($"'{name}' is a whole number or null, and this request carries something else.");
            return false;
        }

        if (number < 0)
        {
            refusal = FormattableString.Invariant($"'{name}' may not be negative.");
            return false;
        }

        value = number;
        return true;
    }

    /// <summary>An optional string property, null when absent, or a refusal naming it.</summary>
    public static bool TryOptionalString(JsonElement body, string name, out string? value, out string? refusal)
    {
        refusal = null;
        value = null;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            refusal = FormattableString.Invariant($"'{name}' is a string, and this request carries something else.");
            return false;
        }

        value = property.GetString();
        return true;
    }

    /// <summary>An optional boolean property, false when absent, or a refusal naming it.</summary>
    public static bool TryFlag(JsonElement body, string name, out bool value, out string? refusal)
    {
        refusal = null;
        value = false;
        if (!body.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            refusal = FormattableString.Invariant($"'{name}' is true or false, and this request carries something else.");
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    /// <summary>The body a mutating action needs, or the refusal for one that arrived without it.</summary>
    public static bool TryBody(
        JsonElement? payload,
        string action,
        string names,
        out JsonElement body,
        out AdminActionResult refusal)
    {
        if (payload is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            body = element;
            refusal = default;
            return true;
        }

        body = default;
        refusal = CatalogRefusal.Malformed(FormattableString.Invariant(
            $"{action} needs a JSON object body naming {names}."));
        return false;
    }

    /// <summary>
    /// The row one key names, found through the seam's own prefix filter. The prefix can match siblings (a
    /// key is a prefix of every longer key), so the walk pages until it finds the EXACT key or runs out.
    /// </summary>
    public static async Task<ContentRow?> FindByKeyAsync(
        IContentAuthoringStore store,
        ContentTypeRegistration type,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(type);

        var wanted = new ContentKey(key);
        int skip = 0;
        while (true)
        {
            ContentRowPage page = await store
                .ListRowsAsync(type.Type, 0, key, true, skip, CatalogAdminActions.MaxPageSize, cancellationToken)
                .ConfigureAwait(false);

            foreach (ContentRow row in page.Rows)
            {
                if (row.Key.Equals(wanted))
                {
                    return row;
                }
            }

            skip += page.Rows.Count;
            if (page.Rows.Count == 0 || skip >= page.Total)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Every row of every registered type live at one version, paged through the seam. It is what the diff
    /// compares two versions over, and it walks the TYPES rather than the pack, so it answers on a store
    /// whose pack files are not reachable.
    /// </summary>
    public static async Task<List<ContentRowRevision>> LiveRowsAsync(
        IContentAuthoringStore store,
        ContentTypeRegistry registry,
        int versionNumber,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);

        var rows = new List<ContentRowRevision>();
        IReadOnlyList<ContentTypeRegistration> registrations = registry.ByTypeId;
        for (int i = 0; i < registrations.Count; i++)
        {
            int skip = 0;
            while (true)
            {
                ContentRowPage page = await store
                    .ListRowsAsync(
                        registrations[i].Type,
                        versionNumber,
                        null,
                        true,
                        skip,
                        CatalogAdminActions.MaxPageSize,
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (ContentRow row in page.Rows)
                {
                    rows.Add(new ContentRowRevision(row, page.VersionNumber, null, null));
                }

                skip += page.Rows.Count;
                if (page.Rows.Count == 0 || skip >= page.Total)
                {
                    break;
                }
            }
        }

        return rows;
    }
}
