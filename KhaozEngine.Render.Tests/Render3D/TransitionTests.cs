using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The screen/world transition state machine that masks a teleport: cover -> swap (under cover) -> optional
/// streaming hold -> reveal. Pure timing, driven by a fake clock (explicit dt) and a fake ready predicate - no GPU.
/// The three built-in effects (HardBlink, CameraDissolve, CharDissolve) share this timing; the render (what
/// "covered" looks like) is verified by running the client, per repo convention.
/// </summary>
public class TransitionTests
{
    static HardBlink Blink() => new(coverSeconds: 0.1f, holdTimeoutSeconds: 1.0f, revealSeconds: 0.1f);

    [Fact]
    public void Starts_idle_and_fully_revealed()
    {
        var t = Blink();
        Assert.Equal(TransitionPhase.Idle, t.Phase);
        Assert.False(t.IsActive);
        Assert.Equal(0f, t.Cover, 3);
    }

    [Fact]
    public void Begin_enters_cover_and_activates()
    {
        var t = Blink();
        t.Begin();
        Assert.Equal(TransitionPhase.Cover, t.Phase);
        Assert.True(t.IsActive);
        Assert.Equal(0f, t.Cover, 3);
    }

    [Fact]
    public void Cover_ramps_zero_to_one_over_cover_seconds()
    {
        var t = Blink();
        t.Begin();
        t.Update(0.05f, destinationReady: false);
        Assert.Equal(0.5f, t.Cover, 2);
        Assert.Equal(TransitionPhase.Cover, t.Phase);
    }

    [Fact]
    public void Swap_fires_once_at_full_cover_then_holds()
    {
        var t = Blink();
        int swaps = 0;
        t.Swapped += () => swaps++;
        t.Begin();
        t.Update(0.1f, false);   // completes cover
        Assert.Equal(1, swaps);
        Assert.Equal(TransitionPhase.Hold, t.Phase);
        Assert.Equal(1f, t.Cover, 3);   // fully covered through the hold
        t.Update(0f, false);
        Assert.Equal(1, swaps);          // never fires twice
    }

    [Fact]
    public void Hold_is_released_early_by_the_ready_signal()
    {
        var t = Blink();
        t.Begin();
        t.Update(0.1f, false);              // -> hold
        Assert.Equal(TransitionPhase.Hold, t.Phase);
        t.Update(0.01f, destinationReady: true);   // ready releases the hold well before the timeout
        Assert.Equal(TransitionPhase.Reveal, t.Phase);
    }

    [Fact]
    public void Hold_is_released_by_the_timeout_when_never_ready()
    {
        var t = Blink();   // holdTimeout 1.0s
        t.Begin();
        t.Update(0.1f, false);   // -> hold
        for (int i = 0; i < 100; i++) t.Update(0.02f, destinationReady: false);   // 2.0s of never-ready
        Assert.NotEqual(TransitionPhase.Hold, t.Phase);   // the bounded timeout still releases it
    }

    [Fact]
    public void Reveal_ramps_one_to_zero_then_completes_once()
    {
        var t = Blink();
        int done = 0;
        t.Completed += () => done++;
        t.Begin();
        t.Update(0.1f, false);   // -> hold
        t.Update(0f, true);      // -> reveal
        Assert.Equal(TransitionPhase.Reveal, t.Phase);
        t.Update(0.05f, true);
        Assert.Equal(0.5f, t.Cover, 2);   // half revealed
        t.Update(0.05f, true);   // completes reveal
        Assert.Equal(TransitionPhase.Done, t.Phase);
        Assert.Equal(0f, t.Cover, 3);
        Assert.False(t.IsActive);
        Assert.Equal(1, done);
        t.Update(1f, true);
        Assert.Equal(1, done);   // Completed never fires twice
    }

    [Fact]
    public void CharDissolve_covers_instantly_then_materializes_in_no_hold()
    {
        // A teleport is a hard cut: the avatar has already cut to the destination, so CharDissolve has NO origin
        // dissolve-out. It covers instantly (fully dissolved = invisible on the cut frame), swaps under cover, then
        // materializes IN at the destination over its reveal - never holding (assumes a streamed destination).
        var t = new CharDissolve(materializeSeconds: 0.2f);
        int swaps = 0;
        t.Swapped += () => swaps++;
        t.Begin();
        Assert.Equal(1f, t.Cover, 3);                    // fully dissolved (gone) on the cut frame - no origin ramp
        Assert.Equal(TransitionPhase.Cover, t.Phase);
        t.Update(0.001f, destinationReady: false);       // any advance completes the instant cover
        Assert.Equal(1, swaps);
        Assert.Equal(TransitionPhase.Reveal, t.Phase);   // straight to reveal (materialize-in), no hold
        // The reveal ramps Cover 1 -> 0 (dissolve threshold gone -> solid): the avatar materializes in at the destination.
        t.Update(0.1f, destinationReady: false);
        Assert.Equal(0.5f, t.Cover, 2);
    }

    [Fact]
    public void HardBlink_default_is_reveal_only_opaque_on_the_cut_frame()
    {
        // FIX 2: a teleport is a hard cut, so the DEFAULT blink covers instantly (coverSeconds 0). It must be fully
        // opaque (Cover 1) the instant it begins - the cut frame - not ramp up from 0.
        var t = new HardBlink();   // defaults: coverSeconds 0
        t.Begin();
        Assert.Equal(1f, t.Cover, 3);                    // opaque on the very first frame, before any Update
        Assert.Equal(TransitionPhase.Cover, t.Phase);
    }

    [Fact]
    public void HardBlink_cover_zero_is_opaque_on_first_update_and_swaps_exactly_once()
    {
        // The instant-cover blink stays fully opaque through its first Update/render frame and fires Swapped exactly
        // once (the early-out that skips a fully-REVEALED frame must never skip this fully-COVERED first frame).
        var t = new HardBlink();   // coverSeconds 0, holdTimeout 1.5, reveal 0.08
        int swaps = 0;
        t.Swapped += () => swaps++;
        t.Begin();
        t.Update(1f / 60f, destinationReady: false);     // the first frame's advance completes the instant cover
        Assert.Equal(1, swaps);
        Assert.Equal(TransitionPhase.Hold, t.Phase);
        Assert.Equal(1f, t.Cover, 3);                    // still fully opaque through the hold
        t.Update(0f, destinationReady: false);
        Assert.Equal(1, swaps);                          // never fires twice
    }

    [Fact]
    public void Reset_cancels_to_idle_without_firing_swap_or_complete()
    {
        // A consumer teardown mid-transition (disconnect / screen swap) must be able to cancel a transition so a stuck
        // effect does not hold the overlay covered forever. Reset returns it to Idle silently (no Swapped/Completed).
        var t = Blink();
        int swaps = 0, done = 0;
        t.Swapped += () => swaps++;
        t.Completed += () => done++;
        t.Begin();
        t.Update(0.1f, destinationReady: false);   // completes cover -> Swapped fired once, now in Hold
        Assert.Equal(1, swaps);
        Assert.True(t.IsActive);

        t.Reset();
        Assert.Equal(TransitionPhase.Idle, t.Phase);
        Assert.False(t.IsActive);
        Assert.Equal(0f, t.Cover, 3);
        Assert.Equal(1, swaps);   // Reset itself does not fire Swapped
        Assert.Equal(0, done);    // nor Completed

        t.Update(1f, destinationReady: true);   // idle after reset: nothing happens, idempotent
        Assert.Equal(TransitionPhase.Idle, t.Phase);
        Assert.Equal(0, done);
    }

    [Fact]
    public void CameraDissolve_covers_instantly_at_begin()
    {
        // The frozen-frame crossfade covers the moment it begins (the captured frame), then holds, then crossfades.
        var t = new CameraDissolve(holdTimeoutSeconds: 1.0f, revealSeconds: 0.2f);
        t.Begin();
        Assert.Equal(1f, t.Cover, 3);
        Assert.Equal(TransitionPhase.Cover, t.Phase);
        int swaps = 0;
        t.Swapped += () => swaps++;
        t.Update(0.001f, false);   // any advance completes the instant cover
        Assert.Equal(1, swaps);
        Assert.Equal(TransitionPhase.Hold, t.Phase);
    }

    [Fact]
    public void Update_is_a_no_op_when_idle_or_done()
    {
        var t = Blink();
        t.Update(1f, true);
        Assert.Equal(TransitionPhase.Idle, t.Phase);   // idle: nothing happens

        t.Begin();
        for (int i = 0; i < 200; i++) t.Update(0.05f, true);
        Assert.Equal(TransitionPhase.Done, t.Phase);

        int done = 0;
        t.Completed += () => done++;
        t.Update(1f, true);
        Assert.Equal(TransitionPhase.Done, t.Phase);   // done: nothing happens, no re-fire
        Assert.Equal(0, done);
    }

    [Fact]
    public void Begin_re_arms_a_running_transition()
    {
        var t = Blink();
        t.Begin();
        t.Update(0.1f, false);   // -> hold
        t.Begin();               // re-arm mid-transition
        Assert.Equal(TransitionPhase.Cover, t.Phase);
        Assert.Equal(0f, t.Cover, 3);
    }
}
