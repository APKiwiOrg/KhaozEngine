using System;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Client.Internal;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Client;

public sealed class FrameHistoryTests
{
    static readonly FrameHistoryOptions Defaults = new();

    static ClipFrameRecord Frame(TimeSpan at, int bytes = 10, int width = 960, int height = 540, int generation = 0,
        double renderMilliseconds = 16.7)
        => new(at, renderMilliseconds, width, height, new byte[bytes], generation);

    static int ClaimedOver(FrameHistory history, double displayHz, double seconds)
    {
        int frames = (int)Math.Round(displayHz * seconds);
        int claimed = 0;
        for (int i = 0; i < frames; i++)
            if (history.TryClaimTick(TimeSpan.FromTicks((long)(i * TimeSpan.TicksPerSecond / displayHz))))
                claimed++;
        return claimed;
    }

    [Theory]
    [InlineData(60.0)]
    [InlineData(144.0)]
    [InlineData(30.0)]
    public void Ticks_hold_twelve_per_second_at_any_display_rate(double hz)
    {
        int claimed = ClaimedOver(new FrameHistory(Defaults), hz, 5);
        Assert.InRange(claimed, 59, 61);
    }

    [Fact]
    public void A_display_slower_than_the_rate_claims_every_frame()
        => Assert.Equal(50, ClaimedOver(new FrameHistory(Defaults), 10, 5));

    [Fact]
    public void The_ring_never_holds_more_than_duration_times_rate_frames()
    {
        var history = new FrameHistory(Defaults);
        TimeSpan newest = TimeSpan.Zero;
        for (int i = 0; i < 200; i++)
        {
            newest = history.Interval * i;
            history.Append(Frame(newest));
        }

        ReportClip clip = history.Freeze();
        Assert.Equal(60, history.Count);
        Assert.Equal(60, clip.Frames.Count);
        TimeSpan oldest = newest - clip.Frames[^1].Offset;
        Assert.True(oldest > newest - Defaults.Duration);
    }

    [Fact]
    public void The_byte_cap_drops_the_oldest_frames_first()
    {
        var history = new FrameHistory(Defaults with { MaxBytes = 1000 });
        for (int i = 0; i < 5; i++)
            history.Append(Frame(TimeSpan.FromMilliseconds(100 * i), bytes: 300, renderMilliseconds: i));

        Assert.Equal(3, history.Count);
        Assert.True(history.TotalBytes <= 1000);
        ReportClip clip = history.Freeze();
        Assert.Equal(new[] { 2.0, 3.0, 4.0 }, new[]
        {
            clip.Frames[0].RenderMilliseconds, clip.Frames[1].RenderMilliseconds, clip.Frames[2].RenderMilliseconds,
        });
    }

    [Fact]
    public void Frames_of_different_sizes_keep_their_own_dimensions()
    {
        var history = new FrameHistory(Defaults);
        history.Append(Frame(TimeSpan.FromMilliseconds(0), width: 960, height: 540));
        history.Append(Frame(TimeSpan.FromMilliseconds(100), width: 960, height: 720));
        history.Append(Frame(TimeSpan.FromMilliseconds(200), width: 540, height: 960));

        ReportClip clip = history.Freeze();
        Assert.Equal(3, clip.Frames.Count);
        Assert.Equal((960, 540), (clip.Frames[0].Width, clip.Frames[0].Height));
        Assert.Equal((960, 720), (clip.Frames[1].Width, clip.Frames[1].Height));
        Assert.Equal((540, 960), (clip.Frames[2].Width, clip.Frames[2].Height));
    }

    [Fact]
    public void Freeze_returns_offsets_from_the_first_frame_and_render_times()
    {
        var history = new FrameHistory(Defaults);
        history.Append(Frame(TimeSpan.FromMilliseconds(10_000), bytes: 3, renderMilliseconds: 16.5));
        history.Append(Frame(TimeSpan.FromMilliseconds(10_100), bytes: 4, renderMilliseconds: 120.25));
        history.Append(Frame(TimeSpan.FromMilliseconds(10_200), bytes: 5, renderMilliseconds: 8));

        ReportClip clip = history.Freeze();
        Assert.Equal(TimeSpan.Zero, clip.Frames[0].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(100), clip.Frames[1].Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(200), clip.Frames[2].Offset);
        Assert.Equal(16.5, clip.Frames[0].RenderMilliseconds);
        Assert.Equal(120.25, clip.Frames[1].RenderMilliseconds);
        Assert.Equal(8, clip.Frames[2].RenderMilliseconds);
        Assert.Equal(new[] { 3, 4, 5 }, new[] { clip.Frames[0].Jpeg.Length, clip.Frames[1].Jpeg.Length, clip.Frames[2].Jpeg.Length });
    }

    [Fact]
    public void Freeze_with_no_frames_returns_an_empty_clip()
    {
        ReportClip clip = new FrameHistory(Defaults).Freeze();
        Assert.NotNull(clip.Frames);
        Assert.Empty(clip.Frames);
    }

    [Fact]
    public void While_frozen_no_tick_is_claimed_and_appends_are_ignored()
    {
        var history = new FrameHistory(Defaults);
        history.Append(Frame(TimeSpan.Zero));
        history.Freeze();

        Assert.True(history.IsFrozen);
        Assert.False(history.TryClaimTick(TimeSpan.FromSeconds(1)));
        history.Append(Frame(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, history.Count);
        Assert.Equal(10, history.TotalBytes);
    }

    [Fact]
    public void Thaw_resumes_ticks_and_keeps_the_frames_from_before_the_freeze()
    {
        var history = new FrameHistory(Defaults);
        history.Append(Frame(TimeSpan.Zero));
        history.Append(Frame(TimeSpan.FromMilliseconds(100)));
        history.Freeze();
        history.Thaw();

        Assert.False(history.IsFrozen);
        Assert.Equal(2, history.Count);
        Assert.True(history.TryClaimTick(TimeSpan.FromSeconds(1)));
        history.Append(Frame(TimeSpan.FromSeconds(1)));
        Assert.Equal(3, history.Freeze().Frames.Count);
    }

    [Fact]
    public void While_suspended_no_tick_is_claimed_but_frames_are_kept()
    {
        var history = new FrameHistory(Defaults);
        history.Append(Frame(TimeSpan.Zero));
        history.Suspend();

        Assert.True(history.IsSuspended);
        Assert.False(history.TryClaimTick(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, history.Count);

        history.Resume();
        Assert.False(history.IsSuspended);
        Assert.True(history.TryClaimTick(TimeSpan.FromSeconds(2)));
        Assert.Single(history.Freeze().Frames);
    }

    [Fact]
    public void Clear_drops_every_frame_and_rejects_frames_from_the_old_generation()
    {
        var history = new FrameHistory(Defaults);
        Assert.True(history.TryClaimTick(TimeSpan.FromSeconds(3)));
        history.Append(Frame(TimeSpan.FromSeconds(3), generation: history.Generation));
        int old = history.Generation;

        history.Clear();

        Assert.Equal(old + 1, history.Generation);
        Assert.Equal(0, history.Count);
        Assert.Equal(0, history.TotalBytes);
        history.Append(Frame(TimeSpan.FromSeconds(3.1), generation: old));
        Assert.Equal(0, history.Count);
        history.Append(Frame(TimeSpan.FromSeconds(3.2), generation: history.Generation));
        Assert.Equal(1, history.Count);
        Assert.True(history.TryClaimTick(TimeSpan.Zero));
    }
}
