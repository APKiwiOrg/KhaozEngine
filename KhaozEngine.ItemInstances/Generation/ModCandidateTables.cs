using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The precomputed candidate tables of spec 9.2, built ONCE at boot and immutable for the life of the
/// process: the bands, the tag-kind-band buckets, the precomputed overlap between the buckets one base's
/// tags select, and the group index.
/// <para>
/// <b>The base count contributes almost nothing, which is the whole trick.</b> The naive table is keyed by
/// (base, item level) and at 50,000 bases and 100 item levels that is 5,000,000 candidate arrays, tens of
/// gigabytes at any honest candidate count. A base never enters a table here, only its tag LIST does, so
/// fifty thousand bases cost the signature intern and nothing else.
/// </para>
/// <para>
/// <b>Three levels.</b> BANDS are the intervals between every distinct <c>item_level_min</c> and
/// <c>item_level_max + 1</c>, so within one band no tier's gate changes and the live tier set is constant.
/// A BUCKET is one flat pair of arrays per (tag, mod kind, band): the packed key
/// <c>(mod id &lt;&lt; TierBits) | tier ordinal</c> and the CUMULATIVE weight through that entry. The
/// OVERLAP is what the first-tag-wins rule of spec 8.3 DISCARDS, per (tag signature, kind, band, tag
/// position), so a roll subtracts two scalars instead of merging anything.
/// </para>
/// <para>
/// <b>Nothing is allocated, memoized or evicted at a roll.</b> There is no cache, so there is no hit rate,
/// no eviction policy and no pathological pack that degrades to a merge per roll. A roll reads the two to
/// eight buckets its base's tags name and nothing else, which is also why its cost does not scale with the
/// candidate pool's size. Spec 9.2's measured table is the reason: the union materialised is 148 MB, the
/// memoized merge is 11 us per roll at 86 percent of rolls, and the overlap is 5.2 MB and zero.
/// </para>
/// <para>
/// <b>Three things fold in at BUILD time rather than per candidate:</b> the kind, which IS the bucket, the
/// legacy flag, because a legacy tier never enters a table at all, and the running weight, because a
/// cumulative array is a binary search and a weight array is a walk.
/// </para>
/// <para>
/// <b>A BOOT builds these, not a publish.</b> They are immutable afterwards, so there is exactly ONE table
/// set in a process and no roll can see two. A new content version becomes active at server RESTART, so
/// there is no swap to build. <see cref="ModCandidateTablesIndex"/> is the boot-side half.
/// </para>
/// <para>
/// <b>There is no <c>IRandomSource</c> here and there cannot be one.</b> These tables are QUERIED by the
/// generator, which is the type that rolls, so a type with no random source provably cannot roll and a
/// replay harness builds a second generator over this same immutable table set.
/// </para>
/// </summary>
public sealed partial class ModCandidateTables
{
    /// <summary>
    /// The bits the packed key gives the tier ordinal. A packed entry is
    /// <c>(mod id &lt;&lt; TierBits) | tier ordinal</c>, so ascending packed order IS (mod id, tier ordinal)
    /// order and a mod's tiers are one contiguous run in every bucket that carries them.
    /// </summary>
    public const int TierBits = 4;

    /// <summary>The ordinal half of a packed key.</summary>
    public const int TierMask = (1 << TierBits) - 1;

    /// <summary>
    /// The largest tier ordinal the packed key can hold. Spec 8.3 writes the ordinal as 1 to 255 and the
    /// payload's tier slot is a byte, and this table packs four bits, so the two numbers disagree and the
    /// smaller one is an AUTHORING ceiling rather than a silent alias of tier 16 onto tier 0 of the next mod
    /// id. A publish refuses a mod tier past it with
    /// <see cref="InstanceContentFindings.TierOrdinal"/>, and <see cref="Build"/> refuses one that reached a
    /// boot without a publish.
    /// </summary>
    public const int MaxTierOrdinal = TierMask;

    /// <summary>The largest mod id the packed key can hold without running off the top of an int.</summary>
    public const int MaxModId = int.MaxValue >> TierBits;

    /// <summary>
    /// The most entries one bucket may hold. The overlap lists are 16 bit indices INTO a bucket, so a wider
    /// bucket cannot be addressed by one and <see cref="Build"/> refuses rather than wrapping.
    /// </summary>
    public const int MaxBucketEntries = ushort.MaxValue;

    /// <summary>
    /// The most authored tags one item base may carry. The suppression header count is signatures times
    /// bands times kinds times POSITIONS, so an unbounded position count makes the build unbounded. Spec
    /// 9.2's arithmetic assumes two to four and this is double the measured worst case: at the owner's scale
    /// of 300 signatures, 50 bands and 2 kinds it is 240,000 headers at 12 bytes, which is 2.9 MB against
    /// budget 9's 40 MB. The header block is sized by the positions the version's signatures ACTUALLY carry,
    /// so a pack whose widest base lists three tags pays for three.
    /// </summary>
    public const int MaxGenerationTagPositions = 8;

    readonly int[] _bandBoundaries;
    readonly int[] _kinds;
    readonly int[] _tagIds;
    readonly int[] _entryPacked;
    readonly int[] _entryCumulative;
    readonly int[] _bucketStart;
    readonly int[] _bucketLength;
    readonly int[] _modIds;
    readonly int[] _modKind;
    readonly int[] _modGroup;
    readonly int[] _groupIds;
    readonly int[] _groupStart;
    readonly int[] _groupMembers;
    readonly int[] _groupMaxPerItem;
    readonly int[] _suppressStart;
    readonly int[] _suppressCount;
    readonly int[] _suppressWeight;
    ushort[] _suppressIndex;
    int _suppressUsed;

    ModCandidateTables(in BuiltTables built)
    {
        _bandBoundaries = built.BandBoundaries;
        _kinds = built.Kinds;
        _tagIds = built.TagIds;
        _entryPacked = built.EntryPacked;
        _entryCumulative = built.EntryCumulative;
        _bucketStart = built.BucketStart;
        _bucketLength = built.BucketLength;
        _modIds = built.ModIds;
        _modKind = built.ModKind;
        _modGroup = built.ModGroup;
        _groupIds = built.GroupIds;
        _groupStart = built.GroupStart;
        _groupMembers = built.GroupMembers;
        _groupMaxPerItem = built.GroupMaxPerItem;
        Signatures = built.Signatures;

        int headers = Signatures.Count * _kinds.Length * BandCount * TagPositionCount;
        _suppressStart = new int[headers];
        _suppressCount = new int[headers];
        _suppressWeight = new int[headers];
        _suppressIndex = [];
    }

    /// <summary>The interned authored tag lists, which is what a header block is keyed by.</summary>
    public GenerationTagSignature Signatures { get; }

    /// <summary>How many bands the authored level curve produced.</summary>
    public int BandCount => _bandBoundaries.Length - 1;

    /// <summary>The band boundaries, ascending, with the last one the exclusive end of the last band.</summary>
    public ReadOnlySpan<int> BandBoundaries => _bandBoundaries;

    /// <summary>The mod kinds the version's <c>mod</c> rows carry, ASCENDING. A bucket is indexed by position.</summary>
    public ReadOnlySpan<int> Kinds => _kinds;

    /// <summary>How many distinct mod kinds the version carries.</summary>
    public int KindCount => _kinds.Length;

    /// <summary>How many distinct tags carry a weight row, which is the tag axis of the bucket space.</summary>
    public int TagCount => _tagIds.Length;

    /// <summary>How many buckets the three axes make between them.</summary>
    public int BucketCount => _bucketStart.Length;

    /// <summary>The tag POSITION count every header block is sized by, which is the widest signature.</summary>
    public int TagPositionCount => Signatures.MaxTagCount;

    /// <summary>Every entry in every bucket, which is the memory line of spec 9.2's arithmetic.</summary>
    public long TableEntries => _entryPacked.Length;

    /// <summary>Every overlap entry, which is what the first-tag-wins rule discards across the whole key space.</summary>
    public long SuppressedEntries => _suppressUsed;

    /// <summary>
    /// A (tag signature, kind, band) whose live count or live weight disagrees with the merge the build ran
    /// to produce its overlap lists. It MUST be zero: the suppression lists ARE that merge, precomputed, so
    /// a wrong list is a wrong WEIGHT rather than a crash and nothing downstream could catch it.
    /// <para>
    /// <b>A zero here is not proof the lists are right.</b> Both sides of the comparison are derived from
    /// the SAME pass, so a walk that is consistently wrong moves both together and reports zero. It catches
    /// a divergence between the merge and the recording of it, which is what an edit to one and not the
    /// other produces, and nothing more. The independent check is
    /// <c>ModCandidateTablesTests.ReferenceMerge</c>, a second merge written in the test file rather than
    /// shared with this code.
    /// </para>
    /// </summary>
    public int ConsistencyFailures { get; private set; }

    /// <summary>
    /// The self-reported size: the tables, the overlap lists, the group and band indexes and the signature
    /// intern, which is what a boot holds for good. It is the one budget 9 number that does not depend on
    /// the garbage collector's mood.
    /// </summary>
    public long ResidentBytes
        => (((long)_bandBoundaries.Length + _kinds.Length + _tagIds.Length + _entryPacked.Length
            + _entryCumulative.Length + _bucketStart.Length + _bucketLength.Length + _modIds.Length
            + _modKind.Length + _modGroup.Length + _groupIds.Length + _groupStart.Length
            + _groupMembers.Length + _groupMaxPerItem.Length + _suppressStart.Length + _suppressCount.Length
            + _suppressWeight.Length) * sizeof(int))
            + ((long)_suppressIndex.Length * sizeof(ushort))
            + Signatures.ResidentBytes;

    /// <summary>One packed key from its two halves.</summary>
    public static int Pack(int modId, int tierOrdinal) => (modId << TierBits) | tierOrdinal;

    /// <summary>The mod id half of a packed key.</summary>
    public static int ModIdOf(int packed) => packed >> TierBits;

    /// <summary>The tier ordinal half of a packed key.</summary>
    public static int TierOrdinalOf(int packed) => packed & TierMask;

    /// <summary>
    /// The band one item level falls in, by BINARY SEARCH over the boundaries and nothing else. A level
    /// outside the authored range clamps into the end band rather than indexing off the array, because an
    /// item level arrives from a caller rather than from a validated row.
    /// </summary>
    public int BandOf(int itemLevel)
    {
        int low = 0;
        int high = _bandBoundaries.Length - 2;
        while (low < high)
        {
            int middle = low + ((high - low + 1) / 2);
            if (_bandBoundaries[middle] <= itemLevel)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        return low < 0 ? 0 : low;
    }

    /// <summary>The bucket POSITION of one mod kind, or false for a kind no <c>mod</c> row carries.</summary>
    public bool TryGetKindPosition(int kind, out int position)
    {
        position = GenerationTagSignature.IndexOf(_kinds, kind);
        return position >= 0;
    }

    /// <summary>The kind at one bucket position, or 0 when the position is outside the kind set.</summary>
    public int KindAt(int position) => (uint)position >= (uint)_kinds.Length ? 0 : _kinds[position];

    /// <summary>
    /// The bucket one (tag, kind position, band) names, or -1 when this version has no table for it. Every
    /// bucket reader below answers empty for -1, so a roll needs no branch of its own.
    /// </summary>
    public int BucketOf(int tagId, int kindPosition, int band)
    {
        int tagPosition = GenerationTagSignature.IndexOf(_tagIds, tagId);
        if (tagPosition < 0 || (uint)kindPosition >= (uint)_kinds.Length || (uint)band >= (uint)BandCount)
        {
            return -1;
        }

        return (((tagPosition * _kinds.Length) + kindPosition) * BandCount) + band;
    }

    /// <summary>How many entries one bucket holds.</summary>
    public int BucketLength(int bucket) => (uint)bucket >= (uint)_bucketLength.Length ? 0 : _bucketLength[bucket];

    /// <summary>
    /// One bucket's packed keys, ASCENDING by construction. A mod's tiers are contiguous inside it, which is
    /// what makes "deduct this mod's whole run" one binary search and one subtraction.
    /// </summary>
    public ReadOnlySpan<int> BucketPacked(int bucket)
        => (uint)bucket >= (uint)_bucketLength.Length
            ? ReadOnlySpan<int>.Empty
            : _entryPacked.AsSpan(_bucketStart[bucket], _bucketLength[bucket]);

    /// <summary>One bucket's RUNNING weight totals, so a weighted pick is one binary search.</summary>
    public ReadOnlySpan<int> BucketCumulative(int bucket)
        => (uint)bucket >= (uint)_bucketLength.Length
            ? ReadOnlySpan<int>.Empty
            : _entryCumulative.AsSpan(_bucketStart[bucket], _bucketLength[bucket]);

    /// <summary>
    /// One bucket's whole weight, which is its last running total. It fits an <c>int</c> because
    /// <see cref="InstanceContentFindings.WeightBucketOverflow"/> bounds the rows sharing one tag and a
    /// bucket is a SUBSET of those, and <see cref="Build"/> refuses a version where that does not hold.
    /// </summary>
    public int BucketTotal(int bucket)
    {
        int length = BucketLength(bucket);
        return length == 0 ? 0 : _entryCumulative[_bucketStart[bucket] + length - 1];
    }

    /// <summary>One entry's own weight, which is the step its running total takes.</summary>
    public int WeightAt(int bucket, int offset)
    {
        if ((uint)bucket >= (uint)_bucketLength.Length || (uint)offset >= (uint)_bucketLength[bucket])
        {
            return 0;
        }

        int index = _bucketStart[bucket] + offset;
        return _entryCumulative[index] - (offset == 0 ? 0 : _entryCumulative[index - 1]);
    }

    /// <summary>The overlap header one (signature, kind position, band, tag position) is recorded under.</summary>
    public int HeaderOf(int signature, int kindPosition, int band, int tagPosition)
        => ((((signature * _kinds.Length) + kindPosition) * BandCount) + band) * TagPositionCount + tagPosition;

    /// <summary>How many of that tag position's entries an earlier tag of the same signature already carries.</summary>
    public int SuppressedCountAt(int header)
        => (uint)header >= (uint)_suppressCount.Length ? 0 : _suppressCount[header];

    /// <summary>Those entries' summed weight, which a roll subtracts from the bucket's own total.</summary>
    public int SuppressedWeightAt(int header)
        => (uint)header >= (uint)_suppressWeight.Length ? 0 : _suppressWeight[header];

    /// <summary>Their sorted 16 bit indices into the bucket, which the draw skips over.</summary>
    public ReadOnlySpan<ushort> SuppressedIndexesAt(int header)
        => (uint)header >= (uint)_suppressCount.Length
            ? ReadOnlySpan<ushort>.Empty
            : _suppressIndex.AsSpan(_suppressStart[header], _suppressCount[header]);

    /// <summary>
    /// The live candidate COUNT of one kind for one signature in one band, which is the union's count
    /// without the union: the bucket lengths less what the overlap discards.
    /// </summary>
    public long LiveCount(int signature, int kindPosition, int band)
    {
        ReadOnlySpan<int> tags = Signatures.TagsOf(signature);
        long live = 0;
        for (int position = 0; position < tags.Length; position++)
        {
            live += BucketLength(BucketOf(tags[position], kindPosition, band))
                - SuppressedCountAt(HeaderOf(signature, kindPosition, band, position));
        }

        return live;
    }

    /// <summary>
    /// The live WEIGHT of one kind for one signature in one band. It is a <c>long</c> because it sums up to
    /// eight tag positions, each of which is bounded by an <c>int</c> on its own.
    /// </summary>
    public long LiveWeight(int signature, int kindPosition, int band)
    {
        ReadOnlySpan<int> tags = Signatures.TagsOf(signature);
        long live = 0;
        for (int position = 0; position < tags.Length; position++)
        {
            live += BucketTotal(BucketOf(tags[position], kindPosition, band))
                - SuppressedWeightAt(HeaderOf(signature, kindPosition, band, position));
        }

        return live;
    }

    /// <summary>
    /// Every mod of one exclusivity group, ASCENDING by mod id, so "every other mod of this group" is a
    /// lookup rather than a scan. A legacy mod is not in one, because it is in no table to exclude from.
    /// </summary>
    public ReadOnlySpan<int> GroupMembers(int groupId)
    {
        int slot = GenerationTagSignature.IndexOf(_groupIds, groupId);
        return slot < 0
            ? ReadOnlySpan<int>.Empty
            : _groupMembers.AsSpan(_groupStart[slot], _groupStart[slot + 1] - _groupStart[slot]);
    }

    /// <summary>How many of one group an item may carry, or 0 for a group no live mod belongs to.</summary>
    public int MaxPerItem(int groupId)
    {
        int slot = GenerationTagSignature.IndexOf(_groupIds, groupId);
        return slot < 0 ? 0 : _groupMaxPerItem[slot];
    }

    /// <summary>The exclusivity group one mod belongs to, or 0 when it belongs to none.</summary>
    public int GroupOf(int modId)
    {
        int slot = GenerationTagSignature.IndexOf(_modIds, modId);
        return slot < 0 ? 0 : _modGroup[slot];
    }

    /// <summary>One mod's kind, or 0 for a mod this version carries no live row for.</summary>
    public int KindOf(int modId)
    {
        int slot = GenerationTagSignature.IndexOf(_modIds, modId);
        return slot < 0 ? 0 : _modKind[slot];
    }
}
