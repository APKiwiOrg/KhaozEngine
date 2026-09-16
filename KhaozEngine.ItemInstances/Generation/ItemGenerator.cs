using System;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// Spec 9.4's thirteen steps, in order, because the ORDER of the draws is the reproducibility contract. It
/// turns (base, item level, source of randomness) into a canonical payload and it does NOT decide WHICH
/// base drops: that is a loot table, which is Scope A's engine-range content type and is <c>ServerOnly</c>.
/// <para>
/// <b>It takes its <see cref="IRandomSource"/> in the CONSTRUCTOR and holds it</b>, which is contracts 14.4
/// verbatim. A per call source keeps "does this roll" answerable at the METHOD and loses it at the TYPE,
/// which is the half the contract cares about: a caller anywhere in the fleet could hand a seeded source to
/// the production generator with nothing in any signature to notice. A replay harness builds a SECOND
/// generator over the same immutable <see cref="ModCandidateTables"/>, which costs one object and no table
/// build.
/// </para>
/// <para>
/// <b>Every draw is a function of the affix COUNT and nothing else</b> (spec 9.3). A candidate that is
/// filtered out leaves the pool BEFORE the draw rather than being drawn and rejected, and a pick whose live
/// pool is EMPTY still draws twice and discards both. Without those two discards one item consumes fewer
/// draws than another of the same rarity on the same base, and a seeded session diverges at the first item
/// whose pool empties.
/// </para>
/// <para>
/// <b>Every one of those draws goes through <see cref="BoundedDraw"/>, and that is not a formality.</b> A
/// bound of one consumes NOTHING from the stream, by <see cref="IRandomSource.NextInt"/>'s own contract, so
/// a discard written as <c>NextInt(0, 1)</c> is no discard at all and a real draw over a live weight of one
/// costs the stream nothing either. Both take <see cref="IRandomSource.Skip"/> instead, which advances it by
/// exactly one draw, so the position after an item is a function of the pick count and never of what the
/// pool happened to hold.
/// </para>
/// <para>
/// <b>Nothing here is a function of the candidate pool's SIZE.</b> Opening the pool is a handful of scalar
/// reads per tag position, a pick is a walk of at most
/// <see cref="ModCandidateTables.MaxGenerationTagPositions"/> tag positions plus a walk of the dead entries
/// behind the draw plus a binary search, and every working array is scratch this generator owns, sized at
/// construction from the tables. The one thing a roll allocates is the payload and the
/// <see cref="ItemInstancePayloadBuilder"/> that encodes it, which is the one encoder the payload has and
/// is what makes canonical form free.
/// </para>
/// </summary>
public sealed partial class ItemGenerator
{
    /// <summary>
    /// The most excluded runs one tag table can hold. A placement seats one run per table and its group
    /// seats one more per member, so the bound is the affix count times the widest group plus itself, and
    /// the clamp is what keeps a pathological pack from sizing this in gigabytes.
    /// </summary>
    const int RunCeiling = 4_096;

    readonly ModCandidateTables _tables;
    readonly GenerationContentTables _content;
    readonly IRandomSource _random;
    readonly InstanceIdAllocator _allocator;
    readonly int _contentVersion;

    readonly int _kindCount;
    readonly int _tagPositions;
    readonly int _maxRuns;
    readonly int[] _slotBucket;
    readonly int[] _slotHeader;
    readonly int[] _slotLength;
    readonly int[] _slotLiveWeight;
    readonly int[] _runFirst;
    readonly int[] _runLast;
    readonly int[] _runWeight;
    readonly int[] _runCount;
    readonly int[] _liveCount;
    readonly int[] _kindPlaced;
    readonly int[] _groupIds;
    readonly int[] _groupPlaced;
    readonly InstanceAffix[] _affixes;
    readonly InstanceSocket[] _sockets;
    readonly int[] _nameWords;

    int _tagCount;
    int _groupCount;

    /// <summary>
    /// Builds a generator over one version's immutable tables. Every dependency arrives here rather than
    /// through an ambient static or a default (contracts 14.4), and the two expensive ones, the candidate
    /// tables and the content fold, are shared by every generator built over the same snapshot.
    /// </summary>
    /// <param name="tables">The candidate tables, built once at boot and immutable afterwards.</param>
    /// <param name="snapshot">The version those tables were built from, read here for the rarity rules, the
    /// rarity and name weights, each base's durability and sockets, and the unique templates.</param>
    /// <param name="random">The gameplay randomness seam. A type with no <see cref="IRandomSource"/> cannot
    /// roll, which is the property that makes this class's signature answer the question.</param>
    /// <param name="allocator">The durable instance id allocator, spec 3.6, asked ONLY when the payload is
    /// non-empty.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    public ItemGenerator(
        ModCandidateTables tables,
        IContentSnapshot snapshot,
        IRandomSource random,
        InstanceIdAllocator allocator)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(allocator);

        _tables = tables;
        _random = random;
        _allocator = allocator;
        _contentVersion = snapshot.VersionNumber;
        _content = GenerationContentTables.Build(snapshot, tables);

        _kindCount = Math.Max(tables.KindCount, 1);
        _tagPositions = Math.Max(tables.TagPositionCount, 1);
        int slots = _kindCount * _tagPositions;
        _maxRuns = RunsPerSlot(tables, _content);

        _slotBucket = new int[slots];
        _slotHeader = new int[slots];
        _slotLength = new int[slots];
        _slotLiveWeight = new int[slots];
        _runFirst = new int[slots * _maxRuns];
        _runLast = new int[slots * _maxRuns];
        _runWeight = new int[slots * _maxRuns];
        _runCount = new int[slots];
        _liveCount = new int[_kindCount];
        _kindPlaced = new int[_kindCount];

        int affixCeiling = Math.Clamp(Math.Max(_content.MaxAffixCount, _content.MaxUniqueLineCount), 0, byte.MaxValue);
        _affixes = new InstanceAffix[affixCeiling];
        _groupIds = new int[affixCeiling];
        _groupPlaced = new int[affixCeiling];
        _sockets = new InstanceSocket[_content.MaxSocketCount];
        _nameWords = new int[_content.NamePositionCount];
    }

    /// <summary>
    /// Rolls one item. The steps below are spec 9.4's, numbered, and their order is the contract rather
    /// than an implementation detail.
    /// </summary>
    /// <param name="context">What to roll.</param>
    /// <returns>The resolved item, its canonical payload and the instance id it took.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An item level outside 1 to 65535, a quality outside 0
    /// to 65535, a forced rarity id outside 0 to 255, a base this version has no live row for, or a forced
    /// unique template this version has no live row for. <b>This is not the codec level width enforcement
    /// of issue 917</b>, which stays gated on issue 903: these are the values a CALLER hands in by code,
    /// refused in the same class as <see cref="ItemInstancePayloadBuilder"/>'s own throws, and nothing here
    /// closes the hole a stored byte can still walk through.</exception>
    /// <exception cref="InvalidOperationException">A forced rarity names no live rule, or the rarity rule
    /// asks for more affixes than kind 131's byte count can hold.</exception>
    public GenerationResult Generate(in GenerationContext context)
    {
        int baseIndex = RefuseAtTheDoor(in context);
        if (context.ForcedUniqueTemplateId != 0)
        {
            return GenerateUnique(in context, baseIndex);                                   // step 2
        }

        int signature = SignatureOf(context.BaseId);
        OpenPool(signature, _tables.BandOf(context.ItemLevel));                              // step 1
        int rarityIndex = ResolveRarity(in context, signature);                              // step 3
        int rarityId = rarityIndex < 0 ? 0 : _content.RarityIdAt(rarityIndex);
        int requested = RollAffixCount(rarityIndex);                                         // step 4
        int placed = DrawAffixes(rarityIndex, requested);                                    // steps 5 to 8
        SortAffixes(placed);                                                                 // step 9
        int words = RollRareName(signature, rarityIndex);                                    // step 10
        int sockets = SeatSockets(_content.BaseSocketsAt(baseIndex));                        // step 11
        byte[] payload = Assemble(in context, baseIndex, 0, rarityId, placed, words, sockets); // step 12
        return Seal(in context, payload, rarityId, placed, requested);                       // step 13
    }

    /// <summary>
    /// Step 2 of 9.4: a FORCED unique takes its lines and its sockets from content and draws NOTHING.
    /// Deciding that a unique drops is the loot table's job, so nothing here rolls one, and a unique's
    /// lines are ordinary mod rows with a single tier and no weight row anywhere, which is what lets them
    /// sit in kind 131 beside a rolled affix with no second entry shape.
    /// <para>
    /// The lines take roll position <see cref="RollPosition.Bottom"/>, because the step draws nothing and a
    /// position has to be something. Spec 8.6 describes a unique's kind 131 as "the rolled positions for
    /// the template's lines" and spec 9.4 step 2 says no draw, which is a disagreement between two sections
    /// rather than a choice this code makes. The v1 reading follows 9.4: the <c>reroll values</c> craft
    /// primitive is what puts rolls on a unique, and it rewrites positions without knowing what a unique is.
    /// </para>
    /// </summary>
    GenerationResult GenerateUnique(in GenerationContext context, int baseIndex)
    {
        int index = _content.IndexOfUnique(context.ForcedUniqueTemplateId);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.ForcedUniqueTemplateId,
                "This version carries no live unique_template row under that id, so there is nothing to seat.");
        }

        ReadOnlySpan<int> mods = _content.UniqueLineModsAt(index);
        ReadOnlySpan<int> tiers = _content.UniqueLineTiersAt(index);
        int placed = 0;
        for (int line = 0; line < mods.Length && placed < _affixes.Length; line++)
        {
            _affixes[placed++] = new InstanceAffix(mods[line], (byte)tiers[line], RollPosition.Bottom);
        }

        SortAffixes(placed);                                                                 // step 9
        int sockets = SeatSockets(_content.UniqueSocketsAt(index));                          // step 11
        byte[] payload = Assemble(
            in context,
            baseIndex,
            context.ForcedUniqueTemplateId,
            context.ForcedRarityId,
            placed,
            0,
            sockets);                                                                        // step 12
        return Seal(in context, payload, context.ForcedRarityId, placed, placed);            // step 13
    }

    /// <summary>
    /// Step 13: the instance id, allocated ONLY when the payload is non-empty. The rule is contracts 6.2's
    /// verbatim and it is a property of the ITEM rather than of the definition, so a definition gaining a
    /// per-instance field later does not retroactively give every stored copy an id it does not have. An
    /// empty payload takes id 0 and the item is a plain stack.
    /// </summary>
    GenerationResult Seal(in GenerationContext context, byte[] payload, int rarityId, int placed, int requested)
        => new(
            context.BaseId,
            payload.Length == 0 ? 0 : _allocator.Next(),
            payload,
            rarityId,
            _contentVersion,
            placed,
            requested);

    /// <summary>
    /// Every refusal the generator makes on the values a caller hands in. Each is a caller bug in the same
    /// class as <see cref="ItemInstancePayloadBuilder"/>'s own throws, and the generator is handed values by
    /// CODE rather than bytes by a peer, which is why these throw while every decoder in this package
    /// answers a reason instead.
    /// </summary>
    int RefuseAtTheDoor(in GenerationContext context)
    {
        if (context.ItemLevel is < ModTierContentType.MinItemLevel or > ModTierContentType.MaxItemLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.ItemLevel,
                FormattableString.Invariant(
                    $"An item level runs {ModTierContentType.MinItemLevel} to {ModTierContentType.MaxItemLevel}, because kind 2 stores it as a varint uint16."));
        }

        if (context.Quality is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.Quality,
                FormattableString.Invariant(
                    $"A quality runs 0 to {ushort.MaxValue} whole percentage points, because kind 3 stores it as a varint uint16."));
        }

        if (context.ForcedRarityId is < 0 or > RarityRuleContentType.MaxDefinitionId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.ForcedRarityId,
                FormattableString.Invariant(
                    $"A rarity rule id runs 1 to {RarityRuleContentType.MaxDefinitionId}, or 0 to roll one, because kind 130 stores it as a single byte."));
        }

        int baseIndex = _content.IndexOfBase(context.BaseId);
        if (baseIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(context),
                context.BaseId,
                "This version carries no live item row under that base id, so it has no tags to roll against.");
        }

        return baseIndex;
    }

    /// <summary>The tag signature one base carries, which is what the whole roll is keyed by.</summary>
    int SignatureOf(int baseId) => _tables.Signatures.TryGetSignature(baseId, out int signature) ? signature : -1;

    /// <summary>
    /// How many excluded runs one tag table may need to hold. A placement seats one run and its group seats
    /// one per member, so the affix count times the widest group bounds it, and the bucket length bounds it
    /// again because runs are disjoint by mod id.
    /// </summary>
    static int RunsPerSlot(ModCandidateTables tables, GenerationContentTables content)
    {
        int widestGroup = 1;
        int widestBucket = 1;
        for (int bucket = 0; bucket < tables.BucketCount; bucket++)
        {
            widestBucket = Math.Max(widestBucket, tables.BucketLength(bucket));
            ReadOnlySpan<int> packed = tables.BucketPacked(bucket);
            for (int entry = 0; entry < packed.Length; entry++)
            {
                int group = tables.GroupOf(ModCandidateTables.ModIdOf(packed[entry]));
                if (group != 0)
                {
                    widestGroup = Math.Max(widestGroup, tables.GroupMembers(group).Length);
                }
            }
        }

        long needed = (long)Math.Max(content.MaxAffixCount, 1) * (1 + widestGroup);
        return (int)Math.Clamp(Math.Min(needed, widestBucket), 1, RunCeiling);
    }
}
