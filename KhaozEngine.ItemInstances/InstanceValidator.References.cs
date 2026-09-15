using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The DERIVED half of the sweep: checks 6, 7 and 8, and check 13's socket clause, walked off the
/// registered <see cref="InstanceReferenceTarget"/> descriptors rather than off a list of kinds.
/// <para>
/// An earlier draft of spec 12.2 wrote check 7 as a closed enumeration and it already omitted kind 7's
/// material ids and a socket's contained definition id, and it would have omitted every game kind at or
/// above 1,024 forever. What ships instead is one walk over the shapes, in the same recursive order the
/// remap pass will take, so a kind cannot be remapped-but-not-validated or validated-but-not-remapped.
/// </para>
/// </summary>
public static partial class InstanceValidator
{
    /// <summary>
    /// Slot values held per level without touching the heap. Every v1 shape has at most four slots, and a
    /// registration declaring more than this gets a per-field array rather than a bigger stack frame.
    /// </summary>
    const int SlotBuffer = 16;

    /// <summary>
    /// Walks every field of one payload. An UNREGISTERED kind is skipped whole: contracts 9.4 keeps its
    /// bytes verbatim, so nothing reads a reference out of them and nothing about them can quarantine.
    /// </summary>
    static bool WalkFields(
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<PayloadField> fields,
        in WalkContext context,
        bool nestedLevel,
        ref bool retired,
        out int check,
        out string? reason)
    {
        check = 0;
        reason = null;
        foreach (PayloadField field in fields)
        {
            if (!context.Properties.TryGet(field.Kind, out InstancePropertyRegistration? registration))
            {
                continue;
            }

            bool tiered = IsTiered(field.Kind);
            if (registration.References.IsEmpty && !tiered && !Nests(registration.Shape))
            {
                continue;
            }

            ReadOnlySpan<byte> body = payload.Slice(field.BodyStart, field.BodyLength);
            if (!WalkField(body, registration, tiered, context, nestedLevel, ref retired, out check, out reason))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Walks ONE field's body against its registered shape, reading out the slots its reference targets
    /// name and recursing into any nested payload it carries.
    /// </summary>
    static bool WalkField(
        ReadOnlySpan<byte> body,
        InstancePropertyRegistration registration,
        bool tiered,
        in WalkContext context,
        bool nestedLevel,
        ref bool retired,
        out int check,
        out string? reason)
    {
        InstanceFieldShape shape = registration.Shape;

        // A field that CARRIES a nested payload is a field whose item references are CONTAINED items, which
        // is what spec 12.2 rows 6 and 13 mean by "every socket's ContainedDefinitionId". Reading it off the
        // shape rather than off kind 132 is what keeps the rule true for a game kind that nests too.
        bool contains = Nests(shape);
        int slots = Math.Max(shape.Header.Length, shape.Entry.Length);
        Span<ulong> values = context.Values.Length >= slots ? context.Values : new ulong[slots];

        int offset = 0;
        if (!ReadRun(shape.Header.Span, body, ref offset, values, context, nestedLevel, ref retired, out check, out reason)
            || !CheckTargets(registration, InstanceReferenceSite.Header, values, contains, context.Content, ref retired, out check, out reason))
        {
            return false;
        }

        if (shape.Count == InstanceCountWidth.None)
        {
            return true;
        }

        if (!ReadCount(shape.Count, body, ref offset, out uint count))
        {
            check = 3;
            reason = InstancePayloadReason.FieldMalformed;
            return false;
        }

        ReadOnlySpan<InstanceSlotKind> entry = shape.Entry.Span;
        for (uint repeat = 0; repeat < count; repeat++)
        {
            if (!ReadRun(entry, body, ref offset, values, context, nestedLevel, ref retired, out check, out reason)
                || !CheckTargets(registration, InstanceReferenceSite.Entry, values, contains, context.Content, ref retired, out check, out reason))
            {
                return false;
            }

            // Check 8, the ONE hand written check, because a tier ordinal is not a content id: it is a key
            // INTO the mod row check 7 has already resolved, which is the one relationship a reference
            // target cannot express.
            if (tiered && !CheckTier(entry, values, context.Content, out check, out reason))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the content ids one run of slots holds. An id of 0 is SKIPPED: contracts 5.1 never assigns
    /// 0, so it is how a payload spells absent, which is an empty socket or an unrestricted socket type.
    /// </summary>
    static bool CheckTargets(
        InstancePropertyRegistration registration,
        InstanceReferenceSite site,
        ReadOnlySpan<ulong> values,
        bool contains,
        ContentReferences content,
        ref bool retired,
        out int check,
        out string? reason)
    {
        check = 0;
        reason = null;
        foreach (InstanceReferenceTarget target in registration.References.Span)
        {
            if (target.Site != site || target.SlotIndex >= values.Length)
            {
                continue;
            }

            ulong raw = values[target.SlotIndex];
            if (raw == 0)
            {
                continue;
            }

            bool definition = contains
                && string.Equals(target.ContentTypeKey, EngineContentTypes.ItemTypeKey, StringComparison.Ordinal);
            int id = raw > int.MaxValue ? 0 : (int)raw;

            if (definition)
            {
                if (id == 0 || !content.ResolvesItem(id))
                {
                    check = 6;
                    reason = InstanceQuarantineReason.UnknownDefinition;
                    return false;
                }

                retired |= content.IsItemRetired(id);
                continue;
            }

            if (id == 0 || !content.Resolves(target.ContentTypeKey, id))
            {
                check = 7;
                reason = InstanceQuarantineReason.UnknownContentReference;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Check 8: the <c>(mod id, tier ordinal)</c> pair of one affix entry names a LIVE tier. The mod id is
    /// the entry's <c>mod</c> reference and the ordinal is the byte slot beside it, spec 3.4's layout.
    /// </summary>
    static bool CheckTier(
        ReadOnlySpan<InstanceSlotKind> entry,
        ReadOnlySpan<ulong> values,
        ContentReferences content,
        out int check,
        out string? reason)
    {
        check = 0;
        reason = null;
        if (entry.Length < 2 || values.Length < 2)
        {
            return true;
        }

        if (content.TierIsLive((int)values[0], (int)values[1]))
        {
            return true;
        }

        check = 8;
        reason = InstanceQuarantineReason.UnknownContentReference;
        return false;
    }

    /// <summary>
    /// Reads one run of slots into <paramref name="values"/>. The bytes already decoded against these same
    /// shapes, so every refusal here is defensive: it answers <c>field-malformed</c> rather than throwing.
    /// </summary>
    static bool ReadRun(
        ReadOnlySpan<InstanceSlotKind> slots,
        ReadOnlySpan<byte> body,
        ref int offset,
        Span<ulong> values,
        in WalkContext context,
        bool nestedLevel,
        ref bool retired,
        out int check,
        out string? reason)
    {
        check = 0;
        reason = null;
        for (int index = 0; index < slots.Length; index++)
        {
            switch (slots[index])
            {
                case InstanceSlotKind.Varint:
                    if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong value, out _))
                    {
                        return Malformed(out check, out reason);
                    }

                    values[index] = value;
                    break;

                case InstanceSlotKind.Byte:
                    if (offset >= body.Length)
                    {
                        return Malformed(out check, out reason);
                    }

                    values[index] = body[offset++];
                    break;

                case InstanceSlotKind.Fixed2:
                    if (body.Length - offset < 2)
                    {
                        return Malformed(out check, out reason);
                    }

                    values[index] = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
                    offset += 2;
                    break;

                case InstanceSlotKind.NestedPayload:
                    if (!ContentVarint.TryRead(body, ref offset, out uint length, out _)
                        || length > (uint)(body.Length - offset))
                    {
                        return Malformed(out check, out reason);
                    }

                    values[index] = length;
                    if (!Nested(body.Slice(offset, (int)length), context, nestedLevel, ref retired, out check, out reason))
                    {
                        return false;
                    }

                    offset += (int)length;
                    break;

                default:
                    return Malformed(out check, out reason);
            }
        }

        return true;
    }

    /// <summary>
    /// Walks a nested payload, which is a payload in this same format ONE level down. A nested payload
    /// carrying a field that itself nests is check 4, refused here as well as in the decoder so the walk
    /// cannot recurse without bound whatever it is handed.
    /// </summary>
    static bool Nested(
        ReadOnlySpan<byte> bytes,
        in WalkContext context,
        bool nestedLevel,
        ref bool retired,
        out int check,
        out string? reason)
    {
        if (nestedLevel)
        {
            check = 4;
            reason = InstancePayloadReason.SocketNesting;
            return false;
        }

        if (!ItemInstancePayload.TryDecode(
            context.Properties, bytes, context.NestedFields, out int count, out string? token))
        {
            check = CheckForToken(token);
            reason = token;
            return false;
        }

        var inner = new WalkContext(context.Properties, context.Content, context.NestedValues, default, default);
        return WalkFields(bytes, context.NestedFields[..count], inner, nestedLevel: true, ref retired, out check, out reason);
    }

    static bool ReadCount(InstanceCountWidth width, ReadOnlySpan<byte> body, ref int offset, out uint count)
    {
        switch (width)
        {
            case InstanceCountWidth.Byte:
                if (offset >= body.Length)
                {
                    count = 0;
                    return false;
                }

                count = body[offset++];
                return true;

            case InstanceCountWidth.Varint:
                return ContentVarint.TryRead(body, ref offset, out count, out _);

            default:
                count = 0;
                return false;
        }
    }

    static bool Malformed(out int check, out string? reason)
    {
        check = 3;
        reason = InstancePayloadReason.FieldMalformed;
        return false;
    }

    /// <summary>Whether a shape carries a nested payload anywhere, in its header or in one repeat.</summary>
    static bool Nests(in InstanceFieldShape shape) => Nests(shape.Header.Span) || Nests(shape.Entry.Span);

    static bool Nests(ReadOnlySpan<InstanceSlotKind> slots)
    {
        foreach (InstanceSlotKind slot in slots)
        {
            if (slot == InstanceSlotKind.NestedPayload)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The two kinds whose entries carry a tier ordinal, spec 3.4's one affix entry layout shared by kind
    /// 131 and kind 133.
    /// </summary>
    static bool IsTiered(ushort kind)
        => kind is InstancePropertyKind.Affixes or InstancePropertyKind.Enchantments;

    /// <summary>
    /// The buffers and registries one level of the walk carries, so the walk allocates nothing per field
    /// and no <c>stackalloc</c> ever sits inside a loop.
    /// </summary>
    readonly ref struct WalkContext
    {
        public WalkContext(
            InstancePropertyRegistry properties,
            ContentReferences content,
            Span<ulong> values,
            Span<PayloadField> nestedFields,
            Span<ulong> nestedValues)
        {
            Properties = properties;
            Content = content;
            Values = values;
            NestedFields = nestedFields;
            NestedValues = nestedValues;
        }

        public InstancePropertyRegistry Properties { get; }

        public ContentReferences Content { get; }

        /// <summary>This level's slot values.</summary>
        public Span<ulong> Values { get; }

        /// <summary>The field buffer one level down, which only a nesting field ever uses.</summary>
        public Span<PayloadField> NestedFields { get; }

        /// <summary>The slot values one level down.</summary>
        public Span<ulong> NestedValues { get; }
    }
}

/// <summary>
/// The snapshot lookups the sweep makes, with the per-sweep indexes they need. Held per call, so nothing
/// here is ambient and a second sweep against a newer snapshot shares nothing with the first.
/// </summary>
sealed class ContentReferences
{
    /// <summary>
    /// The content type key a mod's tiers are rows of (spec 8.3). Scope B registers the type, and until it
    /// does, an item carrying an affix quarantines rather than skipping check 8: a tier the active content
    /// cannot name did not resolve, which is exactly what contracts 10.1 calls Quarantined.
    /// </summary>
    public const string ModTierTypeKey = "mod_tier";

    /// <summary>The <c>mod_tier</c> field pointing back at the mod, spec 8.3.</summary>
    public const string ModTierModIdField = "mod_id";

    /// <summary>The <c>mod_tier</c> field holding the authored ordinal a payload stores, spec 8.3.</summary>
    public const string ModTierOrdinalField = "ordinal";

    readonly ContentTypeRegistry _types;
    readonly IContentSnapshot _snapshot;
    readonly Dictionary<string, ContentTypeRegistration?> _byKey = new(StringComparer.Ordinal);
    HashSet<long>? _tiers;
    int _stackCapField = Unread;

    const int Unread = -2;
    const int NoField = -1;

    public ContentReferences(ContentTypeRegistry types, IContentSnapshot snapshot)
    {
        _types = types;
        _snapshot = snapshot;
    }

    /// <summary>Whether an <c>item</c> row with that id exists in the active version, retired or not.</summary>
    public bool ResolvesItem(int id) => Resolves(EngineContentTypes.ItemTypeKey, id);

    /// <summary>Whether that <c>item</c> row is RETIRED, which is check 13's whole question.</summary>
    public bool IsItemRetired(int id)
    {
        ContentTypeRegistration? item = Type(EngineContentTypes.ItemTypeKey);
        return item is not null && _snapshot.IsRetired(item.Type, id);
    }

    /// <summary>
    /// Whether a row of that content type resolves. A type the active pack does not REGISTER fails closed:
    /// a reference the content cannot explain did not resolve, and skipping it would leave an item carrying
    /// it fully usable.
    /// </summary>
    public bool Resolves(string typeKey, int id)
    {
        ContentTypeRegistration? registration = Type(typeKey);
        return registration is not null && id > 0 && _snapshot.TryGetRow(registration.Type, id, out _);
    }

    /// <summary>
    /// Whether a <c>(mod id, tier ordinal)</c> pair names a tier that is present AND not retired. The index
    /// is built once per sweep, on the first affix, because a linear scan per affix over a published mod
    /// table is the one part of this walk that would not stay inside spec 16's budget.
    /// </summary>
    public bool TierIsLive(int modId, int ordinal)
        => modId > 0 && ordinal > 0 && Tiers().Contains(TierKey(modId, ordinal));

    /// <summary>
    /// Check 12: the count is above the definition's cap AND no lowered-cap rule explains it. Contracts 8.2
    /// kind 4 declares an existing over-cap stack legal, so a rule naming this definition makes the state
    /// one the contract asked for rather than a finding.
    /// </summary>
    public bool IsOverCap(int definitionId, int count)
    {
        if (!TryGetStackCap(definitionId, out int cap) || count <= cap)
        {
            return false;
        }

        ContentTypeRegistration? item = Type(EngineContentTypes.ItemTypeKey);
        if (item is null)
        {
            return true;
        }

        foreach (RemapRule rule in _snapshot.Rules)
        {
            if (rule.Kind == RemapRuleKind.StackCapLowered
                && rule.Type == item.Type
                && rule.FromId == definitionId)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The definition's stack cap, read generically through the schema rather than through the typed item
    /// view: <see cref="IContentSnapshot"/> is the seam every consumer is written against, and its typed
    /// sibling answers only for a snapshot whose rows arrived with their encoded bodies.
    /// </summary>
    bool TryGetStackCap(int definitionId, out int cap)
    {
        cap = 0;
        ContentTypeRegistration? item = Type(EngineContentTypes.ItemTypeKey);
        if (item is null || !_snapshot.TryGetRow(item.Type, definitionId, out ContentRow? row))
        {
            return false;
        }

        if (_stackCapField == Unread)
        {
            _stackCapField = NoField;
            for (int index = 0; index < item.Schema.Fields.Count; index++)
            {
                if (string.Equals(item.Schema.Fields[index].Name, ItemContentType.MaxStackField, StringComparison.Ordinal))
                {
                    _stackCapField = index;
                    break;
                }
            }
        }

        if (_stackCapField < 0 || _stackCapField >= row.Fields.Count)
        {
            return false;
        }

        ContentFieldValue value = row.Fields[_stackCapField];

        // A cap of 0 is a row that declares none, which is not a cap of nothing: an item with no declared
        // maximum is not over any.
        if (value.IsAbsent || value.Number <= 0 || value.Number > int.MaxValue)
        {
            return false;
        }

        cap = (int)value.Number;
        return true;
    }

    HashSet<long> Tiers()
    {
        if (_tiers is not null)
        {
            return _tiers;
        }

        _tiers = new HashSet<long>();
        ContentTypeRegistration? tier = Type(ModTierTypeKey);
        if (tier is null)
        {
            return _tiers;
        }

        int modIdField = NoField;
        int ordinalField = NoField;
        for (int index = 0; index < tier.Schema.Fields.Count; index++)
        {
            string name = tier.Schema.Fields[index].Name;
            if (string.Equals(name, ModTierModIdField, StringComparison.Ordinal))
            {
                modIdField = index;
            }
            else if (string.Equals(name, ModTierOrdinalField, StringComparison.Ordinal))
            {
                ordinalField = index;
            }
        }

        if (modIdField < 0 || ordinalField < 0)
        {
            return _tiers;
        }

        foreach (ContentRow row in _snapshot.Rows(tier.Type))
        {
            if (row.IsRetired || modIdField >= row.Fields.Count || ordinalField >= row.Fields.Count)
            {
                continue;
            }

            ContentFieldValue mod = row.Fields[modIdField];
            ContentFieldValue ordinal = row.Fields[ordinalField];
            if (mod.IsAbsent || ordinal.IsAbsent)
            {
                continue;
            }

            _tiers.Add(TierKey((int)mod.Number, (int)ordinal.Number));
        }

        return _tiers;
    }

    ContentTypeRegistration? Type(string typeKey)
    {
        if (_byKey.TryGetValue(typeKey, out ContentTypeRegistration? held))
        {
            return held;
        }

        _ = _types.TryGetByKey(typeKey, out ContentTypeRegistration? registration);
        _byKey.Add(typeKey, registration);
        return registration;
    }

    static long TierKey(int modId, int ordinal) => ((long)modId << 32) | (uint)ordinal;
}
