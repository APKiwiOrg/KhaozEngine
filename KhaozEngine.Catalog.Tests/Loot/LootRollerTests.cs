using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KhaozEngine.Catalog;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Loot;

/// <summary>
/// The one implementation of spec 3.5's composition rule. What is pinned here is the DRAW ORDER, because it
/// is exactly the interaction two independent reimplementations would have got differently: every guaranteed
/// entry in sort order first, each rolling its own chance, then <c>roll_count</c> weighted picks over the
/// non-guaranteed entries, with a nested table recursing at the point it is drawn and a tag filter drawing
/// uniformly from its precomputed candidates.
/// <para>
/// Joins <c>AllocSensitive</c> because one fact measures
/// <c>GC.GetAllocatedBytesForCurrentThread</c> over a warm loop.
/// </para>
/// </summary>
[Collection("AllocSensitive")]
public class LootRollerTests
{
    [Fact]
    public void A_seeded_roll_over_the_chest_table_drops_exactly_these_lines()
    {
        // Seed 1 draws, in the order the rule consumes them: 4876 against entry 401's 5,000 chance (a hit),
        // 3 for its count over 1 to 3, 2 for the nested rare table's one weighted pick, then 10 and 91 for
        // the chest's two picks over a pool total of 100, and 0 for the tag draw the second pick lands on.
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1));
        Span<LootDraw> destination = stackalloc LootDraw[8];

        int written = roller.Roll(LootRollerFixtures.ChestTable, destination);

        Assert.Equal(4, written);
        Assert.Equal(new LootDraw(2, 3, LootRollerFixtures.ChestTable), destination[0]);
        Assert.Equal(new LootDraw(8, 1, LootRollerFixtures.RareTable), destination[1]);
        Assert.Equal(new LootDraw(1, 2, LootRollerFixtures.ChestTable), destination[2]);
        Assert.Equal(new LootDraw(1, 1, LootRollerFixtures.ChestTable), destination[3]);
    }

    [Fact]
    public void A_guaranteed_entry_whose_chance_misses_drops_nothing_and_the_rest_of_the_table_still_rolls()
    {
        // Seed 3 opens with 7969 against the same 5,000, so entry 401 misses and consumes no count draw. The
        // certain entry 402 still recurses, and both weighted picks still happen.
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(3));
        Span<LootDraw> destination = stackalloc LootDraw[8];

        int written = roller.Roll(LootRollerFixtures.ChestTable, destination);

        Assert.Equal(3, written);
        Assert.Equal(new LootDraw(8, 1, LootRollerFixtures.RareTable), destination[0]);
        Assert.Equal(new LootDraw(2, 1, LootRollerFixtures.ChestTable), destination[1]);
        Assert.Equal(new LootDraw(1, 2, LootRollerFixtures.ChestTable), destination[2]);
    }

    [Fact]
    public void Every_guaranteed_entry_draws_in_sort_order_before_the_first_weighted_pick()
    {
        ContentRuntime runtime = LootRollerFixtures.Runtime();

        for (ulong seed = 1; seed <= 60; seed++)
        {
            var roller = new LootRoller(runtime, new SeededRandomSource(seed));
            LootDraw[] drawn = Draw(roller, LootRollerFixtures.ChestTable, 8);

            // Entry 402 is certain and sorts after 401, so the nested line is always present and every line
            // after it came from a weighted pick over the chest.
            int nested = Array.FindIndex(drawn, draw => draw.TableId == LootRollerFixtures.RareTable);
            Assert.True(nested >= 0, "the certain nested entry drew nothing on seed " + seed);
            Assert.True(nested <= 1, "a weighted pick drew before the guaranteed pass finished on seed " + seed);

            // When 401 hits it is the FIRST line, because it sorts first.
            if (nested == 1)
            {
                Assert.Equal(2, drawn[0].ItemId);
                Assert.Equal(LootRollerFixtures.ChestTable, drawn[0].TableId);
            }

            for (int i = nested + 1; i < drawn.Length; i++)
            {
                Assert.Equal(LootRollerFixtures.ChestTable, drawn[i].TableId);
            }
        }
    }

    [Fact]
    public void TableId_names_the_table_the_line_came_from_including_after_a_nested_draw()
    {
        // The one thing a caller cannot reconstruct: the rare table's line is the rare table's, and the chest
        // lines around it are the chest's. It is what a game's own drop event writes down as the source.
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1));
        LootDraw[] drawn = Draw(roller, LootRollerFixtures.ChestTable, 8);

        Assert.Equal(
            [LootRollerFixtures.ChestTable, LootRollerFixtures.RareTable, LootRollerFixtures.ChestTable, LootRollerFixtures.ChestTable],
            drawn.Select(draw => draw.TableId).ToArray());
    }

    [Fact]
    public void A_weighted_pick_never_lands_on_a_guaranteed_entry()
    {
        // The pool's guaranteed entry carries a weight of 1,000 against the weighted entry's 1 and can never
        // win it, because guaranteed is out of the pool entirely. Its chance is 0, so it drops nothing at all.
        ContentRuntime runtime = LootRollerFixtures.Runtime();

        for (ulong seed = 1; seed <= 40; seed++)
        {
            var roller = new LootRoller(runtime, new SeededRandomSource(seed));
            LootDraw[] drawn = Draw(roller, LootRollerFixtures.PoolTable, 8);

            Assert.Equal(3, drawn.Length);
            Assert.All(drawn, draw => Assert.Equal(8, draw.ItemId));
        }
    }

    [Fact]
    public void A_tag_filtered_entry_draws_uniformly_from_its_precomputed_live_candidates()
    {
        // The sword tag carries items 1, 2 and the retired 35. The candidate array is built at load with the
        // retired row excluded, so a tag draw can only ever be 1 or 2, and over enough seeds it is both.
        ContentRuntime runtime = LootRollerFixtures.Runtime();
        var seen = new HashSet<int>();

        for (ulong seed = 1; seed <= 60; seed++)
        {
            var roller = new LootRoller(runtime, new SeededRandomSource(seed));
            LootDraw[] drawn = Draw(roller, LootRollerFixtures.ChestTable, 8);

            // Everything after the guaranteed pass is a weighted pick, and among those the tag entry is the
            // one drawing a single item: entry 403 always drops two of item 1.
            int nested = Array.FindIndex(drawn, draw => draw.TableId == LootRollerFixtures.RareTable);
            for (int i = nested + 1; i < drawn.Length; i++)
            {
                if (drawn[i].Count != 1)
                {
                    continue;
                }

                Assert.True(drawn[i].ItemId is 1 or 2, "a tag draw offered item " + drawn[i].ItemId);
                seen.Add(drawn[i].ItemId);
            }
        }

        Assert.Equal([1, 2], seen.OrderBy(id => id).ToArray());
    }

    [Fact]
    public void A_count_is_drawn_between_the_entry_s_min_and_max_and_covers_both_ends()
    {
        ContentRuntime runtime = LootRollerFixtures.Runtime();
        var counts = new HashSet<int>();

        for (ulong seed = 1; seed <= 80; seed++)
        {
            var roller = new LootRoller(runtime, new SeededRandomSource(seed));
            foreach (LootDraw draw in Draw(roller, LootRollerFixtures.ChestTable, 8))
            {
                // Entry 401 is the only one with a range, 1 to 3, and it is the only source of item 2 with a
                // TableId of the chest.
                if (draw.ItemId == 2 && draw.TableId == LootRollerFixtures.ChestTable && draw.Count > 1)
                {
                    counts.Add(draw.Count);
                }
            }
        }

        Assert.Equal([2, 3], counts.OrderBy(count => count).ToArray());
    }

    [Fact]
    public void A_retired_entry_and_a_second_row_under_a_live_id_are_never_drawn()
    {
        // The chest carries a retired entry that is guaranteed and certain, dropping 5 of item 8, and a
        // second row under entry 403's id weighing 10,000 against the pool's 100, dropping 9 of item 2. Both
        // are dropped at load (spec 3.9, KEC0036), so neither count can appear however the seed falls.
        ContentRuntime runtime = LootRollerFixtures.Runtime();

        for (ulong seed = 1; seed <= 60; seed++)
        {
            var roller = new LootRoller(runtime, new SeededRandomSource(seed));
            foreach (LootDraw draw in Draw(roller, LootRollerFixtures.ChestTable, 16))
            {
                Assert.False(
                    draw.ItemId == 8 && draw.TableId == LootRollerFixtures.ChestTable,
                    "the retired entry drew on seed " + seed);
                Assert.True(draw.Count is 1 or 2 or 3, "an unauthored count " + draw.Count + " on seed " + seed);
            }
        }
    }

    [Fact]
    public void A_retired_table_rolls_nothing_and_is_not_an_overflow()
    {
        // A table that has left play is a table the roll does not carry: it takes no pick, its guaranteed
        // entries do not fire, and it is not an overflow, so a caller cannot tell it from a missing id.
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1));
        Span<LootDraw> destination = stackalloc LootDraw[8];

        Assert.Equal(0, roller.Roll(LootRollerFixtures.RetiredTable, destination));
        Assert.True(roller.TryRoll(LootRollerFixtures.RetiredTable, destination, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void A_destination_too_small_is_filled_and_the_overflow_is_reported_through_TryRoll()
    {
        // Spec 3.5: a destination too small is FILLED and the return is the span's length, so a caller sizes
        // up rather than silently losing drops.
        ContentRuntime runtime = LootRollerFixtures.Runtime();
        Span<LootDraw> small = stackalloc LootDraw[2];

        int written = new LootRoller(runtime, new SeededRandomSource(1)).Roll(LootRollerFixtures.ChestTable, small);

        Assert.Equal(2, written);
        Assert.Equal(new LootDraw(2, 3, LootRollerFixtures.ChestTable), small[0]);
        Assert.Equal(new LootDraw(8, 1, LootRollerFixtures.RareTable), small[1]);

        Span<LootDraw> again = stackalloc LootDraw[2];
        bool complete = new LootRoller(runtime, new SeededRandomSource(1))
            .TryRoll(LootRollerFixtures.ChestTable, again, out int overflowed);

        Assert.False(complete);
        Assert.Equal(2, overflowed);
    }

    [Fact]
    public void A_destination_that_fits_the_whole_roll_reports_no_overflow()
    {
        Span<LootDraw> destination = stackalloc LootDraw[8];

        bool complete = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1))
            .TryRoll(LootRollerFixtures.ChestTable, destination, out int written);

        Assert.True(complete);
        Assert.Equal(4, written);
    }

    [Fact]
    public void A_table_this_version_does_not_carry_draws_nothing_and_is_not_an_overflow()
    {
        Span<LootDraw> destination = stackalloc LootDraw[4];
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1));

        Assert.Equal(0, roller.Roll(9_999, destination));
        Assert.True(roller.TryRoll(9_999, destination, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void A_cycle_a_hostile_pack_carried_is_bounded_by_the_depth_cap_rather_than_recursing_forever()
    {
        // KEC0024 refuses a cycle at publish, so this pack cannot be published. The cap is the second lock,
        // because the roller reads bytes a store handed it and a hostile pack must not be able to run it out
        // of stack. Each level drops one certain item and then recurses, so the top table plus the cap's
        // worth of nesting is exactly what lands.
        var roller = new LootRoller(LootRollerFixtures.Runtime(), new SeededRandomSource(1));
        Span<LootDraw> destination = stackalloc LootDraw[64];

        int written = roller.Roll(LootRollerFixtures.CycleTable, destination);

        Assert.Equal(LootRoller.MaxNestedDepth + 1, written);
        Assert.Equal(1, destination[0].ItemId);
        Assert.Equal(8, destination[1].ItemId);
        Assert.Equal(LootRollerFixtures.CycleTableBack, destination[1].TableId);
    }

    [Fact]
    public void The_same_seed_draws_the_same_lines_and_a_different_seed_diverges()
    {
        ContentRuntime runtime = LootRollerFixtures.Runtime();

        LootDraw[] first = Draw(new LootRoller(runtime, new SeededRandomSource(11)), LootRollerFixtures.ChestTable, 8);
        LootDraw[] again = Draw(new LootRoller(runtime, new SeededRandomSource(11)), LootRollerFixtures.ChestTable, 8);
        LootDraw[] other = Draw(new LootRoller(runtime, new SeededRandomSource(3)), LootRollerFixtures.ChestTable, 8);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Rolling_allocates_nothing_over_a_warm_loop()
    {
        ContentRuntime runtime = LootRollerFixtures.Runtime();
        var roller = new LootRoller(runtime, new SeededRandomSource(5));

        // Warm first, so a jitted method rather than a cold one is what the measurement sees.
        Warm(roller, 32);

        CatalogAllocAssert.NoPerCallAllocation("LootRoller.Roll over a 4 entry table", () => Warm(roller, 512));
    }

    [Fact]
    public void It_builds_no_instance_places_no_stack_adds_to_no_inventory_and_emits_no_event()
    {
        // Asserted by ABSENCE of API: the roller reads content and a random source and returns numbers. The
        // journal event a game records is the game's, and the caller passes the TableId it got back into it.
        string[] members = typeof(LootRoller)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([".ctor", "MaxNestedDepth", "Roll", "TryRoll"], members);

        // And the draw itself is three numbers, with no instance, container or event reachable from one.
        string[] draw = typeof(LootDraw)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Count", "ItemId", "TableId"], draw);
    }

    [Fact]
    public void The_random_source_comes_in_through_the_constructor_and_there_is_no_default()
    {
        // Contracts 14.4: a type with no IRandomSource cannot roll, which is the property that makes "does
        // this class have gameplay randomness" answerable by reading its signature.
        ConstructorInfo[] constructors = typeof(LootRoller).GetConstructors();
        ParameterInfo[] parameters = Assert.Single(constructors).GetParameters();

        Assert.Equal([typeof(ContentRuntime), typeof(IRandomSource)], parameters.Select(p => p.ParameterType).ToArray());
        Assert.All(parameters, parameter => Assert.False(parameter.HasDefaultValue));

        Assert.Throws<ArgumentNullException>(() => new LootRoller(null!, new SeededRandomSource(1)));
        Assert.Throws<ArgumentNullException>(() => new LootRoller(LootRollerFixtures.Runtime(), null!));
    }

    /// <summary>One roll into a fresh array, for the facts that read the whole result rather than a span.</summary>
    static LootDraw[] Draw(LootRoller roller, int tableId, int capacity)
    {
        var destination = new LootDraw[capacity];
        int written = roller.Roll(tableId, destination);
        return destination[..written];
    }

    /// <summary>The loop the allocation fact measures, which must be safe to run twice.</summary>
    static void Warm(LootRoller roller, int rolls)
    {
        Span<LootDraw> destination = stackalloc LootDraw[8];
        for (int i = 0; i < rolls; i++)
        {
            roller.Roll(LootRollerFixtures.ChestTable, destination);
        }
    }
}
