using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the anchored floating-text store: aging, expiry, the per-anchor cap and the
/// stack index a burst takes.</summary>
public class FloatingTextStoreTests
{
    const float Tol = 1e-4f;

    static FloatingTextStyle Style => new()
    {
        Color = Color.White,
        LifetimeSeconds = 1f,
        DriftPerSecond = new Vector2(0f, -40f),
        StartScale = 1f,
        EndScale = 1f,
        StackSpacing = 12f,
    };

    [Fact]
    public void Add_HoldsTheEntryAtAgeZero()
    {
        var store = new FloatingTextStore();
        store.Add("+12 xp", 7L, new Vector2(0f, -32f), Style);

        Assert.Equal(1, store.Count);
        FloatingText e = store.Live[0];
        Assert.Equal("+12 xp", e.Text);
        Assert.Equal(7L, e.AnchorId);
        Assert.Equal(new Vector2(0f, -32f), e.Offset);
        Assert.Equal(0f, e.Age, Tol);
        Assert.Equal(0, e.StackIndex);
    }

    // An entry nobody can read still holds a slot against the cap and still evicts one somebody could.
    [Fact]
    public void Add_RefusesAnEmptyLine()
    {
        var store = new FloatingTextStore();
        store.Add("", 1L, Vector2.Zero, Style);
        store.Add(null!, 1L, Vector2.Zero, Style);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Age_AdvancesEveryEntryAndExpiresOnTheLifetime()
    {
        var store = new FloatingTextStore();
        store.Add("a", 1L, Vector2.Zero, Style);
        store.Age(0.4f);
        store.Add("b", 1L, Vector2.Zero, Style);

        store.Age(0.4f);
        Assert.Equal(2, store.Count);
        Assert.Equal(0.8f, store.Live[0].Age, Tol);
        Assert.Equal(0.4f, store.Live[1].Age, Tol);

        // The first crosses its one second lifetime and goes, the second is still mid-life.
        store.Age(0.4f);
        Assert.Equal(1, store.Count);
        Assert.Equal("b", store.Live[0].Text);
        Assert.Equal(0.8f, store.Live[0].Age, Tol);
    }

    // The style is per entry, so a store holding a short line and a long one ages both correctly and the oldest is
    // not necessarily the first to go. Order is preserved for the survivors.
    [Fact]
    public void Age_ExpiresOutOfOrderWhenLifetimesDiffer()
    {
        var store = new FloatingTextStore();
        store.Add("short", 1L, Vector2.Zero, Style with { LifetimeSeconds = 0.5f });
        store.Add("long", 1L, Vector2.Zero, Style with { LifetimeSeconds = 5f });
        store.Add("also long", 1L, Vector2.Zero, Style with { LifetimeSeconds = 5f });

        store.Age(0.6f);
        Assert.Equal(2, store.Count);
        Assert.Equal("long", store.Live[0].Text);
        Assert.Equal("also long", store.Live[1].Text);
    }

    [Fact]
    public void Age_IsANoOpForANonPositiveStep()
    {
        var store = new FloatingTextStore();
        store.Add("a", 1L, Vector2.Zero, Style);
        store.Age(0f);
        store.Age(-1f);
        Assert.Equal(1, store.Count);
        Assert.Equal(0f, store.Live[0].Age, Tol);
    }

    // The cap is a hard ceiling on what one anchor holds, applied before the add, and it is the OLDEST that goes.
    [Fact]
    public void Add_EvictsTheAnchorsOldestWhenTheCapIsReached()
    {
        var store = new FloatingTextStore();
        FloatingTextStyle capped = Style with { MaxPerAnchor = 3 };
        for (int i = 0; i < 5; i++) store.Add($"line {i}", 4L, Vector2.Zero, capped);

        Assert.Equal(3, store.Count);
        Assert.Equal("line 2", store.Live[0].Text);
        Assert.Equal("line 3", store.Live[1].Text);
        Assert.Equal("line 4", store.Live[2].Text);
    }

    [Fact]
    public void The_cap_is_per_anchor_rather_than_per_store()
    {
        var store = new FloatingTextStore();
        FloatingTextStyle capped = Style with { MaxPerAnchor = 2 };
        for (int i = 0; i < 3; i++) store.Add($"a{i}", 1L, Vector2.Zero, capped);
        for (int i = 0; i < 3; i++) store.Add($"b{i}", 2L, Vector2.Zero, capped);

        Assert.Equal(4, store.Count);
        Assert.Equal(2, store.CountFor(1L));
        Assert.Equal(2, store.CountFor(2L));
    }

    [Fact]
    public void A_zero_cap_is_unlimited()
    {
        var store = new FloatingTextStore(capacity: 2);
        FloatingTextStyle uncapped = Style with { MaxPerAnchor = 0 };
        for (int i = 0; i < 20; i++) store.Add($"line {i}", 1L, Vector2.Zero, uncapped);

        Assert.Equal(20, store.Count);
        Assert.True(store.Capacity >= 20, "the backing array grew rather than dropping entries");
    }

    // A burst arriving on one frame reads as a column: each entry takes the step below the siblings already there.
    [Fact]
    public void A_burst_on_one_anchor_takes_one_stack_step_each()
    {
        var store = new FloatingTextStore();
        for (int i = 0; i < 5; i++) store.Add($"+{i} xp", 9L, Vector2.Zero, Style);

        for (int i = 0; i < 5; i++) Assert.Equal(i, store.Live[i].StackIndex);
        // Different anchors are different columns, so a second body starts at the top again.
        store.Add("elsewhere", 10L, Vector2.Zero, Style);
        Assert.Equal(0, store.Live[5].StackIndex);
    }

    // The index is a BIRTH-time answer and is never renormalized, which is what stops the rest of a burst jumping up
    // a step when its oldest expires.
    [Fact]
    public void An_expiring_sibling_does_not_move_the_entries_that_outlive_it()
    {
        var store = new FloatingTextStore();
        store.Add("first", 3L, Vector2.Zero, Style with { LifetimeSeconds = 0.5f });
        store.Add("second", 3L, Vector2.Zero, Style with { LifetimeSeconds = 5f });
        Assert.Equal(1, store.Live[1].StackIndex);

        store.Age(0.6f);
        FloatingText survivor = Assert.Single(store.Live.ToArray());
        Assert.Equal("second", survivor.Text);
        Assert.Equal(1, survivor.StackIndex);
    }

    [Fact]
    public void Clear_EmptiesEverythingAndClearByAnchorEmptiesOneColumn()
    {
        var store = new FloatingTextStore();
        store.Add("a", 1L, Vector2.Zero, Style);
        store.Add("b", 2L, Vector2.Zero, Style);
        store.Add("c", 1L, Vector2.Zero, Style);

        store.Clear(1L);
        FloatingText left = Assert.Single(store.Live.ToArray());
        Assert.Equal("b", left.Text);
        Assert.Equal(0, store.CountFor(1L));

        store.Clear();
        Assert.Equal(0, store.Count);
    }
}
