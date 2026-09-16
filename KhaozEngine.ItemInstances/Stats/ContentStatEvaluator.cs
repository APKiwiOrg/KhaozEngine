using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The integer stat evaluator of spec 11, contracts 13.2's formula through spec 11.6's eight steps, with a
/// per stat inverted index so a read walks only the lines that touch that stat.
/// <para>
/// <b>Nothing on the read path allocates.</b> <see cref="Value"/> and <see cref="CopyValuesTo"/> write into
/// a caller span and every working array is built when a source is ADDED, which is a property of the
/// SIGNATURES rather than a thing to be careful about. An evaluation that allocates per attack is an
/// evaluation that runs per attack, and budget 6 is 2 us and zero bytes for eleven worn items.
/// </para>
/// <para>
/// <b>No float, anywhere</b> (contracts 13.4). Intermediates are <c>long</c>, every divide is FLOOR
/// division through <see cref="FloorDivide"/>, and the result is checked into <c>int</c> before the clamp
/// to the stat row's own <c>min</c> and <c>max</c>. Contracts 6.4's roll formula is a DIFFERENT formula,
/// lives in <see cref="RollPosition"/> and keeps its <c>/</c>, because its numerator is never negative.
/// </para>
/// <para>
/// <b>It replaces <c>StatSet</c> for content driven stats and does not touch it</b> (contracts 13.3).
/// <c>StatSet</c> is a shipped float kernel and stays unchanged beside this, and a game uses one or the
/// other for a given stat, never both.
/// </para>
/// <para>
/// <b>The cache is keyed by the stat AND the whole context, compared by VALUE.</b> A read of a clean stat
/// whose stored context equals the incoming one answers the cached number without folding, which is spec
/// 11.5's lazy model, and a read under any OTHER context refolds and refreshes what is stored. That is what
/// it takes for the model to be safe: spec 11.5's dirty events are all SOURCE events and a context is not
/// one, so a cache keyed by stat id alone credits a <c>[fire, spell]</c> line read under a spell context to
/// the melee read of the same stat in the same tick, and <see cref="Recompute(in StatSourceKey)"/> is not an
/// escape because it names a source rather than a context.
/// </para>
/// <para>
/// <b>What is compared is the condition mask, the tag count and every tag ELEMENT, in order.</b> The
/// comparison is deliberately conservative: two contexts holding the same tags in a different order are two
/// contexts here and the second one refolds, because an order insensitive compare is a sort or a set on the
/// read path and the read path allocates nothing. The stored context lives in an evaluator owned buffer
/// sized at <see cref="AddSource"/> time, <see cref="MaxContextTags"/> wide per stat, so the compare and the
/// refresh both write into memory that already exists. A context wider than that is read UNCACHED every
/// time and stores nothing, because a number that cannot be compared is a number that cannot be trusted.
/// <see cref="CopyValuesTo"/> goes through the same rule, one stat at a time, and
/// <see cref="Recompute(in StatSourceKey)"/>'s own meaning is unchanged.
/// </para>
/// <para>
/// <b>Two engine schema positions are read by index here and no engine type declares an index constant</b>
/// the way the eighteen instance types do
/// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/968">#968</see>). They are the named
/// constants below rather than literals, and
/// <c>ContentStatEvaluatorTests.The_stat_schema_positions_the_evaluator_reads_are_still_where_it_reads_them</c>
/// pins each one against <see cref="StatContentType.CreateSchema"/>.
/// </para>
/// </summary>
public sealed partial class ContentStatEvaluator
{
    /// <summary>Where <c>stat.scale</c> sits in the engine stat schema.</summary>
    internal const int StatScaleIndex = 1;

    /// <summary>Where <c>stat.min</c> sits, which is the inclusive clamp floor in scaled units.</summary>
    internal const int StatMinimumIndex = 2;

    /// <summary>Where <c>stat.max</c> sits, which is the inclusive clamp ceiling.</summary>
    internal const int StatMaximumIndex = 3;

    /// <summary>Where <c>stat.tags</c> sits, which is the stat's own half of a scope match.</summary>
    internal const int StatTagsIndex = 4;

    /// <summary>
    /// The most context tags one cached read compares, which is the width of the per stat context buffer.
    /// A context carrying more is read UNCACHED every time, because a number that cannot be compared is a
    /// number that cannot be trusted.
    /// </summary>
    public const int MaxContextTags = 16;

    /// <summary>One hundred percent in basis points, which is the identity for both percent kinds.</summary>
    internal const long BasisPointScale = 10_000;

    /// <summary>Half of <see cref="BasisPointScale"/>, added before every divide to round half up.</summary>
    internal const long BasisPointHalf = BasisPointScale / 2;

    readonly IStatConditionRegistry? _conditions;

    readonly int[] _statScale;
    readonly int[] _statMinimum;
    readonly int[] _statMaximum;
    readonly int[][] _statTags;
    readonly int[] _statBase;

    readonly int[][] _statIndex;
    readonly int[] _statIndexCount;
    readonly int[] _cachedValue;
    readonly bool[] _cachedValid;
    readonly int[] _cachedMask;
    readonly int[] _cachedTagCount;

    int[] _cachedTags = Array.Empty<int>();

    readonly List<StatSource> _sources = new();

    StatModifierLine[] _lines = Array.Empty<StatModifierLine>();
    int[] _scopeTags = Array.Empty<int>();
    int _lineCount;
    int _scopeTagCount;

    /// <summary>
    /// Builds an evaluator over one content version, reading <c>scale</c>, <c>min</c>, <c>max</c> and
    /// <c>tags</c> off its <c>stat</c> rows. A RETIRED stat row is read like any other, because a line
    /// still pointing at one needs the bounds it was authored with rather than no bounds at all.
    /// </summary>
    /// <param name="snapshot">The content version this evaluator resolves stats against.</param>
    /// <param name="conditions">
    /// The game's condition registry, or null. Null drops every conditional line, which is the right answer
    /// for a game that authors none.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public ContentStatEvaluator(IContentSnapshot snapshot, IStatConditionRegistry? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        _conditions = conditions;
        IReadOnlyList<ContentRow> rows = snapshot.Rows(new ContentTypeId(EngineContentTypes.StatTypeId));
        int highest = 0;
        foreach (ContentRow row in rows)
        {
            if (row.Id > highest)
            {
                highest = row.Id;
            }
        }

        int slots = highest + 1;
        _statScale = new int[slots];
        _statMinimum = new int[slots];
        _statMaximum = new int[slots];
        _statTags = new int[slots][];
        _statBase = new int[slots];
        _statIndex = new int[slots][];
        _statIndexCount = new int[slots];
        _cachedValue = new int[slots];
        _cachedValid = new bool[slots];
        _cachedMask = new int[slots];
        _cachedTagCount = new int[slots];
        for (int slot = 0; slot < slots; slot++)
        {
            _statScale[slot] = 1;
            _statMinimum[slot] = int.MinValue;
            _statMaximum[slot] = int.MaxValue;
            _statTags[slot] = Array.Empty<int>();
            _statIndex[slot] = Array.Empty<int>();
        }

        var tags = new List<int>();
        foreach (ContentRow row in rows)
        {
            if (row.Id <= 0)
            {
                continue;
            }

            _statScale[row.Id] = Narrow(InstanceContentChecks.Number(row, StatScaleIndex), 1);
            _statMinimum[row.Id] = Narrow(InstanceContentChecks.Number(row, StatMinimumIndex), int.MinValue);
            _statMaximum[row.Id] = Narrow(InstanceContentChecks.Number(row, StatMaximumIndex), int.MaxValue);
            ReadStatTags(row, tags);
            _statTags[row.Id] = tags.Count == 0 ? Array.Empty<int>() : tags.ToArray();
        }

        StatCount = highest;
    }

    /// <summary>The highest <c>stat</c> id this content version carries, which is the id ceiling.</summary>
    public int StatCount { get; }

    /// <summary>Every modifier line currently held, across every source.</summary>
    public int LineCount => _lineCount;

    /// <summary>How many lines touch one stat, which is the width of its inverted index.</summary>
    /// <param name="statId">The stat id.</param>
    /// <exception cref="ArgumentOutOfRangeException">This content version carries no stat under that id.</exception>
    public int StatLineCount(int statId)
    {
        CheckStat(statId);
        return _statIndexCount[statId];
    }

    /// <summary>
    /// The stat's fixed power of ten, so a caller formatting a value divides by the same integer the server
    /// does. It is never used by the fold, which works in scaled units throughout.
    /// </summary>
    /// <param name="statId">The stat id.</param>
    /// <exception cref="ArgumentOutOfRangeException">This content version carries no stat under that id.</exception>
    public int Scale(int statId)
    {
        CheckStat(statId);
        return _statScale[statId];
    }

    /// <summary>
    /// Sets the stat's base value in scaled units, which is what the fold's first step starts from.
    /// </summary>
    /// <param name="statId">The stat id.</param>
    /// <param name="scaledValue">The base, in the stat's scaled units.</param>
    /// <exception cref="ArgumentOutOfRangeException">This content version carries no stat under that id.</exception>
    public void SetBase(int statId, int scaledValue)
    {
        CheckStat(statId);
        _statBase[statId] = scaledValue;
        _cachedValid[statId] = false;
    }

    /// <summary>
    /// Adds or REPLACES one source's lines. A key already held is replaced in place and keeps its position
    /// in the fold order, which is what makes an add then remove cycle restore the prior value exactly.
    /// <para>
    /// Each line's <c>TagScopeStart</c> is relative to <paramref name="tags"/> and is rebased onto the
    /// evaluator's own shared tag array here. This is the path that allocates, and it runs on exactly five
    /// events: equip, unequip, socket, unsocket and a craft that rewrites a worn item's payload.
    /// </para>
    /// </summary>
    /// <param name="key">Which source these lines came from, which is also where they fold.</param>
    /// <param name="lines">The source's lines, in modifier index order.</param>
    /// <param name="tags">The tag span every line's scope indexes into.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A line names a stat this version does not carry, or a scope runs off the end of
    /// <paramref name="tags"/>.
    /// </exception>
    public void AddSource(in StatSourceKey key, ReadOnlySpan<StatModifierLine> lines, ReadOnlySpan<int> tags)
    {
        foreach (StatModifierLine line in lines)
        {
            CheckStat(line.StatId);
            if (line.TagScopeLength < 0
                || line.TagScopeStart < 0
                || line.TagScopeStart + line.TagScopeLength > tags.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lines),
                    FormattableString.Invariant(
                        $"A scope of {line.TagScopeLength} tags at {line.TagScopeStart} runs off a span of {tags.Length}."));
            }
        }

        if (_cachedTags.Length == 0)
        {
            // The read path's buffer, allocated HERE so nothing on the read path ever does. It is the one
            // event that can make a stat worth caching in the first place.
            _cachedTags = new int[_cachedValid.Length * MaxContextTags];
        }

        int position = Find(in key);
        if (position >= 0)
        {
            _sources[position].Fill(in key, lines, tags);
        }
        else
        {
            _sources.Insert(~position, StatSource.Create(in key, lines, tags));
        }

        Rebuild();
    }

    /// <summary>Removes one source's lines, answering false for a key this evaluator does not hold.</summary>
    /// <param name="key">The source to drop.</param>
    /// <returns>True when a source was held under that key.</returns>
    public bool RemoveSource(in StatSourceKey key)
    {
        int position = Find(in key);
        if (position < 0)
        {
            return false;
        }

        _sources.RemoveAt(position);
        Rebuild();
        return true;
    }

    /// <summary>Drops every source. Base values and the stat metadata are untouched.</summary>
    public void ClearSources()
    {
        if (_sources.Count == 0)
        {
            return;
        }

        _sources.Clear();
        Rebuild();
    }

    /// <summary>
    /// Marks the stats one source feeds as dirty, and does NOTHING else (spec 11.5). The next read of one
    /// of them folds again, every other stat keeps its cached number, and a key this evaluator does not
    /// hold contributes no line to any stat so there is nothing of its to dirty.
    /// </summary>
    /// <param name="changed">The source that changed.</param>
    public void Recompute(in StatSourceKey changed)
    {
        int position = Find(in changed);
        if (position < 0)
        {
            return;
        }

        StatSource source = _sources[position];
        for (int index = 0; index < source.StatCount; index++)
        {
            _cachedValid[source.Stats[index]] = false;
        }
    }

    /// <summary>
    /// One stat's value, folded when the stat is dirty or when the cached number belongs to a DIFFERENT
    /// context, and read from the cache when neither is true. Allocates nothing.
    /// </summary>
    /// <param name="statId">The stat id.</param>
    /// <param name="context">The evaluation context, which is compared by value against the one the cached
    /// number was folded under.</param>
    /// <returns>The clamped value, in the stat's scaled units.</returns>
    /// <exception cref="ArgumentOutOfRangeException">This content version carries no stat under that id.</exception>
    public int Value(int statId, in StatContext context)
    {
        CheckStat(statId);
        ReadOnlySpan<int> tags = context.Tags.Span;
        if (tags.Length > MaxContextTags || _cachedTags.Length == 0)
        {
            // Nothing to compare against, so nothing is stored either: a context past the buffer's width,
            // and the case where no source has ever been added and there is no buffer at all.
            return Fold(statId, in context);
        }

        if (_cachedValid[statId] && SameContext(statId, tags, context.ConditionMask))
        {
            return _cachedValue[statId];
        }

        int value = Fold(statId, in context);
        _cachedValue[statId] = value;
        _cachedValid[statId] = true;
        StoreContext(statId, tags, context.ConditionMask);
        return value;
    }

    /// <summary>
    /// Whether the number cached for one stat was folded under the context now being asked about: the
    /// condition mask, then the tag count, then every tag element in order.
    /// </summary>
    bool SameContext(int statId, ReadOnlySpan<int> tags, int conditionMask)
    {
        if (_cachedMask[statId] != conditionMask || _cachedTagCount[statId] != tags.Length)
        {
            return false;
        }

        int start = statId * MaxContextTags;
        for (int position = 0; position < tags.Length; position++)
        {
            if (_cachedTags[start + position] != tags[position])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Records the context a freshly folded number belongs to, into the buffer that already exists.</summary>
    void StoreContext(int statId, ReadOnlySpan<int> tags, int conditionMask)
    {
        _cachedMask[statId] = conditionMask;
        _cachedTagCount[statId] = tags.Length;
        tags.CopyTo(_cachedTags.AsSpan(statId * MaxContextTags, MaxContextTags));
    }

    /// <summary>
    /// Several stats at once, written into the CALLER's span. Allocates nothing, which is the point of the
    /// shape: a tooltip or a combat step asks for the handful of stats it needs into a stack buffer.
    /// </summary>
    /// <param name="statIds">The stats to read, in the order the answers are wanted.</param>
    /// <param name="destination">Where to write them. Must be at least as long as <paramref name="statIds"/>.</param>
    /// <param name="context">The evaluation context.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="statIds"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">This content version carries no stat under one of those ids.</exception>
    public void CopyValuesTo(ReadOnlySpan<int> statIds, Span<int> destination, in StatContext context)
    {
        if (destination.Length < statIds.Length)
        {
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"A destination of {destination.Length} cannot hold {statIds.Length} values."),
                nameof(destination));
        }

        for (int index = 0; index < statIds.Length; index++)
        {
            destination[index] = Value(statIds[index], in context);
        }
    }

    /// <summary>
    /// Rebuilds the flat line store, the shared tag array and every stat's inverted index from the sorted
    /// source list, so the fold order is a property of the STORE rather than of the order a caller equipped
    /// in. It is O(lines) per mutation, on a path that runs five times per equip change and never per tick.
    /// </summary>
    void Rebuild()
    {
        _lineCount = 0;
        _scopeTagCount = 0;
        Array.Clear(_statIndexCount);
        Array.Clear(_cachedValid);

        int lines = 0;
        int scope = 0;
        foreach (StatSource source in _sources)
        {
            lines += source.LineCount;
            scope += source.TagCount;
        }

        if (_lines.Length < lines)
        {
            _lines = new StatModifierLine[Math.Max(lines, 8)];
        }

        if (_scopeTags.Length < scope)
        {
            _scopeTags = new int[Math.Max(scope, 8)];
        }

        foreach (StatSource source in _sources)
        {
            int tagStart = _scopeTagCount;
            for (int index = 0; index < source.TagCount; index++)
            {
                _scopeTags[_scopeTagCount++] = source.Tags[index];
            }

            for (int index = 0; index < source.LineCount; index++)
            {
                StatModifierLine line = source.Lines[index];
                int slot = _lineCount++;
                _lines[slot] = line with { TagScopeStart = tagStart + line.TagScopeStart };
                Index(line.StatId, slot);
            }
        }
    }

    /// <summary>Appends one line's position to its stat's inverted index, growing the bucket as needed.</summary>
    void Index(int statId, int lineSlot)
    {
        int[] bucket = _statIndex[statId];
        int count = _statIndexCount[statId];
        if (count == bucket.Length)
        {
            Array.Resize(ref bucket, bucket.Length == 0 ? 4 : bucket.Length * 2);
            _statIndex[statId] = bucket;
        }

        bucket[count] = lineSlot;
        _statIndexCount[statId] = count + 1;
    }

    /// <summary>
    /// The position of one key in the sorted source list, or the bitwise complement of where it would be
    /// inserted. A plain binary search rather than a dictionary, because the list has to stay in fold order
    /// anyway and a second structure beside it is a second thing to keep true.
    /// </summary>
    int Find(in StatSourceKey key)
    {
        int low = 0;
        int high = _sources.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int comparison = CompareFoldOrder(_sources[middle].Key, key);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return ~low;
    }

    /// <summary>Refuses a stat id this content version carries no row for, which is a caller bug.</summary>
    void CheckStat(int statId)
    {
        if (statId <= 0 || statId >= _cachedValid.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statId),
                statId,
                FormattableString.Invariant($"This content version carries stat ids 1 to {StatCount}."));
        }
    }

    /// <summary>One stat row's authored tag list, read at the schema position this type declares.</summary>
    static void ReadStatTags(ContentRow row, List<int> into)
    {
        into.Clear();
        if (StatTagsIndex >= row.Fields.Count)
        {
            return;
        }

        ContentFieldValue value = row.Fields[StatTagsIndex];
        if (value.IsAbsent || value.Bytes.Length == 0)
        {
            return;
        }

        // The varint ids in authored order with no count of their own, which is what the row walk wrote. A
        // malformed list stops the walk rather than failing the build: the bytes already decoded into a
        // row, so a list nothing can read is the validator's finding rather than this constructor's.
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

    /// <summary>One authored number into an int, or the fallback when the row does not carry it.</summary>
    static int Narrow(long? value, int fallback)
    {
        if (value is not long number)
        {
            return fallback;
        }

        return number < int.MinValue ? int.MinValue : number > int.MaxValue ? int.MaxValue : (int)number;
    }

    /// <summary>
    /// One source's own copy of its lines and its tags, kept so a rebuild can rebase every scope from the
    /// positions the CALLER handed in rather than from wherever they last landed.
    /// </summary>
    sealed class StatSource
    {
        StatSource(in StatSourceKey key)
        {
            Key = key;
            Lines = Array.Empty<StatModifierLine>();
            Tags = Array.Empty<int>();
            Stats = Array.Empty<int>();
        }

        internal StatSourceKey Key { get; private set; }

        internal StatModifierLine[] Lines { get; private set; }

        internal int LineCount { get; private set; }

        internal int[] Tags { get; private set; }

        internal int TagCount { get; private set; }

        /// <summary>The DISTINCT stats this source feeds, which is the set a dirty mark walks.</summary>
        internal int[] Stats { get; private set; }

        internal int StatCount { get; private set; }

        internal static StatSource Create(in StatSourceKey key, ReadOnlySpan<StatModifierLine> lines, ReadOnlySpan<int> tags)
        {
            var source = new StatSource(in key);
            source.Fill(in key, lines, tags);
            return source;
        }

        internal void Fill(in StatSourceKey key, ReadOnlySpan<StatModifierLine> lines, ReadOnlySpan<int> tags)
        {
            Key = key;
            if (Lines.Length < lines.Length)
            {
                Lines = new StatModifierLine[lines.Length];
            }

            if (Tags.Length < tags.Length)
            {
                Tags = new int[tags.Length];
            }

            if (Stats.Length < lines.Length)
            {
                Stats = new int[lines.Length];
            }

            lines.CopyTo(Lines);
            tags.CopyTo(Tags);
            LineCount = lines.Length;
            TagCount = tags.Length;

            StatCount = 0;
            foreach (StatModifierLine line in lines)
            {
                bool seen = false;
                for (int index = 0; index < StatCount; index++)
                {
                    seen |= Stats[index] == line.StatId;
                }

                if (!seen)
                {
                    Stats[StatCount++] = line.StatId;
                }
            }
        }
    }
}
