using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// One source's slice of the three spans a <see cref="InstanceStatLines.Build"/> filled, which is what a
/// caller hands to <c>ContentStatEvaluator.AddSource</c> as a unit.
/// <para>
/// The lines and the tags travel TOGETHER because the evaluator takes them together: a source with eight
/// lines hands over one tag span and eight structs, and each line's <c>TagScopeStart</c> is relative to
/// that span rather than to the whole build. Splitting them would mean a caller rebasing scopes by hand,
/// which is the arithmetic this record exists to carry.
/// </para>
/// </summary>
/// <param name="Key">The source key, spec 11.4, whose ordinal is <see cref="InstanceStatSourceKind"/>'s.</param>
/// <param name="LineStart">Where this source's lines start in the destination span.</param>
/// <param name="LineCount">How many lines it holds, in modifier index order.</param>
/// <param name="TagStart">Where its tag run starts in the tag span.</param>
/// <param name="TagCount">How many tags that run holds, zero for a source no line of which is scoped.</param>
public readonly record struct InstanceStatSource(
    StatSourceKey Key,
    int LineStart,
    int LineCount,
    int TagStart,
    int TagCount);

/// <summary>
/// Turns one item's payload plus one content version into the <see cref="StatModifierLine"/>s
/// <see cref="ContentStatEvaluator"/> folds. Spec 11 gives the evaluator, the value types and the source
/// order and never says who produces the lines, and this is that missing half.
/// <para>
/// <b>It walks kind 131, kind 133 and kind 132</b> (spec 3.4 and 3.5). Each affix and each enchantment
/// entry resolves its <c>(mod id, tier ordinal)</c> pair to a <c>mod_tier</c> row, reads that tier's
/// <c>stat_line</c> rows in <c>sort</c> order, and emits one line per row. Each socket carrying a nested
/// payload recurses ONE level and emits the contained item's lines as a single source at
/// <see cref="StatSourceKey.SocketedItemKind"/>, which is contracts 9.5's nesting limit honoured by
/// stopping rather than by checking.
/// </para>
/// <para>
/// <b>ONE stored roll position drives EVERY line on the tier</b> (spec 8.4). A two line tier resolves both
/// from the same <c>ushort</c>, so the two move together and the payload stores one position per affix
/// rather than one per line. The value is contracts 6.4's formula through
/// <see cref="RollPosition.Resolve"/>, which is the ONE copy of it in the tree, and there is no float
/// anywhere on this path. <c>stat_line.sort</c> is what makes "the tier's second line" a stable phrase
/// across a republish, so a line's position in a source follows <c>sort</c> and never the row id.
/// </para>
/// <para>
/// <b>Kind 1 is not produced here.</b> Spec 11.4's first row is the base and implicit lines of the worn
/// item, and those come from the item DEFINITION rather than from the payload, which carries no field for
/// them. <see cref="InstanceStatSourceKind.WornItemOrdinal"/> states that kind's ordinal so a caller
/// building base lines from its own content uses the same rule, and this builder emits none.
/// </para>
/// <para>
/// <b>A GATED kind is emitted, because this is NOT a visibility boundary.</b> Kinds 129, 131, 133 and 134
/// are identification gated (spec 12.7), and an unidentified item still folds every one of its affix lines
/// here, because the evaluator runs SERVER side over the TRUE payload and a hidden line would make the
/// item weaker rather than mysterious. A tooltip that wants the player's view asks
/// <see cref="ItemInstanceVisibility.PublicView"/> first and builds from the projection, which is the same
/// one function the replication filter uses. Two answers to "what does this item give me" is exactly what
/// spec 12.5 exists to prevent, so the rule lives there and is never restated here.
/// </para>
/// <para>
/// <b>Build allocates NOTHING.</b> It writes into caller spans and every array is built once at
/// construction, per snapshot and immutable, so the read path binary searches an index rather than
/// scanning rows. Refusals ANSWER rather than throw, because a payload arrives from a stored page or a
/// remote peer, and a refusal reports zero sources rather than a partial build.
/// </para>
/// </summary>
public sealed class InstanceStatLines
{
    /// <summary>What <see cref="Build"/> returns when it wrote nothing at all.</summary>
    public const int Refused = -1;

    readonly long[] _tierKeys;
    readonly int[] _tierLineStart;
    readonly int[] _tierLineCount;

    readonly int[] _lineStatId;
    readonly StatCombineKind[] _lineCombine;
    readonly int[] _lineMinimum;
    readonly int[] _lineMaximum;
    readonly int[] _lineConditionId;
    readonly int[] _lineTagStart;
    readonly int[] _lineTagCount;
    readonly int[] _tagIds;

    /// <summary>
    /// Indexes one content version's <c>mod_tier</c> and <c>stat_line</c> rows, once, so a build never
    /// scans a row. A RETIRED row is skipped on both types: a stored affix naming one contributes no line,
    /// which is the same answer a tier this version never carried gets, and neither is a refusal.
    /// </summary>
    /// <param name="snapshot">The content version this builder resolves tiers against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public InstanceStatLines(IContentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var slotOf = new Dictionary<int, int>();
        (_tierKeys, _tierLineStart, _tierLineCount) = ReadTiers(snapshot, slotOf);

        var rows = new List<LineRow>();
        var tags = new List<int>();
        var scope = new List<int>();
        foreach (ContentRow row in snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.StatLineTypeId)))
        {
            if (row.IsRetired || !TryReadLine(row, slotOf, scope, out LineRow line))
            {
                continue;
            }

            line = line with { TagStart = tags.Count, TagCount = scope.Count };
            tags.AddRange(scope);
            rows.Add(line);
        }

        // The ORDER inside a tier is sort then row id, which is what makes "the tier's second line" a
        // stable phrase: a republish that re-ids a line keeps its sort and so keeps its position.
        rows.Sort(static (left, right) =>
        {
            int byTier = left.TierSlot.CompareTo(right.TierSlot);
            if (byTier != 0)
            {
                return byTier;
            }

            int bySort = left.Sort.CompareTo(right.Sort);
            return bySort != 0 ? bySort : left.RowId.CompareTo(right.RowId);
        });

        _tagIds = [.. tags];
        _lineStatId = new int[rows.Count];
        _lineCombine = new StatCombineKind[rows.Count];
        _lineMinimum = new int[rows.Count];
        _lineMaximum = new int[rows.Count];
        _lineConditionId = new int[rows.Count];
        _lineTagStart = new int[rows.Count];
        _lineTagCount = new int[rows.Count];

        int widestLines = 0;
        int widestTags = 0;
        for (int index = 0; index < rows.Count; index++)
        {
            LineRow line = rows[index];
            _lineStatId[index] = line.StatId;
            _lineCombine[index] = line.Combine;
            _lineMinimum[index] = line.Minimum;
            _lineMaximum[index] = line.Maximum;
            _lineConditionId[index] = line.ConditionId;
            _lineTagStart[index] = line.TagStart;
            _lineTagCount[index] = line.TagCount;

            if (_tierLineCount[line.TierSlot] == 0)
            {
                _tierLineStart[line.TierSlot] = index;
            }

            _tierLineCount[line.TierSlot]++;
        }

        for (int slot = 0; slot < _tierKeys.Length; slot++)
        {
            int count = _tierLineCount[slot];
            widestLines = Math.Max(widestLines, count);
            int width = 0;
            for (int index = 0; index < count; index++)
            {
                width += _lineTagCount[_tierLineStart[slot] + index];
            }

            widestTags = Math.Max(widestTags, width);
        }

        MaxTierLineCount = widestLines;
        MaxTierTagCount = widestTags;
    }

    /// <summary>
    /// The most lines any one tier of this version grants, which is how wide an AFFIX source can get and
    /// what a caller sizes its destination span from.
    /// </summary>
    public int MaxTierLineCount { get; }

    /// <summary>
    /// The most scope tags any one tier's lines ask for between them, which sizes the tag span the same
    /// way. A SOCKET source is the contained item's whole affix list, so its bound is this times that
    /// list's length.
    /// </summary>
    public int MaxTierTagCount { get; }

    /// <summary>
    /// Builds one worn item's sources into the caller's spans, allocating nothing.
    /// <para>
    /// Every emitted source is ready for <c>ContentStatEvaluator.AddSource</c> as
    /// <c>AddSource(source.Key, destination.Slice(source.LineStart, source.LineCount),
    /// tagScopes.Slice(source.TagStart, source.TagCount))</c>, which is the whole reason the lines and the
    /// tags come back together.
    /// </para>
    /// </summary>
    /// <param name="payload">The item's payload bytes, which may be empty.</param>
    /// <param name="wornSlot">The worn slot index, 0 to <see cref="InstanceStatSourceKind.MaxWornSlot"/>.</param>
    /// <param name="instanceId">The worn item's instance id, which rides into the key of its own sources.</param>
    /// <param name="destination">Where the lines go, in modifier index order within each source.</param>
    /// <param name="tagScopes">Where each source's tag run goes.</param>
    /// <param name="sources">Where the source records go.</param>
    /// <param name="tagCount">How many tags were written, 0 on a refusal.</param>
    /// <param name="sourceCount">How many sources were written, 0 on a refusal.</param>
    /// <returns>
    /// The lines written, or <see cref="Refused"/> when the payload does not decode, a field body does not
    /// match its kind's layout, the worn slot is outside the packing, or one of the three spans is too
    /// short. A refusal never throws, because the bytes come from a stored page or a remote peer.
    /// </returns>
    public int Build(
        ReadOnlySpan<byte> payload,
        int wornSlot,
        long instanceId,
        Span<StatModifierLine> destination,
        Span<int> tagScopes,
        Span<InstanceStatSource> sources,
        out int tagCount,
        out int sourceCount)
    {
        tagCount = 0;
        sourceCount = 0;
        if (wornSlot < 0 || wornSlot > InstanceStatSourceKind.MaxWornSlot)
        {
            return Refused;
        }

        // The STRUCTURAL decode, which checks contracts 9.3's three canonical rules, the cap and every
        // declared length without reading a body. The per kind bodies below are read here rather than by a
        // registry bound decode, because this path owns the three layouts it walks and a second full
        // validation on every equip would buy nothing the validator has not already answered.
        Span<PayloadField> fields = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        if (!ItemInstancePayload.TryDecode(payload, fields, out int fieldCount, out _))
        {
            return Refused;
        }

        Span<PayloadField> nested = stackalloc PayloadField[ItemInstancePayload.MaxFields];
        var writer = new Writer(destination, tagScopes, sources);
        for (int index = 0; index < fieldCount; index++)
        {
            PayloadField field = fields[index];
            ReadOnlySpan<byte> body = payload.Slice(field.BodyStart, field.BodyLength);
            bool walked = field.Kind switch
            {
                InstancePropertyKind.Affixes
                    => Entries(body, StatSourceKey.AffixKind, wornSlot, instanceId, ref writer),
                InstancePropertyKind.Enchantments
                    => Entries(body, StatSourceKey.EnchantmentKind, wornSlot, instanceId, ref writer),
                InstancePropertyKind.Sockets => Sockets(body, wornSlot, nested, ref writer),
                _ => true,
            };

            if (!walked)
            {
                return Refused;
            }
        }

        tagCount = writer.TagCount;
        sourceCount = writer.SourceCount;
        return writer.LineCount;
    }

    /// <summary>
    /// Kind 131 or kind 133, which share one entry layout (contracts 9.9). Each ENTRY is its own source,
    /// because each carries its own stored position and spec 11.4 ordinals them by index in the stored
    /// list. That list is ascending by mod id, which is the SORTED list the table names, because the
    /// encoder sorts it whatever order the affixes were rolled or crafted in.
    /// </summary>
    bool Entries(ReadOnlySpan<byte> body, byte sourceKind, int wornSlot, long instanceId, ref Writer writer)
    {
        if (body.Length == 0)
        {
            return false;
        }

        int offset = 1;
        int count = body[0];
        for (int entry = 0; entry < count; entry++)
        {
            if (!TryReadEntry(body, ref offset, out int modId, out int tierOrdinal, out ushort position))
            {
                return false;
            }

            writer.Open();
            if (!Append(modId, tierOrdinal, position, ref writer))
            {
                return false;
            }

            var key = new StatSourceKey(
                sourceKind,
                InstanceStatSourceKind.EntryOrdinal(wornSlot, entry),
                instanceId);
            if (!writer.Close(in key))
            {
                return false;
            }
        }

        return offset == body.Length;
    }

    /// <summary>
    /// Kind 132, in AUTHORED order and never sorted (spec 3.5), so the ordinal is the authored index. A
    /// player who rearranges two gems can move a displayed value by ONE unit, which is correct and is the
    /// price of integer rounding being honest, and sorting here is exactly the fix that would hide it.
    /// <para>
    /// The recursion is ONE level: the contained item's affixes and enchantments are read and its own
    /// socket field is not, which is contracts 9.5's limit honoured by stopping. All of its lines land in
    /// ONE source, because the socket is what the ordinal names.
    /// </para>
    /// </summary>
    bool Sockets(ReadOnlySpan<byte> body, int wornSlot, scoped Span<PayloadField> nested, ref Writer writer)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint count, out _))
        {
            return false;
        }

        for (uint entry = 0; entry < count; entry++)
        {
            if (!ContentVarint.TryRead(body, ref offset, out _, out _)
                || !ContentVarint.TryRead(body, ref offset, out uint definition, out _)
                || !ContentVarint.TryReadUInt64(body, ref offset, out ulong contained, out _)
                || !ContentVarint.TryRead(body, ref offset, out uint length, out _)
                || length > (uint)(body.Length - offset))
            {
                return false;
            }

            ReadOnlySpan<byte> inner = body.Slice(offset, (int)length);
            offset += (int)length;
            if (definition == 0 || inner.Length == 0)
            {
                continue;
            }

            if (entry > InstanceStatSourceKind.MaxEntryIndex
                || !ItemInstancePayload.TryDecode(inner, nested, out int fieldCount, out _))
            {
                return false;
            }

            writer.Open();
            for (int index = 0; index < fieldCount; index++)
            {
                PayloadField field = nested[index];
                if (field.Kind is not (InstancePropertyKind.Affixes or InstancePropertyKind.Enchantments))
                {
                    continue;
                }

                if (!Contained(inner.Slice(field.BodyStart, field.BodyLength), ref writer))
                {
                    return false;
                }
            }

            // The contained item's own instance id rides into the key, so two gems of one mod in one item
            // still order rather than tying. A node prefixed id sets the high bit, so the reinterpretation
            // is unchecked: the key orders by it and never arithmetics on it.
            var key = new StatSourceKey(
                StatSourceKey.SocketedItemKind,
                InstanceStatSourceKind.EntryOrdinal(wornSlot, (int)entry),
                unchecked((long)contained));
            if (!writer.Close(in key))
            {
                return false;
            }
        }

        return offset == body.Length;
    }

    /// <summary>
    /// One nested payload's affix or enchantment list, appended to the socket's SINGLE open source rather
    /// than opened as sources of its own.
    /// </summary>
    bool Contained(ReadOnlySpan<byte> body, ref Writer writer)
    {
        if (body.Length == 0)
        {
            return false;
        }

        int offset = 1;
        int count = body[0];
        for (int entry = 0; entry < count; entry++)
        {
            if (!TryReadEntry(body, ref offset, out int modId, out int tierOrdinal, out ushort position)
                || !Append(modId, tierOrdinal, position, ref writer))
            {
                return false;
            }
        }

        return offset == body.Length;
    }

    /// <summary>
    /// One tier's lines, in <c>sort</c> order, resolved from the ONE stored position through contracts
    /// 6.4's formula. A tier this version carries no live row for contributes nothing and is NOT a
    /// refusal, because a stored item naming content a later version dropped is ordinary rather than
    /// something this read path gets to have an opinion about.
    /// <para>
    /// Each line's scope is copied into the caller's tag span and its <c>TagScopeStart</c> is written
    /// relative to where the SOURCE's own run begins, which is the frame <c>AddSource</c> rebases from, so
    /// a source is portable into the evaluator's own shared array without a caller touching a single
    /// index. The <c>Writer</c> below owns that arithmetic.
    /// </para>
    /// </summary>
    bool Append(int modId, int tierOrdinal, ushort position, ref Writer writer)
    {
        int slot = FindTier(modId, tierOrdinal);
        if (slot < 0 || _tierLineCount[slot] == 0)
        {
            return true;
        }

        int count = _tierLineCount[slot];
        int start = _tierLineStart[slot];
        for (int index = 0; index < count; index++)
        {
            int line = start + index;
            if (!writer.Write(
                _lineStatId[line],
                _lineCombine[line],
                RollPosition.Resolve(position, _lineMinimum[line], _lineMaximum[line]),
                _tagIds.AsSpan(_lineTagStart[line], _lineTagCount[line]),
                _lineConditionId[line]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The slot one <c>(mod id, tier ordinal)</c> pair sits at, or -1 for a pair with no row.</summary>
    int FindTier(int modId, int tierOrdinal)
    {
        long key = Pack(modId, tierOrdinal);
        int low = 0;
        int high = _tierKeys.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            long at = _tierKeys[middle];
            if (at == key)
            {
                return middle;
            }

            if (at < key)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// One entry of kind 131, 133 or a nested one of either:
    /// <c>[ModId: varint][Tier: byte][Position: uint16 LE][Flags: varint]</c>. The position is read with
    /// the endianness in the method name on both sides (contracts 15), never through
    /// <c>BitConverter</c>, and the reserved flags field is read and discarded so the cursor lands where
    /// the next entry starts.
    /// </summary>
    static bool TryReadEntry(
        ReadOnlySpan<byte> body,
        ref int offset,
        out int modId,
        out int tierOrdinal,
        out ushort position)
    {
        modId = 0;
        tierOrdinal = 0;
        position = 0;
        if (!ContentVarint.TryRead(body, ref offset, out uint raw, out _) || raw is 0 or > int.MaxValue)
        {
            return false;
        }

        if (offset >= body.Length)
        {
            return false;
        }

        tierOrdinal = body[offset++];
        if (body.Length - offset < 2)
        {
            return false;
        }

        position = BinaryPrimitives.ReadUInt16LittleEndian(body[offset..]);
        offset += 2;
        if (!ContentVarint.TryRead(body, ref offset, out _, out _))
        {
            return false;
        }

        modId = (int)raw;
        return true;
    }

    /// <summary>
    /// Every live <c>mod_tier</c> row keyed by the pair a PAYLOAD carries, ascending, which is what makes
    /// the lookup a binary search. A duplicate pair keeps the LOWEST row id and the other row's lines are
    /// dropped with it, so the index does not depend on the order rows arrived in. The validator reports
    /// the duplicate as its own finding.
    /// </summary>
    static (long[] Keys, int[] Start, int[] Count) ReadTiers(IContentSnapshot snapshot, Dictionary<int, int> slotOf)
    {
        var tiers = new List<(long Key, int RowId)>();
        foreach (ContentRow row in snapshot.Rows(new ContentTypeId(InstanceContentTypeIds.ModTierTypeId)))
        {
            if (row.IsRetired)
            {
                continue;
            }

            long modId = InstanceContentChecks.Number(row, ModTierContentType.ModIdIndex) ?? 0;
            long ordinal = InstanceContentChecks.Number(row, ModTierContentType.OrdinalIndex) ?? 0;
            if (modId is < 1 or > int.MaxValue
                || ordinal < ModTierContentType.MinOrdinal
                || ordinal > ModTierContentType.MaxOrdinal)
            {
                continue;
            }

            tiers.Add((Pack((int)modId, (int)ordinal), row.Id));
        }

        tiers.Sort(static (left, right) =>
        {
            int byKey = left.Key.CompareTo(right.Key);
            return byKey != 0 ? byKey : left.RowId.CompareTo(right.RowId);
        });

        var keys = new List<long>(tiers.Count);
        foreach ((long key, int rowId) in tiers)
        {
            if (keys.Count != 0 && keys[^1] == key)
            {
                continue;
            }

            slotOf[rowId] = keys.Count;
            keys.Add(key);
        }

        return ([.. keys], new int[keys.Count], new int[keys.Count]);
    }

    /// <summary>
    /// One <c>stat_line</c> row into the fields the build reads, or false for a row this builder can make
    /// nothing of: a parent that is not in the index, a stat id or a combine value outside its domain.
    /// Every one of those is a validator finding elsewhere and none of them is a reason to fail a read.
    /// </summary>
    static bool TryReadLine(ContentRow row, Dictionary<int, int> slotOf, List<int> scope, out LineRow line)
    {
        line = default;
        long tierId = InstanceContentChecks.Number(row, StatLineContentType.ModTierIdIndex) ?? 0;
        if (tierId is < 1 or > int.MaxValue || !slotOf.TryGetValue((int)tierId, out int slot))
        {
            return false;
        }

        long statId = InstanceContentChecks.Number(row, StatLineContentType.StatIdIndex) ?? 0;
        if (statId is < 1 or > int.MaxValue)
        {
            return false;
        }

        StatCombineKind combine = (InstanceContentChecks.Number(row, StatLineContentType.CombineIndex) ?? 0) switch
        {
            StatLineContentType.CombineFlat => StatCombineKind.Flat,
            StatLineContentType.CombineIncreased => StatCombineKind.Increased,
            StatLineContentType.CombineMore => StatCombineKind.More,
            _ => default,
        };

        if (combine == default)
        {
            return false;
        }

        ReadScope(row, scope);
        line = new LineRow(
            slot,
            Narrow(InstanceContentChecks.Number(row, StatLineContentType.SortIndex) ?? 0),
            row.Id,
            (int)statId,
            combine,
            Narrow(InstanceContentChecks.Number(row, StatLineContentType.MinIndex) ?? 0),
            Narrow(InstanceContentChecks.Number(row, StatLineContentType.MaxIndex) ?? 0),
            Narrow(InstanceContentChecks.Number(row, StatLineContentType.ConditionIdIndex)
                ?? IStatConditionRegistry.Unconditional),
            0,
            0);
        return true;
    }

    /// <summary>
    /// One line's <c>tag_scope</c>, which is the varint ids in authored order with no count of their own
    /// (contracts 4.6), exactly as the row walk wrote them. A list that stops reading mid way keeps what it
    /// read rather than failing the index, because the bytes already decoded into a row and a list nothing
    /// can read is the validator's finding rather than this constructor's.
    /// </summary>
    static void ReadScope(ContentRow row, List<int> into)
    {
        into.Clear();
        if (StatLineContentType.TagScopeIndex >= row.Fields.Count)
        {
            return;
        }

        ContentFieldValue value = row.Fields[StatLineContentType.TagScopeIndex];
        if (value.IsAbsent || value.Bytes.Length == 0)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = value.Bytes.Span;
        int offset = 0;
        while (offset < bytes.Length)
        {
            if (!ContentVarint.TryRead(bytes, ref offset, out uint raw, out _))
            {
                return;
            }

            int tagId = unchecked((int)raw);
            if (tagId >= 1)
            {
                into.Add(tagId);
            }
        }
    }

    /// <summary>
    /// The lookup key a payload's <c>(mod id, tier ordinal)</c> pair makes. The ordinal takes the low
    /// eight bits because it is a BYTE on the wire and runs 1 to 255, so the pair packs into a long with
    /// no mod id it can alias onto.
    /// </summary>
    static long Pack(int modId, int tierOrdinal) => ((long)modId << 8) | (uint)tierOrdinal;

    /// <summary>One authored number into an int, saturating rather than wrapping.</summary>
    static int Narrow(long value)
        => value < int.MinValue ? int.MinValue : value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>
    /// The output cursor over the three caller spans, and the ONE place the per source arithmetic lives.
    /// <para>
    /// <see cref="Open"/> marks where a source's lines and tags begin, <see cref="Write"/> appends one
    /// line with its scope REBASED onto that mark, and <see cref="Close"/> seals the run or drops it when
    /// it stayed empty. A tier with no live row leaves the run empty and closes to nothing, which is why a
    /// stored affix naming content a later version dropped costs a source rather than a refusal.
    /// </para>
    /// <para>
    /// Every method answers false rather than throwing when a span runs out, and a build that gets a false
    /// reports zero of everything: a caller reading a partial answer as a whole one is how an item would
    /// silently lose an affix.
    /// </para>
    /// </summary>
    ref struct Writer
    {
        readonly Span<StatModifierLine> _lines;
        readonly Span<int> _tags;
        readonly Span<InstanceStatSource> _sources;
        int _lineOrigin;
        int _tagOrigin;

        internal Writer(Span<StatModifierLine> lines, Span<int> tags, Span<InstanceStatSource> sources)
        {
            _lines = lines;
            _tags = tags;
            _sources = sources;
        }

        /// <summary>The lines written so far, across every closed source.</summary>
        internal int LineCount { get; private set; }

        /// <summary>The tags written so far.</summary>
        internal int TagCount { get; private set; }

        /// <summary>The sources closed so far.</summary>
        internal int SourceCount { get; private set; }

        /// <summary>Marks where the next source's lines and tags start.</summary>
        internal void Open()
        {
            _lineOrigin = LineCount;
            _tagOrigin = TagCount;
        }

        /// <summary>Appends one line, copying its scope in and rebasing the scope start onto the source.</summary>
        internal bool Write(
            int statId,
            StatCombineKind combine,
            int value,
            ReadOnlySpan<int> scope,
            int conditionId)
        {
            if (LineCount == _lines.Length || _tags.Length - TagCount < scope.Length)
            {
                return false;
            }

            int scopeStart = TagCount - _tagOrigin;
            scope.CopyTo(_tags[TagCount..]);
            TagCount += scope.Length;
            _lines[LineCount++] = new StatModifierLine(
                statId,
                combine,
                value,
                scopeStart,
                scope.Length,
                conditionId);
            return true;
        }

        /// <summary>Seals the open run under one key, or drops it when no line landed in it.</summary>
        internal bool Close(in StatSourceKey key)
        {
            if (LineCount == _lineOrigin)
            {
                return true;
            }

            if (SourceCount == _sources.Length)
            {
                return false;
            }

            _sources[SourceCount++] = new InstanceStatSource(
                key,
                _lineOrigin,
                LineCount - _lineOrigin,
                _tagOrigin,
                TagCount - _tagOrigin);
            return true;
        }
    }

    /// <summary>One <c>stat_line</c> row as the constructor holds it before it flattens the lot.</summary>
    readonly record struct LineRow(
        int TierSlot,
        int Sort,
        int RowId,
        int StatId,
        StatCombineKind Combine,
        int Minimum,
        int Maximum,
        int ConditionId,
        int TagStart,
        int TagCount);
}
