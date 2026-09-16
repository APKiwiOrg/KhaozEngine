using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The bundle as a JSON DOCUMENT, written and read here and nowhere else.
/// <para>
/// <b>Every property is written explicitly, in a fixed order, through a writer rather than a reflected
/// serializer.</b> A bundle is a seeding artifact that lands in a repository beside the code it seeds, so two
/// exports of one version have to be the same bytes: a reflected order depends on the declaration order of a
/// type nobody thinks of as a format, and a member added in the middle of a record would silently rewrite
/// every checked-in bundle's diff.
/// </para>
/// <para>
/// Numbers are written as numbers and bytes as LOWER HEX, because a bundle is read and edited by hand as
/// often as it is generated, and base64 in a field an author is retuning is a wall.
/// </para>
/// <para>
/// Reading is TOTAL for shape: a document this build cannot read is a
/// <see cref="ContentAuthoringException"/> naming what was wrong, never a half-built bundle.
/// </para>
/// </summary>
public static class ContentBundleJson
{
    /// <summary>The writer options an export uses: indented, so a checked-in bundle diffs line by line.</summary>
    static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        SkipValidation = false,
    };

    /// <summary>The reader options an import uses: comments and a trailing comma allowed, because a hand-authored seed carries both.</summary>
    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The bundle as one JSON document. Two exports of one version produce identical bytes.
    /// </summary>
    /// <param name="bundle">The bundle to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
    public static string Write(ContentBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", bundle.FormatVersion);
            writer.WriteString("storeEpoch", bundle.StoreEpoch);
            writer.WriteNumber("sourceVersion", bundle.SourceVersion);
            WriteTypes(writer, bundle.Types);
            WriteFamilies(writer, bundle.Families);
            WriteRows(writer, bundle.Rows);
            WriteRules(writer, bundle.Rules);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads a bundle document.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ContentAuthoringException">The document is not JSON, carries a format version this build does not know, is missing a member, or carries a member whose value is not the shape that member takes (a number that is not the integer it wants, hex that is not hex).</exception>
    public static ContentBundle Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException malformed)
        {
            throw Refuse("The bundle is not a JSON document: " + malformed.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Refuse("A bundle document is a JSON object.");
            }

            int formatVersion = Int(root, "formatVersion");
            if (formatVersion != ContentBundle.CurrentFormatVersion)
            {
                throw Refuse(FormattableString.Invariant(
                    $"The bundle declares format version {formatVersion} and this build reads {ContentBundle.CurrentFormatVersion}. A format version is a refusal of the whole document rather than a best-effort partial read."));
            }

            return new ContentBundle(
                formatVersion,
                Text(root, "storeEpoch"),
                Int(root, "sourceVersion"),
                ReadTypes(root),
                ReadRows(root),
                ReadFamilies(root),
                ReadRules(root));
        }
    }

    static void WriteTypes(Utf8JsonWriter writer, IReadOnlyList<ContentBundleType> types)
    {
        writer.WriteStartArray("types");
        for (int i = 0; i < types.Count; i++)
        {
            ContentBundleType type = types[i];
            writer.WriteStartObject();
            writer.WriteNumber("typeId", type.Type.Value);
            writer.WriteString("typeKey", type.TypeKey);
            writer.WriteNumber("defaultVisibility", (int)type.DefaultVisibility);
            writer.WriteNumber("chunkSlots", type.ChunkSlots);
            if (type.MaxDefinitionId is int ceiling)
            {
                writer.WriteNumber("maxDefinitionId", ceiling);
            }
            else
            {
                writer.WriteNull("maxDefinitionId");
            }

            writer.WriteStartArray("fields");
            IReadOnlyList<ContentFieldEntry> fields = type.Schema.Fields;
            for (int f = 0; f < fields.Count; f++)
            {
                ContentFieldEntry field = fields[f];
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteNumber("kind", (int)field.Kind);
                if (field.ReferenceTarget is string target)
                {
                    writer.WriteString("referenceTarget", target);
                }
                else
                {
                    writer.WriteNull("referenceTarget");
                }

                writer.WriteNumber("visibility", (int)field.Visibility);
                writer.WriteBoolean("required", field.Required);
                writer.WriteNumber("scale", field.Scale);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    static void WriteFamilies(Utf8JsonWriter writer, IReadOnlyList<ContentFamily> families)
    {
        writer.WriteStartArray("families");
        for (int i = 0; i < families.Count; i++)
        {
            ContentFamily family = families[i];
            writer.WriteStartObject();
            writer.WriteNumber("familyId", family.FamilyId);
            writer.WriteNumber("typeId", family.Type.Value);
            writer.WriteString("familyKey", family.FamilyKey);
            writer.WriteNumber("blockSize", family.BlockSize);
            writer.WriteBoolean("retired", family.IsRetired);
            writer.WriteNumber("createdInVersion", family.CreatedInVersion);
            writer.WriteStartArray("blocks");
            for (int b = 0; b < family.Blocks.Count; b++)
            {
                ContentFamilyBlock block = family.Blocks[b];
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", block.BlockOrdinal);
                writer.WriteNumber("baseId", block.BaseId);
                writer.WriteNumber("nextFreeId", block.NextFreeId);
                writer.WriteNumber("reservedInVersion", block.ReservedInVersion);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    static void WriteRows(Utf8JsonWriter writer, IReadOnlyList<ContentBundleRow> rows)
    {
        writer.WriteStartArray("rows");
        for (int i = 0; i < rows.Count; i++)
        {
            ContentBundleRow row = rows[i];
            writer.WriteStartObject();
            writer.WriteNumber("typeId", row.Type.Value);
            if (row.Id is int id)
            {
                writer.WriteNumber("id", id);
            }
            else
            {
                // The id is OPTIONAL and that is the whole of the import id rule: a row that names one is
                // imported with it, and a row that names none is allocated one in this list's order.
                writer.WriteNull("id");
            }

            writer.WriteString("key", row.Key.ToString());
            writer.WriteBoolean("retired", row.IsRetired);
            if (row.FamilyKey is string family)
            {
                writer.WriteString("familyKey", family);
            }
            else
            {
                writer.WriteNull("familyKey");
            }

            writer.WriteStartArray("fields");
            for (int f = 0; f < row.Fields.Count; f++)
            {
                ContentFieldEdit field = row.Fields[f];
                writer.WriteStartObject();
                writer.WriteString("name", field.Name);
                writer.WriteNumber("kind", (int)field.Value.Kind);
                if (field.Value.IsAbsent)
                {
                    writer.WriteBoolean("absent", true);
                }
                else if (ContentFieldValue.StoresNumber(field.Value.Kind))
                {
                    writer.WriteNumber("number", field.Value.Number);
                }
                else
                {
                    writer.WriteString("bytes", Convert.ToHexString(field.Value.Bytes.Span).ToLowerInvariant());
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    static void WriteRules(Utf8JsonWriter writer, IReadOnlyList<RemapRule> rules)
    {
        writer.WriteStartArray("rules");
        for (int i = 0; i < rules.Count; i++)
        {
            RemapRule rule = rules[i];
            writer.WriteStartObject();
            writer.WriteNumber("sequence", rule.Sequence);
            writer.WriteNumber("introducedIn", rule.IntroducedIn);
            writer.WriteNumber("typeId", rule.Type.Value);
            writer.WriteNumber("kind", (int)rule.Kind);
            writer.WriteNumber("fromId", rule.FromId);
            writer.WriteNumber("toId", rule.ToId);
            writer.WriteString("payload", Convert.ToHexString(rule.Payload).ToLowerInvariant());
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    static IReadOnlyList<ContentBundleType> ReadTypes(JsonElement root)
    {
        var types = new List<ContentBundleType>();
        foreach (JsonElement element in Array(root, "types"))
        {
            var fields = new List<ContentFieldEntry>();
            foreach (JsonElement field in Array(element, "fields"))
            {
                fields.Add(new ContentFieldEntry(
                    Text(field, "name"),
                    (ContentFieldKind)Int(field, "kind"),
                    OptionalText(field, "referenceTarget"),
                    (ContentVisibility)Int(field, "visibility"),
                    Bool(field, "required"),
                    Int(field, "scale")));
            }

            types.Add(new ContentBundleType(
                new ContentTypeId((ushort)Int(element, "typeId")),
                Text(element, "typeKey"),
                (ContentVisibility)Int(element, "defaultVisibility"),
                Int(element, "chunkSlots"),
                OptionalInt(element, "maxDefinitionId"),
                Schema(fields)));
        }

        return types;
    }

    static IReadOnlyList<ContentFamily> ReadFamilies(JsonElement root)
    {
        var families = new List<ContentFamily>();
        foreach (JsonElement element in Array(root, "families"))
        {
            long familyId = Long(element, "familyId");
            int blockSize = Int(element, "blockSize");
            var blocks = new List<ContentFamilyBlock>();
            foreach (JsonElement block in Array(element, "blocks"))
            {
                blocks.Add(new ContentFamilyBlock(
                    familyId,
                    Int(block, "ordinal"),
                    Int(block, "baseId"),
                    blockSize,
                    Int(block, "nextFreeId"),
                    Int(block, "reservedInVersion")));
            }

            try
            {
                families.Add(new ContentFamily(
                    familyId,
                    new ContentTypeId((ushort)Int(element, "typeId")),
                    Text(element, "familyKey"),
                    blockSize,
                    Bool(element, "retired"),
                    Int(element, "createdInVersion"),
                    blocks));
            }
            catch (ArgumentException malformed)
            {
                throw Refuse("A bundle family is malformed: " + malformed.Message);
            }
        }

        return families;
    }

    static IReadOnlyList<ContentBundleRow> ReadRows(JsonElement root)
    {
        var rows = new List<ContentBundleRow>();
        foreach (JsonElement element in Array(root, "rows"))
        {
            var fields = new List<ContentFieldEdit>();
            foreach (JsonElement field in Array(element, "fields"))
            {
                var kind = (ContentFieldKind)Int(field, "kind");
                fields.Add(new ContentFieldEdit(Text(field, "name"), Value(field, kind)));
            }

            rows.Add(new ContentBundleRow(
                new ContentTypeId((ushort)Int(element, "typeId")),
                OptionalInt(element, "id"),
                new ContentKey(Text(element, "key")),
                Bool(element, "retired"),
                OptionalText(element, "familyKey"),
                fields));
        }

        return rows;
    }

    static IReadOnlyList<RemapRule> ReadRules(JsonElement root)
    {
        var rules = new List<RemapRule>();
        foreach (JsonElement element in Array(root, "rules"))
        {
            try
            {
                rules.Add(new RemapRule(
                    Int(element, "sequence"),
                    Int(element, "introducedIn"),
                    new ContentTypeId((ushort)Int(element, "typeId")),
                    (RemapRuleKind)Int(element, "kind"),
                    Int(element, "fromId"),
                    Int(element, "toId"),
                    Bytes(element, "payload")));
            }
            catch (ArgumentException malformed)
            {
                throw Refuse("A bundle rule is malformed: " + malformed.Message);
            }
        }

        return rules;
    }

    static ContentFieldValue Value(JsonElement field, ContentFieldKind kind)
    {
        if (field.TryGetProperty("absent", out JsonElement absent) && absent.ValueKind == JsonValueKind.True)
        {
            return ContentFieldValue.Absent(kind);
        }

        try
        {
            return ContentFieldValue.StoresNumber(kind)
                ? ContentFieldValue.OfNumber(kind, Long(field, "number"))
                : ContentFieldValue.OfBytes(kind, Bytes(field, "bytes"));
        }
        catch (ArgumentException malformed)
        {
            throw Refuse("A bundle field value is malformed: " + malformed.Message);
        }
    }

    static ContentFieldSchema Schema(IReadOnlyList<ContentFieldEntry> fields)
    {
        try
        {
            return new ContentFieldSchema(fields);
        }
        catch (ArgumentException malformed)
        {
            throw Refuse("A bundle type's schema is malformed: " + malformed.Message);
        }
    }

    static JsonElement.ArrayEnumerator Array(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Array)
        {
            throw Refuse(FormattableString.Invariant($"A bundle carries a '{name}' array, and this one does not."));
        }

        return element.EnumerateArray();
    }

    static string Text(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw Refuse(FormattableString.Invariant($"A bundle member '{name}' is a string, and this one is not."));

    static string? OptionalText(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    static int Int(JsonElement parent, string name) => (int)Long(parent, name);

    // A JSON number is a decimal literal of ANY magnitude, so Number-kinded is not yet integer-valued: 1.5,
    // an id of 9,999,999,999 against an int, and 1e308 against a long are all Number and none of them
    // converts. Try rather than Get, because a Get on a miss is a FormatException, which is neither this
    // reader's declared surface nor something a caller can tell apart from a fault. An author's typo is a
    // refusal naming the member, the same as every other shape defect in this file.
    static int? OptionalInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return element.TryGetInt32(out int value)
            ? value
            : throw Refuse(FormattableString.Invariant(
                $"A bundle member '{name}' is a 32-bit integer, and this one is not."));
    }

    static long Long(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            throw Refuse(FormattableString.Invariant($"A bundle member '{name}' is a number, and this one is not."));
        }

        return element.TryGetInt64(out long value)
            ? value
            : throw Refuse(FormattableString.Invariant(
                $"A bundle member '{name}' is a 64-bit integer, and this one is not."));
    }

    static bool Bool(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw Refuse(FormattableString.Invariant($"A bundle member '{name}' is a boolean, and this one is not."));

    static byte[] Bytes(JsonElement parent, string name)
    {
        string hex = Text(parent, name);
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw Refuse(FormattableString.Invariant($"A bundle member '{name}' is lower hex, and '{hex}' is not."));
        }
    }

    static ContentAuthoringException Refuse(string message)
        => new(message, default, 0, ContentAuthoringException.BundleFormatReason);
}
