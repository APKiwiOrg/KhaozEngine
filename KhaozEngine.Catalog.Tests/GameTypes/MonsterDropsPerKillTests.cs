using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.GameTypes;
using Xunit;
using static KhaozEngine.Tests.Catalog.GameTypes.GameLootFixtures;
using static KhaozEngine.Tests.Catalog.GameTypes.GameTypeValidationFixtures;

namespace KhaozEngine.Tests.Catalog.GameTypes;

/// <summary>
/// The drops-per-kill rules, <c>KGT1316</c> and <c>KGT1317</c>: a monster whose drop tree can leave more
/// lines off one kill than the knob allows is refused at publish.
/// </summary>
/// <remarks>
/// <b>The knob is what makes the rule shippable at all.</b> A publish-time rule with no knob would be
/// RETROACTIVE: the sweep judges the whole candidate, so it would refuse the seed of every older baseline and
/// every intermediate version an upgrade chain publishes on its way to the fix. The rule is enforced only on
/// a catalog that DECLARES it, and absence means the catalog predates the rule.
/// </remarks>
public class MonsterDropsPerKillTests
{
    /// <summary>
    /// Four drop trees, three of which can leave two lines and one of which can leave one, plus a retired
    /// rule that rolls nothing at all.
    /// </summary>
    [Fact]
    public void AMonsterOverTheDropsPerKillKnobIsRefusedByTheCrossTypeSweep()
    {
        (ContentTypeRegistry registry, ContentSnapshot candidate) = DropTrees(allowed: 1);

        // ONE finding per offending MONSTER, not per table and not per entry. The fourth tree passes and the
        // fifth is retired, so neither reports.
        Assert.Equal(
            new[]
            {
                (1, GameContentFindings.SweepMonsterDropsMoreThanOne),
                (2, GameContentFindings.SweepMonsterDropsMoreThanOne),
                (3, GameContentFindings.SweepMonsterDropsMoreThanOne),
            },
            Sweep(registry, candidate));

        // Each message names the monster drop row, the maximum its tree computes to, the allowed one, and
        // the TABLE the lines come from. The third tree's root is a single innocent pick, and what an author
        // has to edit is the table below it.
        string[] messages = SweepFindings(registry, candidate).Select(f => f.Message).ToArray();
        Assert.Contains(
            "'two_picks' rolls a tree that can leave 2 lines off one kill, over the "
            + $"'{DropsKnob}' knob at 1. The lines come from table 'two_picks'.",
            messages[0],
            StringComparison.Ordinal);
        Assert.Contains(
            "'bread_and_purse' rolls a tree that can leave 2 lines off one kill, over the "
            + $"'{DropsKnob}' knob at 1. The lines come from table 'bread_and_purse'.",
            messages[1],
            StringComparison.Ordinal);
        Assert.Contains(
            "'one_pick' rolls a tree that can leave 2 lines off one kill, over the "
            + $"'{DropsKnob}' knob at 1. The lines come from table 'nested_two'.",
            messages[2],
            StringComparison.Ordinal);
        Assert.All(messages, m => Assert.EndsWith("A kill would drop more than one item.", m, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE BLAME PASSES DOWN, one level at a time, to the deepest table whose own structure produced the
    /// count. A root that draws a table that draws a table is innocent of everything but the draw.
    /// </summary>
    [Fact]
    public void TheFindingNamesTheDeepestTableThatProducedTheLines()
    {
        ContentFinding only = Assert.Single(PerKill(OneTree("three_deep", allowed: 1)));
        Assert.Contains("The lines come from table 'bottom'.", only.Message, StringComparison.Ordinal);
        Assert.Contains("can leave 2 lines off one kill", only.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The blame STOPS where the roll_count is doing the multiplying. A table that picks three times from a
    /// table worth two lines is six lines, and naming the nested table would send an author to edit a table
    /// worth two while the six went on being six.
    /// </summary>
    [Fact]
    public void AMultiplyingRollCountKeepsTheBlameOnTheTableThatMultiplied()
    {
        ContentFinding only = Assert.Single(PerKill(OneTree("picks_of_two", allowed: 1)));
        Assert.Contains("can leave 6 lines off one kill", only.Message, StringComparison.Ordinal);
        Assert.Contains("The lines come from table 'picks_of_two'.", only.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// AUTHORED NUMBERS CANNOT OVERFLOW THEIR WAY PAST THE RULE. Two tables of four billion picks nested one
    /// inside the other make a product past <see cref="long.MaxValue"/>, and two guaranteed entries drawing
    /// that table add two saturated maxima, so both operations would wrap negative unchecked.
    /// </summary>
    [Fact]
    public void ADropTreeThatOverflowsALongIsStillRefused()
    {
        ContentFinding only = Assert.Single(PerKill(OneTree("overflows_long", allowed: 1)));
        Assert.Equal(GameContentFindings.SweepMonsterDropsMoreThanOne, only.Code);
        Assert.Contains(
            FormattableString.Invariant($"can leave {long.MaxValue} lines off one kill"),
            only.Message,
            StringComparison.Ordinal);

        // The same tree under an absurd knob is still refused, because a saturated maximum is above every
        // whole number a knob can hold bar the maximum itself.
        Assert.Single(PerKill(OneTree("overflows_long", allowed: int.MaxValue)));
    }

    /// <summary>
    /// The rule is a MAXIMUM compared against the knob rather than a shape, so raising the knob lets the
    /// same content through.
    /// </summary>
    [Fact]
    public void RaisingTheDropsPerKillKnobLetsTheSameTreesThrough()
    {
        (ContentTypeRegistry two, ContentSnapshot underTwo) = DropTrees(allowed: 2);
        Assert.Empty(Sweep(two, underTwo));

        (ContentTypeRegistry one, ContentSnapshot underOne) = DropTrees(allowed: 1);
        Assert.Equal(3, Sweep(one, underOne).Length);
    }

    /// <summary>
    /// The knob's ABSENCE is the whole of how this rule ships: a catalog that predates it is judged by
    /// nothing.
    /// </summary>
    [Fact]
    public void ACatalogWithNoDropsPerKillKnobIsJudgedByNothing()
    {
        (ContentTypeRegistry registry, ContentSnapshot candidate) = DropTrees(allowed: null);
        Assert.Empty(Sweep(registry, candidate));
    }

    /// <summary>
    /// A game that names no knob has no rule, whatever the candidate carries: a null name disables both
    /// codes, exactly as the other knob-driven rules are disabled.
    /// </summary>
    [Fact]
    public void ANullDropsPerKillKnobNameDisablesBothRules()
    {
        (ContentTypeRegistry over, ContentSnapshot overCandidate) = DropTrees(allowed: 1);
        Assert.Empty(Sweep(over, overCandidate, GameContentSweepOptions.None));

        (ContentTypeRegistry unusable, ContentSnapshot unusableCandidate) = DropTrees(allowed: null, scaledKnob: 0);
        Assert.Empty(Sweep(unusable, unusableCandidate, GameContentSweepOptions.None));
    }

    /// <summary>
    /// A row that is PRESENT and unusable is the opposite of an absent one: somebody typed it. Zero drops
    /// allowed is an authoring mistake and a fraction of a drop is a number no kill can be held against.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(150)]
    [InlineData(-100)]
    public void AnUnusableDropsPerKillRowIsItsOwnRefusalAndNoTreeIsJudged(int stored)
    {
        (ContentTypeRegistry registry, ContentSnapshot candidate) = DropTrees(allowed: null, scaledKnob: stored);
        Assert.Equal(new[] { (0, GameContentFindings.SweepMaxDropsPerKillUnusable) }, Sweep(registry, candidate));
    }

    /// <summary>
    /// The arithmetic itself, one shape at a time: a table's most lines is every live guaranteed entry's own
    /// most, plus <c>roll_count</c> times the widest single weighted entry.
    /// </summary>
    [Theory]
    [InlineData("guaranteed_and_pick", 2)]
    [InlineData("two_picks", 2)]
    [InlineData("pick_into_two", 2)]
    [InlineData("widest_pick_wins", 2)]
    [InlineData("never_fires_and_pick", 1)]
    [InlineData("lone_certainty", 1)]
    [InlineData("below_the_depth_cap", 1)]
    [InlineData("at_the_depth_cap", 2)]
    public void TheDropsPerKillMaximumIsComputedShapeByShape(string tree, int most)
    {
        // A tree at its own maximum passes, which is what says the number is EXACT rather than merely large
        // enough to report. Read through THIS rule's codes alone: the never-fires shape is also a guaranteed
        // entry at no chance, which is KGT1315 and is a different statement about the same row.
        Assert.Empty(PerKill(OneTree(tree, most)));

        if (most == 1)
        {
            // A knob below one is its own refusal, so one line under a knob of one is the whole statement.
            return;
        }

        ContentFinding only = Assert.Single(PerKill(OneTree(tree, most - 1)));
        Assert.Equal(GameContentFindings.SweepMonsterDropsMoreThanOne, only.Code);
        Assert.Contains(
            FormattableString.Invariant($"can leave {most} lines off one kill"),
            only.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A single weighted pick over four entries under a knob of one sweeps clean. A rule that refused this
    /// would be refusing the ordinary drop table.
    /// </summary>
    [Fact]
    public void AnOrdinarySinglePickUnderAKnobOfOneSweepsClean()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);
        ContentTypeRegistration drop = Registration(registry, GameContentTypeIds.MonsterDrop);
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);

        ContentSnapshot candidate = Snapshot(
            registry,
            [
                LootTable(table, 1, "pouch_drops", rollCount: 1),
                LootEntry(entry, 1, "pouch_bread", table: 1, weight: 45, guaranteed: false, chance: 10_000, item: 11),
                LootEntry(entry, 2, "pouch_coins_2", table: 1, weight: 66, guaranteed: false, chance: 10_000, item: 12),
                LootEntry(entry, 3, "pouch_coins_4", table: 1, weight: 65, guaranteed: false, chance: 10_000, item: 12),
                LootEntry(entry, 8, "pouch_belt", table: 1, weight: 4, guaranteed: false, chance: 10_000, item: 13),
                Drop(drop, 1, "raider", kind: 1, table: 1),
                .. Knob(tuning, GameTuningContentType.ValueScale),
            ]);

        Assert.Empty(Sweep(registry, candidate));
    }

    /// <summary>
    /// A guaranteed entry on its own chance, a pick, and a guaranteed draw of a rare table is THREE under the
    /// knob, and the same rows with no knob row publish clean.
    /// </summary>
    [Fact]
    public void AThreeDrawTableScoresThreeUnderTheKnob()
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);
        ContentTypeRegistration drop = Registration(registry, GameContentTypeIds.MonsterDrop);
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);

        ContentRow[] rows =
        [
            LootTable(table, 1, "raider_drops", rollCount: 1),
            LootTable(table, 3, "nothing", rollCount: 0),
            LootTable(table, 4, "raider_rare", rollCount: 1),
            LootEntry(entry, 1, "raider_bread", table: 1, weight: 0, guaranteed: true, chance: 2_500, item: 11),
            LootEntry(entry, 2, "raider_coins_2", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 12),
            LootEntry(entry, 3, "raider_coins_4", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 12),
            LootEntry(entry, 5, "raider_rare_belt", table: 4, weight: 1, guaranteed: false, chance: 10_000, item: 13),
            LootEntry(entry, 6, "raider_rare_nothing", table: 4, weight: 44, guaranteed: false, chance: 10_000, nested: 3),
            LootEntry(entry, 7, "raider_rare_roll", table: 1, weight: 0, guaranteed: true, chance: 10_000, nested: 4),
            Drop(drop, 1, "raider", kind: 1, table: 1),
        ];

        // The bread is one, the rare roll's nested table is one, and the table's own pick is one: three.
        ContentFinding only = Assert.Single(
            SweepFindings(registry, Snapshot(registry, [.. rows, .. Knob(tuning, GameTuningContentType.ValueScale)])));
        Assert.Equal(GameContentFindings.SweepMonsterDropsMoreThanOne, only.Code);
        Assert.Contains("can leave 3 lines off one kill", only.Message, StringComparison.Ordinal);

        Assert.Empty(Sweep(registry, Snapshot(registry, [.. rows, .. Knob(tuning, null)])));
    }

    /// <summary>The findings of THIS rule alone, which is the pair of codes it owns.</summary>
    static ContentFinding[] PerKill((ContentTypeRegistry Registry, ContentSnapshot Candidate) tree) =>
    [
        .. SweepFindings(tree.Registry, tree.Candidate).Where(f =>
            string.Equals(f.Code, GameContentFindings.SweepMonsterDropsMoreThanOne, StringComparison.Ordinal)
            || string.Equals(f.Code, GameContentFindings.SweepMaxDropsPerKillUnusable, StringComparison.Ordinal)),
    ];

    /// <summary>Four drop trees under a knob, or under none.</summary>
    /// <param name="allowed">The whole number of drops the knob allows, or null for no knob row.</param>
    /// <param name="scaledKnob">A knob written at this STORED value instead, for the unusable shapes.</param>
    static (ContentTypeRegistry, ContentSnapshot) DropTrees(int? allowed, int? scaledKnob = null)
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);
        ContentTypeRegistration drop = Registration(registry, GameContentTypeIds.MonsterDrop);
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);

        var rows = new List<ContentRow>
        {
            // Two picks over one pool, which is two lines whatever the pool holds.
            LootTable(table, 1, "two_picks", rollCount: 2),
            LootEntry(entry, 1, "two_picks_coin", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 11),

            // A guaranteed entry beside a weighted pool.
            LootTable(table, 2, "bread_and_purse", rollCount: 1),
            LootEntry(entry, 2, "bread", table: 2, weight: 0, guaranteed: true, chance: 2_500, item: 11),
            LootEntry(entry, 3, "purse", table: 2, weight: 1, guaranteed: false, chance: 10_000, item: 12),

            // One pick into a table that is itself two lines. The root table alone says nothing about it.
            LootTable(table, 3, "one_pick", rollCount: 1),
            LootEntry(entry, 4, "one_pick_nest", table: 3, weight: 1, guaranteed: false, chance: 10_000, nested: 4),
            LootTable(table, 4, "nested_two", rollCount: 1),
            LootEntry(entry, 5, "nested_certainty", table: 4, weight: 0, guaranteed: true, chance: 10_000, item: 11),
            LootEntry(entry, 6, "nested_pick", table: 4, weight: 1, guaranteed: false, chance: 10_000, item: 12),

            // The legal shape: one pick over a pool, one branch of which nests into another single pick.
            LootTable(table, 5, "one_thing", rollCount: 1),
            LootEntry(entry, 7, "one_thing_item", table: 5, weight: 1, guaranteed: false, chance: 10_000, item: 11),
            LootEntry(entry, 8, "one_thing_nest", table: 5, weight: 1, guaranteed: false, chance: 10_000, nested: 6),
            LootTable(table, 6, "one_thing_below", rollCount: 1),
            LootEntry(entry, 9, "below_item", table: 6, weight: 1, guaranteed: false, chance: 10_000, item: 12),

            Drop(drop, 1, "two_picks", kind: 1, table: 1),
            Drop(drop, 2, "bread_and_purse", kind: 2, table: 2),
            Drop(drop, 3, "one_pick", kind: 3, table: 3),
            Drop(drop, 4, "one_thing", kind: 4, table: 5),

            // A withdrawn rule rolls nothing, so a retired row is skipped exactly as the roller skips it.
            Drop(drop, 5, "retired_two_picks", kind: 6, table: 1, retired: true),
        };

        rows.AddRange(Knob(
            tuning,
            scaledKnob ?? (allowed is int whole ? whole * GameTuningContentType.ValueScale : null)));
        return (registry, Snapshot(registry, rows.ToArray()));
    }

    /// <summary>ONE named drop tree under a knob at <paramref name="allowed"/> whole drops.</summary>
    static (ContentTypeRegistry, ContentSnapshot) OneTree(string tree, int allowed)
    {
        ContentTypeRegistry registry = TypeRegistry();
        ContentTypeRegistration table = Registration(registry, EngineContentTypes.LootTableTypeId);
        ContentTypeRegistration entry = Registration(registry, EngineContentTypes.LootEntryTypeId);
        ContentTypeRegistration drop = Registration(registry, GameContentTypeIds.MonsterDrop);
        ContentTypeRegistration tuning = Registration(registry, GameContentTypeIds.GameTuning);

        var rows = new List<ContentRow> { Drop(drop, 1, tree, kind: 1, table: 1) };
        rows.AddRange(Knob(tuning, allowed * GameTuningContentType.ValueScale));
        rows.AddRange(TreeRows(tree, table, entry));
        return (registry, Snapshot(registry, rows.ToArray()));
    }

    static IEnumerable<ContentRow> TreeRows(string tree, ContentTypeRegistration table, ContentTypeRegistration entry)
    {
        switch (tree)
        {
            case "guaranteed_and_pick":
                yield return LootTable(table, 1, tree, rollCount: 1);
                yield return LootEntry(entry, 1, "kept", table: 1, weight: 0, guaranteed: true, chance: 2_500, item: 11);
                yield return LootEntry(entry, 2, "picked", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "two_picks":
                yield return LootTable(table, 1, tree, rollCount: 2);
                yield return LootEntry(entry, 1, "picked", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 11);
                break;
            case "pick_into_two":
                yield return LootTable(table, 1, tree, rollCount: 1);
                yield return LootEntry(entry, 1, "nests", table: 1, weight: 1, guaranteed: false, chance: 10_000, nested: 2);
                yield return LootTable(table, 2, "below", rollCount: 1);
                yield return LootEntry(entry, 2, "below_kept", table: 2, weight: 0, guaranteed: true, chance: 10_000, item: 11);
                yield return LootEntry(entry, 3, "below_picked", table: 2, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "widest_pick_wins":
                yield return LootTable(table, 1, tree, rollCount: 1);
                yield return LootEntry(entry, 1, "plain", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 11);
                yield return LootEntry(entry, 2, "nests", table: 1, weight: 1, guaranteed: false, chance: 10_000, nested: 2);
                yield return LootTable(table, 2, "below", rollCount: 2);
                yield return LootEntry(entry, 3, "below_picked", table: 2, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "never_fires_and_pick":
                yield return LootTable(table, 1, tree, rollCount: 1);
                yield return LootEntry(entry, 1, "never", table: 1, weight: 0, guaranteed: true, chance: 0, item: 11);
                yield return LootEntry(entry, 2, "picked", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "lone_certainty":
                // One guaranteed certainty, no pool and no pick at all, which has to pass.
                yield return LootTable(table, 1, tree, rollCount: 0);
                yield return LootEntry(entry, 1, "carcass", table: 1, weight: 0, guaranteed: true, chance: 10_000, item: 11);
                break;
            case "three_deep":
                // Only the bottom table's own structure produces a second line. The two above it draw.
                yield return LootTable(table, 1, tree, rollCount: 1);
                yield return LootEntry(entry, 1, "to_middle", table: 1, weight: 1, guaranteed: false, chance: 10_000, nested: 2);
                yield return LootTable(table, 2, "middle", rollCount: 1);
                yield return LootEntry(entry, 2, "to_bottom", table: 2, weight: 1, guaranteed: false, chance: 10_000, nested: 3);
                yield return LootTable(table, 3, "bottom", rollCount: 1);
                yield return LootEntry(entry, 3, "bottom_kept", table: 3, weight: 0, guaranteed: true, chance: 10_000, item: 11);
                yield return LootEntry(entry, 4, "bottom_picked", table: 3, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "picks_of_two":
                // Three picks of a table worth two lines: SIX, and the multiplying is this table's own
                // roll_count.
                yield return LootTable(table, 1, tree, rollCount: 3);
                yield return LootEntry(entry, 1, "nests", table: 1, weight: 1, guaranteed: false, chance: 10_000, nested: 2);
                yield return LootTable(table, 2, "below", rollCount: 1);
                yield return LootEntry(entry, 2, "below_kept", table: 2, weight: 0, guaranteed: true, chance: 10_000, item: 11);
                yield return LootEntry(entry, 3, "below_picked", table: 2, weight: 1, guaranteed: false, chance: 10_000, item: 12);
                break;
            case "overflows_long":
                // Two guaranteed entries drawing ONE table whose own product already saturates, so the sum
                // and the multiply are both exercised and both would wrap negative unchecked.
                yield return LootTable(table, 1, tree, rollCount: 0);
                yield return LootEntry(entry, 1, "left", table: 1, weight: 0, guaranteed: true, chance: 10_000, nested: 2);
                yield return LootEntry(entry, 2, "right", table: 1, weight: 0, guaranteed: true, chance: 10_000, nested: 2);
                yield return LootTable(table, 2, "four_billion_picks", rollCount: 4_000_000_000L);
                yield return LootEntry(entry, 3, "to_floor", table: 2, weight: 1, guaranteed: false, chance: 10_000, nested: 3);
                yield return LootTable(table, 3, "four_billion_more", rollCount: 4_000_000_000L);
                yield return LootEntry(entry, 4, "floor_item", table: 3, weight: 1, guaranteed: false, chance: 10_000, item: 11);
                break;
            case "at_the_depth_cap":
                // The same chain ending ONE level shallower, so its two lines sit in the deepest table a roll
                // still reaches (table ids here are depth plus one). A walk that stopped a level early would
                // score this tree 1 like the one below the cap.
                foreach (ContentRow row in DepthChain(tree, table, entry, LootRoller.MaxNestedDepth + 1))
                {
                    yield return row;
                }

                break;
            default:
                // A chain running past the roller's cap, with two lines waiting at the bottom that no roll can
                // reach. The tree is worth the one line its top table's own pick leaves.
                foreach (ContentRow row in DepthChain(tree, table, entry, LootRoller.MaxNestedDepth + 2))
                {
                    yield return row;
                }

                break;
        }
    }

    static IEnumerable<ContentRow> DepthChain(
        string tree,
        ContentTypeRegistration table,
        ContentTypeRegistration entry,
        int floor)
    {
        yield return LootTable(table, 1, tree, rollCount: 1);
        yield return LootEntry(entry, 1, "top_item", table: 1, weight: 1, guaranteed: false, chance: 10_000, item: 11);
        yield return LootEntry(entry, 2, "descends", table: 1, weight: 1, guaranteed: false, chance: 10_000, nested: 2);
        for (int level = 2; level < floor; level++)
        {
            yield return LootTable(table, level, Level(level), rollCount: 1);
            yield return LootEntry(
                entry, level + 20, "down_" + Level(level), table: level,
                weight: 1, guaranteed: false, chance: 10_000, nested: level + 1);
        }

        yield return LootTable(table, floor, "floor", rollCount: 2);
        yield return LootEntry(
            entry, floor + 20, "floor_item", table: floor,
            weight: 1, guaranteed: false, chance: 10_000, item: 12);
    }

    static string Level(int level) => "level_" + level.ToString(CultureInfo.InvariantCulture);
}
