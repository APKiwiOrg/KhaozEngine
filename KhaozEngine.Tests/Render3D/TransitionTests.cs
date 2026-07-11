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
    public void CharDissolve_skips_the_hold_and_ignores_ready()
    {
        // World-space dissolve assumes an already-streamed destination: it never waits between out and in.
        var t = new CharDissolve(dissolveOutSeconds: 0.1f, dissolveInSeconds: 0.1f);
        int swaps = 0;
        t.Swapped += () => swaps++;
        t.Begin();
        t.Update(0.1f, destinationReady: false);   // completes the dissolve-out
        Assert.Equal(1, swaps);
        Assert.Equal(TransitionPhase.Reveal, t.Phase);   // straight to reveal, no hold
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
