using System;
using System.Collections.Generic;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The interned tag SIGNATURES of one version's item bases, which is what the candidate tables key their
/// precomputed overlap on (spec 9.2 item 3).
/// <para>
/// <b>A base costs eight bytes because it never enters a table, only its tag LIST does.</b> Tag lists are
/// authored, so a catalog of 50,000 bases carries a few hundred distinct lists rather than 50,000 of them,
/// and every base sharing a list shares its overlap. That is the row of spec 9.2's arithmetic that makes
/// the whole shape survive a large catalog: 400 KB of base-derived memory for fifty thousand bases, against
/// the 5,000,000 candidate arrays the naive (base, item level) key would need.
/// </para>
/// <para>
/// The list is the base's AUTHORED order, verbatim, because the order IS the first-tag-wins rule of spec
/// 8.3 and a sort here would silently reprice every base. A base listing one tag twice keeps both
/// positions, and the overlap pass suppresses the repeat exactly as it suppresses a repeat across two tags.
/// </para>
/// <para>
/// <b>The position count is CAPPED at <see cref="ModCandidateTables.MaxGenerationTagPositions"/>.</b> The
/// suppression header count is signatures times bands times kinds times POSITIONS, so an unbounded position
/// count makes the table build unbounded. A publish reports a base past the ceiling as
/// <see cref="InstanceContentFindings.GenerationTagPositions"/>, and this throws on one that reached a boot
/// without a publish.
/// </para>
/// </summary>
public sealed class GenerationTagSignature
{
    /// <summary>The signatures of a version carrying no item base at all.</summary>
    public static readonly GenerationTagSignature Empty = new([], [], [], [], [], 0);

    readonly int[] _tags;
    readonly int[] _start;
    readonly int[] _count;
    readonly int[] _baseIds;
    readonly int[] _baseSignature;

    GenerationTagSignature(int[] tags, int[] start, int[] count, int[] baseIds, int[] baseSignature, int maxTagCount)
    {
        _tags = tags;
        _start = start;
        _count = count;
        _baseIds = baseIds;
        _baseSignature = baseSignature;
        MaxTagCount = maxTagCount;
    }

    /// <summary>How many DISTINCT authored tag lists the version's bases carry between them.</summary>
    public int Count => _start.Length;

    /// <summary>How many live bases were interned, which is the other half of the eight bytes a base costs.</summary>
    public int BaseCount => _baseIds.Length;

    /// <summary>The widest signature, which is the tag POSITION count every header block is sized by.</summary>
    public int MaxTagCount { get; }

    /// <summary>The tag ids of one signature, in authored order. Empty for a signature this version has none of.</summary>
    public ReadOnlySpan<int> TagsOf(int signature)
        => (uint)signature >= (uint)_start.Length
            ? ReadOnlySpan<int>.Empty
            : _tags.AsSpan(_start[signature], _count[signature]);

    /// <summary>The signature one base carries, or false when this version has no live base under that id.</summary>
    public bool TryGetSignature(int baseId, out int signature)
    {
        int slot = IndexOf(_baseIds, baseId);
        signature = slot < 0 ? 0 : _baseSignature[slot];
        return slot >= 0;
    }

    /// <summary>The managed bytes this holds, counted into the tables' own self-reported size.</summary>
    public long ResidentBytes
        => ((long)_tags.Length + _start.Length + _count.Length + _baseIds.Length + _baseSignature.Length)
            * sizeof(int);

    /// <summary>
    /// Interns every live item base's authored tag list, in base id order, so the signature numbering is a
    /// property of the version rather than of the order rows arrived in.
    /// </summary>
    /// <param name="snapshot">The loaded version, read for its <c>item</c> ROWS and nothing else.</param>
    /// <param name="maxTagPositions">The ceiling a base's tag list may not pass.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidOperationException">A live base carries more tags than the ceiling permits.</exception>
    public static GenerationTagSignature Build(IContentSnapshot snapshot, int maxTagPositions)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTagPositions);

        IReadOnlyList<ContentRow> rows = snapshot.Rows(new ContentTypeId(EngineContentTypes.ItemTypeId));
        if (rows.Count == 0)
        {
            return Empty;
        }

        var tags = new List<int>(rows.Count);
        var start = new List<int>();
        var count = new List<int>();
        var baseIds = new List<int>(rows.Count);
        var baseSignature = new List<int>(rows.Count);
        var byHash = new Dictionary<ulong, List<int>>();
        var scratch = new List<int>(maxTagPositions);
        int widest = 0;

        foreach (ContentRow row in rows)
        {
            if (row.IsRetired)
            {
                continue;
            }

            ReadTags(row, scratch);
            if (scratch.Count > maxTagPositions)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"{InstanceContentFindings.GenerationTagPositions}: item base {row.Id} carries {scratch.Count} authored tags, over the generation ceiling of {maxTagPositions}. The candidate tables size their suppression headers by the tag POSITION count, so a base past the ceiling makes the table build unbounded."));
            }

            int signature = Intern(tags, start, count, byHash, scratch);
            baseIds.Add(row.Id);
            baseSignature.Add(signature);
            if (scratch.Count > widest)
            {
                widest = scratch.Count;
            }
        }

        return baseIds.Count == 0
            ? Empty
            : new GenerationTagSignature(
                tags.ToArray(), start.ToArray(), count.ToArray(), baseIds.ToArray(), baseSignature.ToArray(), widest);
    }

    /// <summary>
    /// One row's authored tag list, which is the FIRST tag-list field it carries. The engine owns the
    /// <c>item</c> schema and it declares exactly one, so reading the value list by kind needs no schema and
    /// keeps this on the <see cref="IContentSnapshot"/> seam the load index is handed.
    /// </summary>
    internal static void ReadTags(ContentRow row, List<int> into)
    {
        into.Clear();
        for (int field = 0; field < row.Fields.Count; field++)
        {
            ContentFieldValue value = row.Fields[field];
            if (value.Kind != ContentFieldKind.TagList)
            {
                continue;
            }

            if (value.IsAbsent || value.Bytes.Length == 0)
            {
                return;
            }

            // The varint ids in authored order with no count of their own, which is what the row walk wrote.
            // A malformed list stops the walk rather than failing the load, exactly as the tag index does:
            // the bytes already decoded into a row, so a list nothing can read is the validator's finding.
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

            return;
        }
    }

    /// <summary>
    /// The signature that tag list already has, or a new one appended. The hash buckets the candidates and
    /// the sequence compare decides, so two bases with the same list always land on one signature.
    /// </summary>
    static int Intern(
        List<int> tags,
        List<int> start,
        List<int> count,
        Dictionary<ulong, List<int>> byHash,
        List<int> authored)
    {
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < authored.Count; i++)
        {
            hash = (hash ^ unchecked((uint)authored[i])) * 1099511628211UL;
        }

        if (!byHash.TryGetValue(hash, out List<int>? candidates))
        {
            candidates = [];
            byHash.Add(hash, candidates);
        }

        foreach (int candidate in candidates)
        {
            if (Same(tags, start[candidate], count[candidate], authored))
            {
                return candidate;
            }
        }

        int signature = start.Count;
        start.Add(tags.Count);
        count.Add(authored.Count);
        for (int i = 0; i < authored.Count; i++)
        {
            tags.Add(authored[i]);
        }

        candidates.Add(signature);
        return signature;
    }

    static bool Same(List<int> tags, int start, int count, List<int> authored)
    {
        if (count != authored.Count)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (tags[start + i] != authored[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A binary search written out rather than taken from <see cref="Array"/>, because every reader of this
    /// type is on a path that must allocate nothing and a comparer is one more thing to be sure of.
    /// </summary>
    internal static int IndexOf(int[] ascending, int value)
    {
        int low = 0;
        int high = ascending.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            int at = ascending[middle];
            if (at == value)
            {
                return middle;
            }

            if (at < value)
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
}
