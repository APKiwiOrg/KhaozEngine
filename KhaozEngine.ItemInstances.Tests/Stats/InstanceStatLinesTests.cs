using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using Xunit;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceContentTypeFixtures;
using static KhaozEngine.Tests.ItemInstances.Content.InstanceValidationFixtures;

namespace KhaozEngine.Tests.ItemInstances.Stats;

/// <summary>
/// Spec 11.4's source table read as assertions, over the builder that turns a payload plus its content
/// into the lines the evaluator folds. Ten facts are the plan's and six more pin what its prose states
/// without naming a fact: the refusal, the ordinal packing, the visibility non-boundary, the one level of
/// socket recursion, a tier this content version no longer carries, and the scope and condition a line
/// carries into its source.
/// <para>
/// Every fact builds its OWN registry, its OWN snapshot and its OWN builder, so nothing here writes
/// process-global state and no <c>DisableParallelization</c> collection is needed. The allocation fact
/// reads <c>GC.GetAllocatedBytesForCurrentThread()</c>, which is per thread, so it needs none either.
/// </para>
/// <para>
/// Every expected number is worked out by hand from contracts 6.4 and 13.2 rather than recomputed by a
/// second copy of either formula, except where the fact is deliberately ABOUT the formula having one copy.
/// </para>
/// </summary>
public sealed class InstanceStatLinesTests
{
    /// <summary>The stat nearly every fact folds, id 1.</summary>
    const int Hottest = 1;

    /// <summary>A second stat, for the tier that grants two lines.</summary>
    const int Second = 2;

    /// <summary>The tag a scoped line asks for.</summary>
    const int FireTag = 7;

    /// <summary>A gem definition id, which a socket names as its contained item.</summary>
    const int GemDefinition = 60;

    [Fact]
    public void ONE_roll_position_drives_EVERY_line_on_the_tier()
    {
        // Mod 1 tier 1 grants two lines from ONE stored position: stat 1 over 10 to 40 and stat 2 over 0
        // to 100. Position 32,768 is contracts 6.4's worked midpoint, so the first line is 25 and the
        // second is 50. Two numbers, one position, and neither is a second roll.
        var content = new InstanceStatLines(TwoLineWorld());
        Built built = Build(content, Affixed(new InstanceAffix(1, 1, 32_768)), wornSlot: 0);

        Assert.Equal(2, built.LineCount);
        Assert.Equal(25, built.Lines[0].Value);
        Assert.Equal(50, built.Lines[1].Value);
        Assert.Equal(Hottest, built.Lines[0].StatId);
        Assert.Equal(Second, built.Lines[1].StatId);

        // Both lines sit in ONE source, because the payload stores one position for the affix rather than
        // one per line, so the affix is what a source key names.
        Assert.Equal(1, built.SourceCount);
        Assert.Equal(2, built.Sources[0].LineCount);

        // Which is also the widest source this content version can produce from one affix, and it is what
        // a caller sizes its destination span from.
        Assert.Equal(2, content.MaxTierLineCount);
        Assert.Equal(0, content.MaxTierTagCount);
    }

    [Fact]
    public void A_two_line_tier_resolves_both_from_the_SAME_stored_position()
    {
        // The same tier at a different position moves BOTH lines together, which is the observable
        // difference between one stored position and two. Position 0 is the bottom of both ranges and
        // 65,535 is the top of both.
        var content = new InstanceStatLines(TwoLineWorld());

        Built bottom = Build(content, Affixed(new InstanceAffix(1, 1, RollPosition.Bottom)), wornSlot: 0);
        Assert.Equal(10, bottom.Lines[0].Value);
        Assert.Equal(0, bottom.Lines[1].Value);

        Built top = Build(content, Affixed(new InstanceAffix(1, 1, RollPosition.Top)), wornSlot: 0);
        Assert.Equal(40, top.Lines[0].Value);
        Assert.Equal(100, top.Lines[1].Value);

        // A quarter of the way up moves both, and neither moves independently of the other.
        Built quarter = Build(content, Affixed(new InstanceAffix(1, 1, 16_384)), wornSlot: 0);
        Assert.Equal(18, quarter.Lines[0].Value);
        Assert.Equal(25, quarter.Lines[1].Value);
    }

    [Fact]
    public void The_value_is_contracts_6_4s_formula_and_there_is_exactly_ONE_copy_of_it()
    {
        // First by REFERENCE: what the builder writes is what RollPosition.Resolve answers, at every
        // corner of the range and at a spread of interior positions, for both a rising and a falling one.
        var content = new InstanceStatLines(TwoLineWorld());
        foreach (ushort position in new ushort[] { 0, 1, 7, 16_384, 32_767, 32_768, 40_000, 65_534, 65_535 })
        {
            Built built = Build(content, Affixed(new InstanceAffix(1, 1, position)), wornSlot: 0);
            Assert.Equal(RollPosition.Resolve(position, 10, 40), built.Lines[0].Value);
            Assert.Equal(RollPosition.Resolve(position, 0, 100), built.Lines[1].Value);
        }

        // Then by SWEEP, because "equals the one copy" is only worth anything while there is one copy.
        // A second copy is an integer divide by 65,535 or a round-half addition of 32,767 anywhere in the
        // tree, and the ONLY file allowed to carry either is RollPosition.cs itself.
        var halfUp = new Regex(@"\+\s*32_?767(?![0-9])", RegexOptions.None, TimeSpan.FromSeconds(10));
        var divide = new Regex(
            @"/\s*(?:65_?535|ushort\.MaxValue|RollPosition\.Top)(?![0-9A-Za-z_])",
            RegexOptions.None,
            TimeSpan.FromSeconds(10));

        var copies = new List<string>();
        foreach (string file in SourceFiles(RepoRoot()))
        {
            if (string.Equals(Path.GetFileName(file), "RollPosition.cs", StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int index = 0; index < lines.Length; index++)
            {
                if (halfUp.IsMatch(lines[index]) || divide.IsMatch(lines[index]))
                {
                    copies.Add(Path.GetFileName(file) + ":" + (index + 1).ToString(CultureInfo.InvariantCulture) + " " + lines[index].Trim());
                }
            }
        }

        Assert.Empty(copies);
    }

    [Fact]
    public void sort_is_what_makes_the_tiers_second_line_a_stable_phrase_across_a_republish()
    {
        // The two lines of mod 1 tier 1 are authored with their sorts against the OPPOSITE row ids: row 2
        // sorts first and row 1 sorts second. A builder reading row id order would put stat 1 first.
        ContentTypeRegistry first = Registry();
        var content = new InstanceStatLines(Snapshot(
            first,
            StatRow(first, Hottest, "hottest"),
            StatRow(first, Second, "second"),
            ModRow(first, 1, "sharp"),
            TierRow(first, 1, "sharp_t1", modId: 1, ordinal: 1),
            LineRow(first, 1, "sharp_t1_b", tierId: 1, sort: 2, statId: Hottest, min: 10, max: 40),
            LineRow(first, 2, "sharp_t1_a", tierId: 1, sort: 1, statId: Second, min: 0, max: 100)));

        Built built = Build(content, Affixed(new InstanceAffix(1, 1, 32_768)), wornSlot: 0);
        Assert.Equal(Second, built.Lines[0].StatId);
        Assert.Equal(Hottest, built.Lines[1].StatId);

        // A REPUBLISH that re-ids both lines, keeping their sorts, keeps the phrase. The second line is
        // still stat 1, which is the property sort exists to hold.
        ContentTypeRegistry second = Registry();
        var republished = new InstanceStatLines(Snapshot(
            second,
            StatRow(second, Hottest, "hottest"),
            StatRow(second, Second, "second"),
            ModRow(second, 1, "sharp"),
            TierRow(second, 1, "sharp_t1", modId: 1, ordinal: 1),
            LineRow(second, 9, "sharp_t1_b", tierId: 1, sort: 2, statId: Hottest, min: 10, max: 40),
            LineRow(second, 4, "sharp_t1_a", tierId: 1, sort: 1, statId: Second, min: 0, max: 100)));

        Built after = Build(republished, Affixed(new InstanceAffix(1, 1, 32_768)), wornSlot: 0);
        Assert.Equal(Second, after.Lines[0].StatId);
        Assert.Equal(Hottest, after.Lines[1].StatId);
    }

    [Fact]
    public void Source_kind_1_is_the_worn_slot_index_ascending()
    {
        // Kind 1's ordinal is the worn slot itself, unpacked, so slot 0 and slot 1 ascend by one. It is
        // the one kind whose ordinal carries no index beside the slot, because a worn item has exactly one
        // set of base and implicit lines.
        Assert.Equal(0, InstanceStatSourceKind.WornItemOrdinal(0));
        Assert.Equal(1, InstanceStatSourceKind.WornItemOrdinal(1));
        Assert.Equal(10, InstanceStatSourceKind.WornItemOrdinal(10));

        // The builder EMITS none of them. Base and implicit lines come from the item DEFINITION and the
        // payload carries no field for them, so a payload carrying every kind this builder reads still
        // produces nothing at kind 1.
        var content = new InstanceStatLines(GemWorld());
        Built built = Build(content, SocketedPair(3, 4), wornSlot: 0);
        Assert.NotEqual(0, built.SourceCount);
        foreach (InstanceStatSource source in built.Sources)
        {
            Assert.NotEqual(StatSourceKey.WornItemKind, source.Key.SourceKind);
        }
    }

    [Fact]
    public void Source_kind_2_is_worn_slot_then_affix_index_in_the_SORTED_list()
    {
        // The affixes are handed to the encoder mod 2 first, and the encoder sorts ascending by mod id, so
        // the SORTED list is mod 1 then mod 2 and the affix index follows the stored order rather than the
        // order anything was rolled or crafted in.
        var content = new InstanceStatLines(TwoModWorld());
        byte[] payload = Affixed(new InstanceAffix(2, 1, RollPosition.Top), new InstanceAffix(1, 1, RollPosition.Bottom));

        Built built = Build(content, payload, wornSlot: 3);
        Assert.Equal(2, built.SourceCount);

        Assert.Equal(StatSourceKey.AffixKind, built.Sources[0].Key.SourceKind);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(3, 0), built.Sources[0].Key.Ordinal);
        Assert.Equal(StatSourceKey.AffixKind, built.Sources[1].Key.SourceKind);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(3, 1), built.Sources[1].Key.Ordinal);

        // Mod 1 is the Flat line at its range floor and mod 2 is the Increased line at its ceiling, so the
        // values say which affix landed at which index without reading the ordinal back.
        Assert.Equal(10, built.Lines[built.Sources[0].LineStart].Value);
        Assert.Equal(StatCombineKind.Flat, built.Lines[built.Sources[0].LineStart].Combine);
        Assert.Equal(5_000, built.Lines[built.Sources[1].LineStart].Value);
        Assert.Equal(StatCombineKind.Increased, built.Lines[built.Sources[1].LineStart].Combine);

        // An ENCHANTMENT list is the same entry layout at kind 3, ordinalled the same way.
        Built enchanted = Build(content, Enchanted(new InstanceAffix(1, 1, RollPosition.Top)), wornSlot: 3);
        Assert.Equal(StatSourceKey.EnchantmentKind, enchanted.Sources[0].Key.SourceKind);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(3, 0), enchanted.Sources[0].Key.Ordinal);
    }

    [Fact]
    public void Source_kind_4_is_worn_slot_then_socket_index_in_AUTHORED_order()
    {
        // Socket order is authored and NEVER sorted, unlike the affix list, so seating the higher mod id
        // first leaves it at socket index 0. A builder that sorted sockets would swap these two.
        var content = new InstanceStatLines(GemWorld());
        Built built = Build(content, SocketedPair(4, 3), wornSlot: 2);

        Assert.Equal(2, built.SourceCount);
        Assert.Equal(StatSourceKey.SocketedItemKind, built.Sources[0].Key.SourceKind);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(2, 0), built.Sources[0].Key.Ordinal);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(2, 1), built.Sources[1].Key.Ordinal);

        // Gem B is mod 4 at 100 basis points and gem A is mod 3 at 14,750, and the values say which gem
        // sits in which socket.
        Assert.Equal(100, built.Lines[built.Sources[0].LineStart].Value);
        Assert.Equal(14_750, built.Lines[built.Sources[1].LineStart].Value);

        // The contained item's own INSTANCE id rides into the key, so two gems of one mod in one item
        // still order rather than tying.
        Assert.Equal(4_001, built.Sources[0].Key.InstanceId);
        Assert.Equal(3_001, built.Sources[1].Key.InstanceId);
    }

    [Fact]
    public void A_player_who_rearranges_two_gems_can_move_a_displayed_value_by_ONE_unit()
    {
        // Base 20 with two More factors, 14,750 and 100 basis points. Folded in socket order 0 then 1:
        // more(20, 14750) is 50 and more(50, 100) is 51. The other way round: more(20, 100) is 20 and
        // more(20, 14750) is 50. One unit apart, and the socket the player dropped each gem into is what
        // decides which. That is correct and it is the price of integer rounding being honest.
        ContentSnapshot world = GemWorld();
        var content = new InstanceStatLines(world);

        Assert.Equal(51, Folded(world, content, SocketedPair(3, 4)));
        Assert.Equal(50, Folded(world, content, SocketedPair(4, 3)));
    }

    [Fact]
    public void Flat_is_in_the_stats_SCALED_UNITS_and_Increased_and_More_are_in_BASIS_POINTS()
    {
        // The three combine values of spec 8.4 map onto the three kinds of contracts 13.2 and the UNITS
        // follow the kind rather than the field. 5,000 authored under Increased is 50 percent and the same
        // 5,000 authored under Flat is five thousand scaled units.
        ContentTypeRegistry registry = Registry();
        var content = new InstanceStatLines(Snapshot(
            registry,
            StatRow(registry, Hottest, "hottest"),
            ModRow(registry, 1, "flat"),
            ModRow(registry, 2, "increased"),
            ModRow(registry, 3, "more"),
            TierRow(registry, 1, "flat_t1", modId: 1, ordinal: 1),
            TierRow(registry, 2, "increased_t1", modId: 2, ordinal: 1),
            TierRow(registry, 3, "more_t1", modId: 3, ordinal: 1),
            LineRow(registry, 1, "flat_l", tierId: 1, sort: 1, statId: Hottest, min: 5_000, max: 5_000),
            LineRow(
                registry,
                2,
                "increased_l",
                tierId: 2,
                sort: 1,
                statId: Hottest,
                combine: StatLineContentType.CombineIncreased,
                min: 5_000,
                max: 5_000),
            LineRow(
                registry,
                3,
                "more_l",
                tierId: 3,
                sort: 1,
                statId: Hottest,
                combine: StatLineContentType.CombineMore,
                min: 5_000,
                max: 5_000)));

        byte[] payload = Affixed(
            new InstanceAffix(1, 1, RollPosition.Top),
            new InstanceAffix(2, 1, RollPosition.Top),
            new InstanceAffix(3, 1, RollPosition.Top));
        Built built = Build(content, payload, wornSlot: 0);

        Assert.Equal(StatCombineKind.Flat, built.Lines[0].Combine);
        Assert.Equal(StatCombineKind.Increased, built.Lines[1].Combine);
        Assert.Equal(StatCombineKind.More, built.Lines[2].Combine);
        Assert.Equal(5_000, built.Lines[0].Value);
        Assert.Equal(5_000, built.Lines[1].Value);
        Assert.Equal(5_000, built.Lines[2].Value);

        // The evaluator is what reads the units, and the three fold apart: base 0 plus 5,000 flat is
        // 5,000 scaled units, then 50 percent increased takes it to 7,500, then 50 percent more takes it
        // to 11,250. A builder that wrote percent rather than basis points would answer 5,000 unchanged.
        ContentTypeRegistry alone = Registry();
        var evaluator = new ContentStatEvaluator(Snapshot(alone, StatRow(alone, Hottest, "hottest")));
        evaluator.SetBase(Hottest, 0);
        Add(evaluator, built);
        Assert.Equal(11_250, evaluator.Value(Hottest, new StatContext(Array.Empty<int>(), 0)));
    }

    [Fact]
    public void Build_writes_into_a_caller_span_and_allocates_nothing()
    {
        var content = new InstanceStatLines(GemWorld());
        byte[] payload = SocketedPair(3, 4);
        Span<StatModifierLine> lines = stackalloc StatModifierLine[32];
        Span<int> tags = stackalloc int[32];
        Span<InstanceStatSource> sources = stackalloc InstanceStatSource[8];

        // Warmed, so the first call's JIT work is not measured as the builder's.
        int warm = content.Build(payload, 0, 1, lines, tags, sources, out _, out _);
        Assert.Equal(2, warm);

        // The loop asserts NOTHING, because an assertion of its own allocates and would be measured as the
        // builder's. It accumulates instead, and the accumulator is checked once the measuring is over.
        int totalLines = 0;
        int totalTags = 0;
        int totalSources = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 32; iteration++)
        {
            totalLines += content.Build(payload, 0, 1, lines, tags, sources, out int tagCount, out int sourceCount);
            totalTags += tagCount;
            totalSources += sourceCount;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0L, allocated);
        Assert.Equal(64, totalLines);
        Assert.Equal(0, totalTags);
        Assert.Equal(64, totalSources);
    }

    [Fact]
    public void The_ordinal_packing_is_UNIQUE_per_worn_slot_for_kind_2_and_kind_4()
    {
        // The ordinal is the worn slot times the stride plus the entry index, so the last index of one
        // slot and the first of the next are adjacent and never equal. Without that, slot 1's first affix
        // and slot 0's second would fold as one source and one of them would vanish.
        Assert.Equal(InstanceStatSourceKind.MaxEntryIndex, InstanceStatSourceKind.EntryOrdinal(0, InstanceStatSourceKind.MaxEntryIndex));
        Assert.Equal(
            InstanceStatSourceKind.EntryOrdinal(0, InstanceStatSourceKind.MaxEntryIndex) + 1,
            InstanceStatSourceKind.EntryOrdinal(1, 0));

        var seen = new HashSet<int>();
        for (int slot = 0; slot <= 6; slot++)
        {
            for (int index = 0; index <= InstanceStatSourceKind.MaxEntryIndex; index++)
            {
                Assert.True(seen.Add(InstanceStatSourceKind.EntryOrdinal(slot, index)));
                Assert.Equal(slot, InstanceStatSourceKind.WornSlotOf(InstanceStatSourceKind.EntryOrdinal(slot, index)));
                Assert.Equal(index, InstanceStatSourceKind.EntryIndexOf(InstanceStatSourceKind.EntryOrdinal(slot, index)));
            }
        }

        // The affix count is a byte on the wire, so 255 is the widest list a payload can carry and the
        // stride is one more than that.
        Assert.Equal(byte.MaxValue, InstanceStatSourceKind.MaxEntryIndex);
    }

    [Fact]
    public void A_refusal_writes_NOTHING_and_never_throws_for_a_byte()
    {
        var content = new InstanceStatLines(TwoLineWorld());
        byte[] payload = Affixed(new InstanceAffix(1, 1, 32_768));
        Span<StatModifierLine> lines = stackalloc StatModifierLine[1];
        Span<int> tags = stackalloc int[8];
        Span<InstanceStatSource> sources = stackalloc InstanceStatSource[4];

        // The tier grants two lines and the destination holds one.
        Assert.Equal(
            InstanceStatLines.Refused,
            content.Build(payload, 0, 1, lines, tags, sources, out int tagCount, out int sourceCount));
        Assert.Equal(0, tagCount);
        Assert.Equal(0, sourceCount);

        // Bytes that do not decode are a refusal rather than a throw, because a payload arrives from a
        // stored page or a remote peer. Kind 131's body here declares three entries and carries none.
        Span<StatModifierLine> room = stackalloc StatModifierLine[8];
        byte[] malformed = new ItemInstancePayloadBuilder()
            .Add(InstancePropertyKind.Affixes, new byte[] { 3 })
            .ToArray();
        Assert.Equal(InstanceStatLines.Refused, content.Build(malformed, 0, 1, room, tags, sources, out _, out _));

        // A worn slot outside the packing is a caller error and is refused the same way.
        Assert.Equal(InstanceStatLines.Refused, content.Build(payload, -1, 1, room, tags, sources, out _, out _));
    }

    [Fact]
    public void A_GATED_kind_is_emitted_because_this_builder_is_NOT_a_visibility_boundary()
    {
        // Kinds 131 and 133 are both identification gated, and an UNIDENTIFIED item still folds every one
        // of their lines here. The evaluator runs server side over the true payload, so hiding a line from
        // it would make the item weaker rather than mysterious. A tooltip wanting the player's view asks
        // ItemInstanceVisibility.PublicView first and builds from the projection.
        var content = new InstanceStatLines(TwoModWorld());
        byte[] unidentified = new ItemInstancePayloadBuilder()
            .AddIdentification(identified: false, revealedMask: 0)
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(1, 1, RollPosition.Bottom) })
            .AddAffixes(InstancePropertyKind.Enchantments, new[] { new InstanceAffix(2, 1, RollPosition.Top) })
            .ToArray();

        Built built = Build(content, unidentified, wornSlot: 0);
        Assert.Equal(2, built.SourceCount);
        Assert.Equal(StatSourceKey.AffixKind, built.Sources[0].Key.SourceKind);
        Assert.Equal(StatSourceKey.EnchantmentKind, built.Sources[1].Key.SourceKind);

        // The projection a viewer gets is the OTHER answer, and it is a different function: with neither
        // bit revealed it drops both gated kinds, so a tooltip built from it sees no line at all.
        InstancePropertyRegistry properties = InstancePropertyRegistry.CreateV1();
        Span<byte> view = stackalloc byte[unidentified.Length];
        int viewLength = ItemInstancePayload.PublicView(
            properties, unidentified, PropertyVisibility.OwnerOnly, identified: false, revealedMask: 0, view);
        Assert.True(viewLength >= 0);
        Built projected = Build(content, view[..viewLength].ToArray(), wornSlot: 0);
        Assert.Equal(0, projected.SourceCount);
    }

    [Fact]
    public void A_socket_recurses_exactly_ONE_level()
    {
        // The gem in socket 0 carries its own affix, which is emitted, and a socket field of its own,
        // which is not. Contracts 9.5 caps nesting at one level and this builder stops at the same place
        // rather than one deeper, so a payload that somehow carries a nested socket contributes nothing
        // extra rather than opening a recursion a 45 byte payload can drive.
        var content = new InstanceStatLines(GemWorld());
        byte[] inner = new ItemInstancePayloadBuilder()
            .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(3, 1, RollPosition.Top) })
            .AddSockets(new[]
            {
                new InstanceSocket(0, GemDefinition, 9_001, new ItemInstancePayloadBuilder()
                    .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(4, 1, RollPosition.Top) })
                    .ToArray()),
            })
            .ToArray();
        byte[] payload = new ItemInstancePayloadBuilder()
            .AddSockets(new[] { new InstanceSocket(0, GemDefinition, 3_001, inner) })
            .ToArray();

        Built built = Build(content, payload, wornSlot: 0);
        Assert.Equal(1, built.SourceCount);
        Assert.Equal(1, built.LineCount);
        Assert.Equal(14_750, built.Lines[0].Value);
    }

    [Fact]
    public void A_tier_this_version_no_longer_carries_contributes_NO_line_and_stops_nothing()
    {
        // Mod 1 tier 1 is authored and mod 1 tier 9 is not, so the second affix resolves to no row. It
        // contributes no line and no source, the affix beside it still builds, and the payload is not
        // refused: a stored item naming content a later version dropped is ordinary rather than a defect
        // this read path gets to have an opinion about.
        var content = new InstanceStatLines(TwoModWorld());
        byte[] payload = Affixed(
            new InstanceAffix(1, 1, RollPosition.Bottom),
            new InstanceAffix(2, 9, RollPosition.Top));

        Built built = Build(content, payload, wornSlot: 0);
        Assert.Equal(1, built.SourceCount);
        Assert.Equal(1, built.LineCount);
        Assert.Equal(10, built.Lines[0].Value);
        Assert.Equal(InstanceStatSourceKind.EntryOrdinal(0, 0), built.Sources[0].Key.Ordinal);
    }

    [Fact]
    public void A_lines_TAG_SCOPE_and_CONDITION_ride_into_the_source_and_the_tags_are_SHARED()
    {
        // A tier whose two lines both carry the fire tag writes ONE tag run per line into the source's own
        // slice, and the source hands that slice to AddSource as a unit. The positions the builder writes
        // are relative to the SOURCE, which is what AddSource rebases, so line 1 starts at 0 and line 2 at
        // 1 rather than at wherever the shared array happened to be.
        ContentTypeRegistry registry = Registry();
        var content = new InstanceStatLines(Snapshot(
            registry,
            TagRow(registry, FireTag, "fire"),
            StatRow(registry, Hottest, "hottest"),
            StatRow(registry, Second, "second"),
            ModRow(registry, 1, "sharp"),
            TierRow(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            LineRow(
                registry,
                1,
                "sharp_a",
                tierId: 1,
                sort: 1,
                statId: Hottest,
                min: 10,
                max: 40,
                tagScope: new[] { FireTag }),
            LineRow(
                registry,
                2,
                "sharp_b",
                tierId: 1,
                sort: 2,
                statId: Second,
                min: 0,
                max: 100,
                tagScope: new[] { FireTag },
                conditionId: 4_096)));

        Built built = Build(content, Affixed(new InstanceAffix(1, 1, RollPosition.Top)), wornSlot: 0);
        Assert.Equal(2, built.TagCount);
        Assert.Equal(new[] { FireTag, FireTag }, built.Tags);

        Assert.Equal(0, built.Lines[0].TagScopeStart);
        Assert.Equal(1, built.Lines[0].TagScopeLength);
        Assert.Equal(1, built.Lines[1].TagScopeStart);
        Assert.Equal(1, built.Lines[1].TagScopeLength);

        Assert.Equal(IStatConditionRegistry.Unconditional, built.Lines[0].ConditionId);
        Assert.Equal(4_096, built.Lines[1].ConditionId);

        Assert.Equal(0, built.Sources[0].TagStart);
        Assert.Equal(2, built.Sources[0].TagCount);
        Assert.Equal(2, content.MaxTierTagCount);
    }

    /// <summary>Everything one <c>Build</c> wrote, copied out so a fact can assert against arrays.</summary>
    sealed record Built(
        int LineCount,
        int TagCount,
        int SourceCount,
        StatModifierLine[] Lines,
        int[] Tags,
        InstanceStatSource[] Sources);

    /// <summary>One build into spans wide enough for every fact here, copied out.</summary>
    static Built Build(InstanceStatLines content, byte[] payload, int wornSlot)
    {
        Span<StatModifierLine> lines = stackalloc StatModifierLine[64];
        Span<int> tags = stackalloc int[64];
        Span<InstanceStatSource> sources = stackalloc InstanceStatSource[32];
        int written = content.Build(payload, wornSlot, 1, lines, tags, sources, out int tagCount, out int sourceCount);
        Assert.NotEqual(InstanceStatLines.Refused, written);
        return new Built(
            written,
            tagCount,
            sourceCount,
            lines[..written].ToArray(),
            tags[..tagCount].ToArray(),
            sources[..sourceCount].ToArray());
    }

    /// <summary>Hands every built source to the evaluator the way spec 11.4 says a caller does.</summary>
    static void Add(ContentStatEvaluator evaluator, Built built)
    {
        foreach (InstanceStatSource source in built.Sources)
        {
            evaluator.AddSource(
                source.Key,
                built.Lines.AsSpan(source.LineStart, source.LineCount),
                built.Tags.AsSpan(source.TagStart, source.TagCount));
        }
    }

    /// <summary>The folded value of stat 1 over base 20, which is where the one unit shows up.</summary>
    static int Folded(ContentSnapshot world, InstanceStatLines content, byte[] payload)
    {
        var evaluator = new ContentStatEvaluator(world);
        evaluator.SetBase(Hottest, 20);
        Add(evaluator, Build(content, payload, wornSlot: 0));
        return evaluator.Value(Hottest, new StatContext(Array.Empty<int>(), 0));
    }

    static byte[] Affixed(params InstanceAffix[] affixes)
        => new ItemInstancePayloadBuilder().AddAffixes(InstancePropertyKind.Affixes, affixes).ToArray();

    static byte[] Enchanted(params InstanceAffix[] affixes)
        => new ItemInstancePayloadBuilder().AddAffixes(InstancePropertyKind.Enchantments, affixes).ToArray();

    /// <summary>Two sockets in AUTHORED order, each holding a gem whose only affix is that mod.</summary>
    static byte[] SocketedPair(int firstMod, int secondMod)
        => new ItemInstancePayloadBuilder()
            .AddSockets(new[] { Gem(firstMod), Gem(secondMod) })
            .ToArray();

    static InstanceSocket Gem(int modId)
        => new(
            0,
            GemDefinition,
            (ulong)((modId * 1_000) + 1),
            new ItemInstancePayloadBuilder()
                .AddAffixes(InstancePropertyKind.Affixes, new[] { new InstanceAffix(modId, 1, RollPosition.Top) })
                .ToArray());

    /// <summary>Mod 1 tier 1 grants TWO lines, stat 1 over 10 to 40 and stat 2 over 0 to 100.</summary>
    static ContentSnapshot TwoLineWorld()
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(
            registry,
            StatRow(registry, Hottest, "hottest"),
            StatRow(registry, Second, "second"),
            ModRow(registry, 1, "sharp"),
            TierRow(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            LineRow(registry, 1, "sharp_a", tierId: 1, sort: 1, statId: Hottest, min: 10, max: 40),
            LineRow(registry, 2, "sharp_b", tierId: 1, sort: 2, statId: Second, min: 0, max: 100));
    }

    /// <summary>Mod 1 is a Flat line over 10 to 40 and mod 2 an Increased line fixed at 5,000.</summary>
    static ContentSnapshot TwoModWorld()
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(
            registry,
            StatRow(registry, Hottest, "hottest"),
            ModRow(registry, 1, "sharp"),
            ModRow(registry, 2, "keen"),
            TierRow(registry, 1, "sharp_t1", modId: 1, ordinal: 1),
            TierRow(registry, 2, "keen_t1", modId: 2, ordinal: 1),
            LineRow(registry, 1, "sharp_a", tierId: 1, sort: 1, statId: Hottest, min: 10, max: 40),
            LineRow(
                registry,
                2,
                "keen_a",
                tierId: 2,
                sort: 1,
                statId: Hottest,
                combine: StatLineContentType.CombineIncreased,
                min: 5_000,
                max: 5_000));
    }

    /// <summary>Mod 3 is a More factor of 14,750 basis points and mod 4 one of 100.</summary>
    static ContentSnapshot GemWorld()
    {
        ContentTypeRegistry registry = Registry();
        return Snapshot(
            registry,
            StatRow(registry, Hottest, "hottest"),
            ModRow(registry, 3, "gem_a"),
            ModRow(registry, 4, "gem_b"),
            TierRow(registry, 3, "gem_a_t1", modId: 3, ordinal: 1),
            TierRow(registry, 4, "gem_b_t1", modId: 4, ordinal: 1),
            LineRow(
                registry,
                3,
                "gem_a_l",
                tierId: 3,
                sort: 1,
                statId: Hottest,
                combine: StatLineContentType.CombineMore,
                min: 14_750,
                max: 14_750),
            LineRow(
                registry,
                4,
                "gem_b_l",
                tierId: 4,
                sort: 1,
                statId: Hottest,
                combine: StatLineContentType.CombineMore,
                min: 100,
                max: 100));
    }

    static ContentRow TagRow(ContentTypeRegistry registry, int id, string key)
        => RowAt(
            Lookup(registry, EngineContentTypes.TagTypeKey),
            id,
            key,
            Marker(),
            ContentFieldValue.Absent(ContentFieldKind.Int));

    /// <summary>A stat wide enough that no fact here is reading a clamp instead of a fold.</summary>
    static ContentRow StatRow(ContentTypeRegistry registry, int id, string key)
        => RowAt(
            Lookup(registry, EngineContentTypes.StatTypeKey),
            id,
            key,
            Marker(),
            Int(1),
            Int(-1_000_000),
            Int(1_000_000),
            ContentFieldValue.Absent(ContentFieldKind.TagList),
            Marker());

    static ContentRow ModRow(ContentTypeRegistry registry, int id, string key) => Mod(registry, id, key);

    static ContentRow TierRow(ContentTypeRegistry registry, int id, string key, int modId, int ordinal)
        => ModTier(registry, id, key, modId, ordinal);

    static ContentRow LineRow(
        ContentTypeRegistry registry,
        int id,
        string key,
        int tierId,
        int sort,
        int statId,
        int combine = StatLineContentType.CombineFlat,
        int min = 0,
        int max = 0,
        IReadOnlyList<int>? tagScope = null,
        int conditionId = 0)
        => RowAt(
            Lookup(registry, InstanceContentTypeIds.StatLineTypeKey),
            id,
            key,
            Reference(tierId),
            Int(sort),
            Reference(statId),
            Int(combine),
            Int(min),
            Int(max),
            tagScope is null
                ? ContentFieldValue.Absent(ContentFieldKind.TagList)
                : ContentRowCodecBase.TagListValue(tagScope),
            conditionId == 0 ? ContentFieldValue.Absent(ContentFieldKind.Int) : Int(conditionId));

    static string RepoRoot([CallerFilePath] string thisFile = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;

    /// <summary>Every tracked source file, skipping build output and every dot directory.</summary>
    static IEnumerable<string> SourceFiles(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            string name = Path.GetFileName(directory);
            if (name.StartsWith('.') || string.Equals(name, "bin", StringComparison.Ordinal)
                || string.Equals(name, "obj", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string file in SourceFiles(directory))
            {
                yield return file;
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }
    }
}
