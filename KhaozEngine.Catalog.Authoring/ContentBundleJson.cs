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
/// <see cref="ContentAuthoringException"/> naming what was wrong, never a half-built bundle. A shape rule this
/// reader checks itself carries its <see cref="ContentAuthoringException.Refusal"/> and the rooted
/// <see cref="ContentAuthoringException.JsonPath"/> of the member at fault, and so does a value a lower
/// constructor refuses, mapped from that constructor's typed finding.
/// </para>
/// </summary>
public static class ContentBundleJson
{
    /// <summary>The writer options an export uses: indented, so a checked-in bundle diffs line by line.</summary>
    static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        SkipValidation = false,
    };

    /// <summary>The reader options an import uses: comments and a trailing comma allowed, because a hand-authored seed carries both.</summary>
    static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The rooted path of the document itself, which every refusal's <c>JsonPath</c> starts from.</summary>
    const string RootPath = "$";

    /// <summary>
    /// The bundle as one JSON document. Two exports of one version produce identical bytes.
    /// </summary>
    /// <param name="bundle">The bundle to write.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is null.</exception>
    /// <exception cref="ContentAuthoringException">The bundle declares a format this build does not write, or is format 2 and lost its text section, which no document may claim to be text free.</exception>
    public static string Write(ContentBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.FormatVersion is not ContentBundle.CurrentFormatVersion and not ContentBundle.TextFormatVersion)
        {
            // A later format is refused whole, text section or not, exactly as a read refuses it.
            throw ContentBundleTextCompatibility.UnsupportedFormat(bundle.FormatVersion);
        }

        if (bundle.FormatVersion != ContentBundle.CurrentFormatVersion && bundle.TextState is null)
        {
            throw new ContentAuthoringException(
                ContentAuthoringRefusal.BundleTextSectionMissing,
                FormattableString.Invariant(
                    $"The bundle is format {bundle.FormatVersion} and carries no text section, so writing it would claim a text section it lost."),
                default,
                0,
                ContentAuthoringException.BundleFormatReason)
            {
                ActualFormatVersion = bundle.FormatVersion,
            };
        }

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
            if (bundle.TextState is ContentBundleTextState text)
            {
                // AFTER every row member, so a format 1 document is the same bytes it always was.
                ContentBundleTextJson.Write(writer, text);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads the declared bundle format version without reading the rest of the bundle.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <returns>The declared format version, including a version this build does not support.</returns>
    /// <remarks>
    /// This uses the same JSONC policy and version member rules as <see cref="Read"/>. Comments and trailing
    /// commas are accepted. Format support is validated by <see cref="Read"/> after this precheck.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <exception cref="ContentAuthoringException">The document is not JSON, is not an object, or has no integer <c>formatVersion</c> member.</exception>
    public static int ReadFormatVersion(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using JsonDocument document = ParseDocument(json);
        return FormatVersion(document.RootElement);
    }

    /// <summary>
    /// Reads a bundle document.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
    /// <remarks>
    /// Format 1 reads as before, with no text section, and a format 1 document carrying one is refused. Format
    /// 2 reads its text section too, and refuses a missing section, two declarations or values whose
    /// languages share a canonical identity, a malformed target and an invalid value.
    /// </remarks>
    /// <exception cref="ContentAuthoringException">The document is not JSON, carries a format version this build does not know, is missing a member, or carries a member whose value is not the shape that member takes (a number that is not the integer it wants, hex that is not hex).</exception>
    public static ContentBundle Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document = ParseDocument(json);
        using (document)
        {
            JsonElement root = document.RootElement;
            int formatVersion = FormatVersion(root);
            if (formatVersion != ContentBundle.CurrentFormatVersion && formatVersion != ContentBundle.TextFormatVersion)
            {
                throw ContentBundleTextCompatibility.UnsupportedFormat(formatVersion);
            }

            string storeEpoch = Text(root, RootPath, "storeEpoch");
            int sourceVersion = Int(root, RootPath, "sourceVersion");
            IReadOnlyList<ContentBundleType> types = ReadTypes(root);
            IReadOnlyList<ContentBundleRow> rows = ReadRows(root);
            IReadOnlyList<ContentFamily> families = ReadFamilies(root);
            IReadOnlyList<RemapRule> rules = ReadRules(root);
            if (formatVersion == ContentBundle.CurrentFormatVersion)
            {
                // Format 1 is text free by contract, so a text section in one would be silently dropped.
                if (root.TryGetProperty(ContentBundleTextJson.SectionName, out _))
                {
                    throw new ContentAuthoringException(
                        ContentAuthoringRefusal.BundleUnexpectedTextSection,
                        FormattableString.Invariant(
                            $"A format {ContentBundle.CurrentFormatVersion} bundle is text free by contract, and this one carries a '{ContentBundleTextJson.SectionName}' section. Declare format {ContentBundle.TextFormatVersion} for a text-bearing bundle."),
                        default,
                        0,
                        ContentAuthoringException.BundleFormatReason)
                    {
                        JsonPath = Member(RootPath, ContentBundleTextJson.SectionName),
                        ActualFormatVersion = formatVersion,
                    };
                }

                return new ContentBundle(formatVersion, storeEpoch, sourceVersion, types, rows, families, rules);
            }

            return new ContentBundle(
                formatVersion, storeEpoch, sourceVersion, types, rows, families, rules, ContentBundleTextJson.Read(root));
        }
    }

    static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException malformed)
        {
            throw new ContentAuthoringException(
                ContentAuthoringRefusal.BundleJsonMalformed,
                "The bundle is not a JSON document: " + malformed.Message,
                default,
                0,
                ContentAuthoringException.BundleFormatReason,
                malformed)
            {
                JsonPath = malformed.Path,
                LineNumber = malformed.LineNumber,
                BytePositionInLine = malformed.BytePositionInLine,
            };
        }
    }

    static int FormatVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ContentAuthoringException(
                ContentAuthoringRefusal.BundleRootObjectRequired,
                "A bundle document is a JSON object.",
                default,
                0,
                ContentAuthoringException.BundleFormatReason)
            {
                JsonPath = RootPath,
                ActualJsonKind = root.ValueKind,
            };
        }

        return Int(root, RootPath, "formatVersion");
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
        string typesPath = Member(RootPath, "types");
        int typeIndex = 0;
        foreach (JsonElement element in Array(root, RootPath, "types"))
        {
            string typePath = Element(typesPath, typeIndex++);
            string fieldsPath = Member(typePath, "fields");
            int fieldIndex = 0;
            var fields = new List<ContentFieldEntry>();
            foreach (JsonElement field in Array(element, typePath, "fields"))
            {
                string fieldPath = Element(fieldsPath, fieldIndex++);
                fields.Add(new ContentFieldEntry(
                    Text(field, fieldPath, "name"),
                    FieldKind(field, fieldPath, "kind"),
                    OptionalText(field, "referenceTarget"),
                    Visibility(field, fieldPath, "visibility"),
                    Bool(field, fieldPath, "required"),
                    Int(field, fieldPath, "scale")));
            }

            types.Add(new ContentBundleType(
                TypeId(element, typePath, "typeId"),
                Text(element, typePath, "typeKey"),
                Visibility(element, typePath, "defaultVisibility"),
                Int(element, typePath, "chunkSlots"),
                OptionalInt(element, typePath, "maxDefinitionId"),
                Schema(fields, typePath)));
        }

        return types;
    }

    static IReadOnlyList<ContentFamily> ReadFamilies(JsonElement root)
    {
        var families = new List<ContentFamily>();
        string familiesPath = Member(RootPath, "families");
        int familyIndex = 0;
        foreach (JsonElement element in Array(root, RootPath, "families"))
        {
            string familyPath = Element(familiesPath, familyIndex++);
            long familyId = Long(element, familyPath, "familyId");
            int blockSize = Int(element, familyPath, "blockSize");
            string blocksPath = Member(familyPath, "blocks");
            int blockIndex = 0;
            var blocks = new List<ContentFamilyBlock>();
            foreach (JsonElement block in Array(element, familyPath, "blocks"))
            {
                string blockPath = Element(blocksPath, blockIndex++);
                blocks.Add(new ContentFamilyBlock(
                    familyId,
                    Int(block, blockPath, "ordinal"),
                    Int(block, blockPath, "baseId"),
                    blockSize,
                    Int(block, blockPath, "nextFreeId"),
                    Int(block, blockPath, "reservedInVersion")));
            }

            // Every member is read first, in the order the constructor call always read them, so only the
            // constructor's own rules reach the catch. ContentFamilyBlock is a record struct with no checks, so
            // building the blocks above cannot refuse.
            ContentTypeId type = TypeId(element, familyPath, "typeId");
            string familyKey = Text(element, familyPath, "familyKey");
            bool retired = Bool(element, familyPath, "retired");
            int createdInVersion = Int(element, familyPath, "createdInVersion");
            try
            {
                families.Add(new ContentFamily(familyId, type, familyKey, blockSize, retired, createdInVersion, blocks));
            }
            catch (ArgumentException malformed)
            {
                throw ContentBundleConstructionRefusals.Family(
                    ContentFamily.FindDefect(familyKey, blockSize, blocks), malformed, familyPath, familyKey, blocks);
            }
        }

        return families;
    }

    static IReadOnlyList<ContentBundleRow> ReadRows(JsonElement root)
    {
        var rows = new List<ContentBundleRow>();
        string rowsPath = Member(RootPath, "rows");
        int rowIndex = 0;
        foreach (JsonElement element in Array(root, RootPath, "rows"))
        {
            string rowPath = Element(rowsPath, rowIndex++);
            string fieldsPath = Member(rowPath, "fields");
            int fieldIndex = 0;
            var fields = new List<ContentFieldEdit>();
            foreach (JsonElement field in Array(element, rowPath, "fields"))
            {
                string fieldPath = Element(fieldsPath, fieldIndex++);
                ContentFieldKind kind = FieldKind(field, fieldPath, "kind");
                fields.Add(new ContentFieldEdit(Text(field, fieldPath, "name"), Value(field, fieldPath, kind)));
            }

            rows.Add(new ContentBundleRow(
                TypeId(element, rowPath, "typeId"),
                OptionalInt(element, rowPath, "id"),
                new ContentKey(Text(element, rowPath, "key")),
                Bool(element, rowPath, "retired"),
                OptionalText(element, "familyKey"),
                fields));
        }

        return rows;
    }

    static IReadOnlyList<RemapRule> ReadRules(JsonElement root)
    {
        var rules = new List<RemapRule>();
        string rulesPath = Member(RootPath, "rules");
        int ruleIndex = 0;
        foreach (JsonElement element in Array(root, RootPath, "rules"))
        {
            string rulePath = Element(rulesPath, ruleIndex++);
            // Every member is read first, in the order the constructor call always read them, so only the
            // constructor's own rules reach the catch.
            int sequence = Int(element, rulePath, "sequence");
            int introducedIn = Int(element, rulePath, "introducedIn");
            ContentTypeId type = TypeId(element, rulePath, "typeId");
            RemapRuleKind kind = RuleKind(element, rulePath, "kind");
            int fromId = Int(element, rulePath, "fromId");
            int toId = Int(element, rulePath, "toId");
            byte[] payload = Bytes(element, rulePath, "payload");
            try
            {
                rules.Add(new RemapRule(sequence, introducedIn, type, kind, fromId, toId, payload));
            }
            catch (ArgumentException malformed)
            {
                throw ContentBundleConstructionRefusals.Rule(malformed, rulePath, kind, payload);
            }
        }

        return rules;
    }

    static ContentFieldValue Value(JsonElement field, string fieldPath, ContentFieldKind kind)
    {
        if (field.TryGetProperty("absent", out JsonElement absent) && absent.ValueKind == JsonValueKind.True)
        {
            return ContentFieldValue.Absent(kind);
        }

        if (ContentFieldValue.StoresNumber(kind))
        {
            return ContentFieldValue.OfNumber(kind, Long(field, fieldPath, "number"));
        }

        byte[] bytes = Bytes(field, fieldPath, "bytes");
        try
        {
            return ContentFieldValue.OfBytes(kind, bytes);
        }
        catch (ArgumentException malformed)
        {
            // OfBytes has one rule, that the kind stores bytes, and the one defined kind that stores neither a
            // number nor bytes is the byte-free text marker.
            throw ContentBundleConstructionRefusals.MarkerValue(malformed, fieldPath);
        }
    }

    static ContentFieldSchema Schema(IReadOnlyList<ContentFieldEntry> fields, string typePath)
    {
        try
        {
            return new ContentFieldSchema(fields);
        }
        catch (ArgumentException malformed)
        {
            // No baseline is declared, so the schema treats every field as a baseline field.
            throw ContentBundleConstructionRefusals.Schema(ContentFieldSchema.FindDefect(fields, fields.Count), malformed, typePath);
        }
    }

    internal static JsonElement.ArrayEnumerator Array(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        if (!present || element.ValueKind != JsonValueKind.Array)
        {
            throw Shape(present, element, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle carries a '{name}' array, and this one does not."));
        }

        return element.EnumerateArray();
    }

    static string Text(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        return present && element.ValueKind == JsonValueKind.String
            ? element.GetString()!
            : throw Shape(present, element, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a string, and this one is not."));
    }

    static string? OptionalText(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    static int Int(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        if (!present || element.ValueKind != JsonValueKind.Number)
        {
            throw Shape(present, element, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a number, and this one is not."));
        }

        return element.TryGetInt32(out int value)
            ? value
            : throw Refuse(ContentAuthoringRefusal.BundleInt32Unrepresentable, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a 32-bit integer, and this one is not."));
    }

    internal static ContentTypeId TypeId(JsonElement parent, string parentPath, string name)
    {
        int value = Int(parent, parentPath, name);
        return value is >= 1 and <= ushort.MaxValue
            ? new ContentTypeId((ushort)value)
            : throw new ContentAuthoringException(
                ContentAuthoringRefusal.BundleTypeIdOutsideRange,
                FormattableString.Invariant(
                    $"A bundle member '{name}' is a content type id from 1 to {ushort.MaxValue}, and this one is not."),
                default,
                0,
                ContentAuthoringException.BundleFormatReason)
            {
                JsonPath = Member(parentPath, name),
                ActualInteger = value,
                MinimumInteger = 1,
                MaximumInteger = ushort.MaxValue,
            };
    }

    static ContentFieldKind FieldKind(JsonElement parent, string parentPath, string name)
    {
        int value = Int(parent, parentPath, name);
        return value is >= (int)ContentFieldKind.Int and <= (int)ContentFieldKind.OpaqueBytes
            ? (ContentFieldKind)value
            : throw Unknown(ContentAuthoringRefusal.BundleFieldKindUnknown, Member(parentPath, name), value, FormattableString.Invariant(
                $"A bundle member '{name}' is a known content field kind, and this one is not."));
    }

    static ContentVisibility Visibility(JsonElement parent, string parentPath, string name)
    {
        int value = Int(parent, parentPath, name);
        return value is >= (int)ContentVisibility.Client and <= (int)ContentVisibility.ServerOnly
            ? (ContentVisibility)value
            : throw Unknown(ContentAuthoringRefusal.BundleVisibilityUnknown, Member(parentPath, name), value, FormattableString.Invariant(
                $"A bundle member '{name}' is a known content visibility, and this one is not."));
    }

    static RemapRuleKind RuleKind(JsonElement parent, string parentPath, string name)
    {
        int value = Int(parent, parentPath, name);
        return value is >= (int)RemapRuleKind.ReplacedBy and <= (int)RemapRuleKind.StackCapLowered
            ? (RemapRuleKind)value
            : throw Unknown(ContentAuthoringRefusal.BundleRemapKindUnknown, Member(parentPath, name), value, FormattableString.Invariant(
                $"A bundle member '{name}' is a known remap rule kind, and this one is not."));
    }

    // A JSON number is a decimal literal of ANY magnitude, so Number-kinded is not yet integer-valued: 1.5,
    // an id of 9,999,999,999 against an int, and 1e308 against a long are all Number and none of them
    // converts. Try rather than Get, because a Get on a miss is a FormatException, which is neither this
    // reader's declared surface nor something a caller can tell apart from a fault. An author's typo is a
    // refusal naming the member, the same as every other shape defect in this file.
    static int? OptionalInt(JsonElement parent, string parentPath, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return element.TryGetInt32(out int value)
            ? value
            : throw Refuse(ContentAuthoringRefusal.BundleInt32Unrepresentable, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a 32-bit integer, and this one is not."));
    }

    static long Long(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        if (!present || element.ValueKind != JsonValueKind.Number)
        {
            throw Shape(present, element, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a number, and this one is not."));
        }

        return element.TryGetInt64(out long value)
            ? value
            : throw Refuse(ContentAuthoringRefusal.BundleInt64Unrepresentable, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a 64-bit integer, and this one is not."));
    }

    static bool Bool(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        return present && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : throw Shape(present, element, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a boolean, and this one is not."));
    }

    static byte[] Bytes(JsonElement parent, string parentPath, string name)
    {
        string hex = Text(parent, parentPath, name);
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            throw Refuse(ContentAuthoringRefusal.BundleHexMalformed, Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is lower hex, and '{hex}' is not."));
        }
    }

    /// <summary>The path of a document member, under its parent's rooted path.</summary>
    internal static string Member(string parentPath, string name) => parentPath + "." + name;

    /// <summary>The path of an array element, under the array's rooted path.</summary>
    internal static string Element(string arrayPath, int index)
        => FormattableString.Invariant($"{arrayPath}[{index}]");

    // A member-shape guard admits one present JSON kind. Its refusal names the branch that failed, in the
    // predicate's own order: an absent member first, then a present member of another kind. The caller
    // passes what its TryGetProperty already read, so the reason and kind describe the value it compared.
    internal static ContentAuthoringException Shape(bool present, JsonElement element, string path, string message)
        => present
            ? new ContentAuthoringException(
                ContentAuthoringRefusal.BundleMemberKindInvalid,
                message,
                default,
                0,
                ContentAuthoringException.BundleFormatReason)
            {
                JsonPath = path,
                ActualJsonKind = element.ValueKind,
            }
            : Refuse(ContentAuthoringRefusal.BundleMemberRequired, path, message);

    static ContentAuthoringException Unknown(ContentAuthoringRefusal refusal, string path, int value, string message)
        => new(refusal, message, default, 0, ContentAuthoringException.BundleFormatReason)
        {
            JsonPath = path,
            ActualInteger = value,
        };

    /// <summary>A document refusal naming the rule and the rooted path of the member it is about.</summary>
    internal static ContentAuthoringException Refuse(ContentAuthoringRefusal refusal, string jsonPath, string message)
        => new(refusal, message, default, 0, ContentAuthoringException.BundleFormatReason) { JsonPath = jsonPath };

}
