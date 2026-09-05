using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D.Vfx;
using Xunit;

namespace KhaozEngine.Tests.Render2D.Vfx;

/// <summary>Headless coverage for the screen-fixed banner: its easing, its endpoints and its store's lifecycle.</summary>
public class FloatingBannerTests
{
    const float Tol = 1e-4f;

    static readonly Vector2 Middle = new(640f, 360f);
    static readonly Vector2 Corner = new(1180f, 60f);

    static FloatingTextStyle Style => new()
    {
        Color = Color.White,
        LifetimeSeconds = 2f,
        StartScale = 2f,
        EndScale = 0.6f,
        FadeOutSeconds = 0.5f,
    };

    [Fact]
    public void Ease_IsPinnedAtBothEndsAndClamped()
    {
        Assert.Equal(0f, FloatingBannerCurves.Ease(0f), Tol);
        Assert.Equal(1f, FloatingBannerCurves.Ease(1f), Tol);
        Assert.Equal(0f, FloatingBannerCurves.Ease(-3f), Tol);
        Assert.Equal(1f, FloatingBannerCurves.Ease(4f), Tol);
        // Ease-out: past the half way point of the travel by the half way point of the time.
        Assert.True(FloatingBannerCurves.Ease(0.5f) > 0.5f);
    }

    [Fact]
    public void PositionAt_HitsBothEndpointsExactly()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(Middle, FloatingBannerCurves.PositionAt(0f, Middle, Corner, p));
        Assert.Equal(Corner, FloatingBannerCurves.PositionAt(2f, Middle, Corner, p));
        Assert.Equal(Corner, FloatingBannerCurves.PositionAt(9f, Middle, Corner, p));
        Assert.Equal(Middle, FloatingBannerCurves.PositionAt(-1f, Middle, Corner, p));
        // No span to travel: the start point is all there is to answer.
        Assert.Equal(Middle, FloatingBannerCurves.PositionAt(1f, Middle, Corner, p with { LifetimeSeconds = 0f }));
    }

    [Fact]
    public void PositionAt_MovesMonotonicallyTowardTheEnd()
    {
        FloatingTextStyle p = Style;
        float previous = -1f;
        for (int i = 0; i <= 20; i++)
        {
            float travelled = Vector2.Distance(Middle, FloatingBannerCurves.PositionAt(i * 0.1f, Middle, Corner, p));
            Assert.True(travelled >= previous, $"the banner went backwards at age {i * 0.1f}");
            previous = travelled;
        }
        Assert.Equal(Vector2.Distance(Middle, Corner), previous, 1e-3f);
    }

    // The banner zooms and fades on the anchored text's own curves rather than a second copy of them.
    [Fact]
    public void The_banner_zooms_down_and_fades_out_on_the_shared_curves()
    {
        FloatingTextStyle p = Style;
        Assert.Equal(2f, FloatingTextCurves.ScaleAt(0f, p), Tol);
        Assert.Equal(0.6f, FloatingTextCurves.ScaleAt(2f, p), Tol);
        Assert.Equal(1f, FloatingTextCurves.AlphaAt(0f, p), Tol);   // no fade-in: a banner is meant to be seen at once
        Assert.Equal(1f, FloatingTextCurves.AlphaAt(1.5f, p), Tol);
        Assert.Equal(0.5f, FloatingTextCurves.AlphaAt(1.75f, p), Tol);
        Assert.Equal(0f, FloatingTextCurves.AlphaAt(2f, p), Tol);
    }

    [Fact]
    public void The_store_ages_and_expires_its_banners()
    {
        var store = new FloatingBannerStore();
        store.Add("Woodcutting 10", Middle, Corner, Style);
        Assert.Equal(1, store.Count);
        Assert.Equal(Middle, store.Live[0].Start);
        Assert.Equal(Corner, store.Live[0].End);

        store.Age(1f);
        Assert.Equal(1f, store.Live[0].Age, Tol);
        store.Age(1f);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void The_store_refuses_an_empty_line_and_clears_on_demand()
    {
        var store = new FloatingBannerStore();
        store.Add("", Middle, Corner, Style);
        Assert.Equal(0, store.Count);

        store.Add("Attack 20", Middle, Corner, Style);
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void The_store_grows_rather_than_dropping_a_banner()
    {
        var store = new FloatingBannerStore(capacity: 1);
        for (int i = 0; i < 6; i++) store.Add($"level {i}", Middle, Corner, Style);
        Assert.Equal(6, store.Count);
        Assert.True(store.Capacity >= 6);
    }
}

/// <summary>
/// The steady-state aging pass allocates nothing, which is what lets a game age both stores every frame without
/// feeding the collector. In the <c>AllocSensitive</c> collection because it measures this thread's allocated bytes
/// and a parallel class's gen-0 collection can land in the window.
/// </summary>
[Collection("AllocSensitive")]
public class FloatingTextAllocationTests
{
    [Fact]
    public void Aging_a_full_store_allocates_nothing()
    {
        FloatingTextStyle style = FloatingTextStyle.Default with { LifetimeSeconds = 1000f, MaxPerAnchor = 0 };
        var text = new FloatingTextStore(capacity: 64);
        var banners = new FloatingBannerStore(capacity: 8);
        for (int i = 0; i < 64; i++) text.Add("+12 xp", i % 8, Vector2.Zero, style);
        for (int i = 0; i < 8; i++) banners.Add("Woodcutting 10", Vector2.Zero, Vector2.One, style);

        AllocAssert.NoPerCallAllocation("FloatingTextStore.Age + FloatingBannerStore.Age", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                text.Age(0.016f);
                banners.Age(0.016f);
            }
        });

        // Nothing expired during the measurement, so what was measured is the steady state rather than a drain.
        Assert.Equal(64, text.Count);
        Assert.Equal(8, banners.Count);
    }
}
