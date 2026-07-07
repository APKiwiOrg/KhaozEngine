using System;
using System.Collections.Generic;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless tests for the single-stream music crossfade (<see cref="AudioSystem.MusicCrossfadeDuration"/> /
/// <see cref="AudioSystem.CrossfadeTo(string,float)"/>). All math is dt-driven and asserted against the recording
/// <see cref="FakeMusicBackend"/>: no real OpenAL device. Verifies the fade completes in the configured duration,
/// composes multiplicatively with the user music volume, mid-fade retarget restarts toward the newest track, and
/// duration 0 stays a hard cut identical to the pre-crossfade behavior.
/// </summary>
public sealed class MusicCrossfadeTests
{
    private static (AudioSystem audio, FakeMusicBackend backend) NewLoaded(params string[] tracks)
    {
        var backend = new FakeMusicBackend();
        var audio = new AudioSystem(backend, tracks);
        audio.LoadContent("tracks");
        return (audio, backend);
    }

    [Fact]
    public void DurationDefaultsToZero()
    {
        var (audio, _) = NewLoaded("a");
        Assert.Equal(0f, audio.MusicCrossfadeDuration);
    }

    [Fact]
    public void NegativeDurationClampsToZero()
    {
        var (audio, _) = NewLoaded("a");
        audio.MusicCrossfadeDuration = -3f;
        Assert.Equal(0f, audio.MusicCrossfadeDuration);
    }

    [Fact]
    public void Duration0_PlayTrack_IsImmediateHardCutIdenticalToToday()
    {
        // With no crossfade duration, PlayTrack must switch the backend track synchronously, exactly as before.
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");
        Assert.Equal(0, backend.PlayedIndices[^1]);   // "a" played immediately
        Assert.Equal("a", audio.CurrentTrack);

        audio.PlayTrack("b");
        Assert.Equal(1, backend.PlayedIndices[^1]);   // "b" played immediately, no Update() needed
        Assert.Equal("b", audio.CurrentTrack);
    }

    [Fact]
    public void CrossfadeTo_Duration0_IsImmediateHardCut()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");
        audio.CrossfadeTo("b", 0f);
        Assert.Equal(1, backend.PlayedIndices[^1]);
        Assert.Equal("b", audio.CurrentTrack);
    }

    [Fact]
    public void Crossfade_DefersTheSwitchUntilFadeOutReachesZero()
    {
        // The new track must NOT start on the frame the crossfade is requested: the old one fades out first.
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");                       // "a" playing (index 0)
        int playsBefore = backend.PlayedIndices.Count;

        audio.CrossfadeTo("b", 1.0f);               // 0.5s out, 0.5s in
        Assert.Equal(playsBefore, backend.PlayedIndices.Count);   // no immediate switch
        Assert.Equal("a", audio.CurrentTrack);      // still "a" during the fade-out

        // Advance halfway through the fade-out: still no switch.
        audio.Update(0.25f);
        Assert.Equal(playsBefore, backend.PlayedIndices.Count);
        Assert.Equal("a", audio.CurrentTrack);

        // Cross the fade-out end (0.5s total): the switch to "b" fires.
        audio.Update(0.30f);
        Assert.Equal("b", audio.CurrentTrack);
        Assert.Equal(1, backend.PlayedIndices[^1]);
    }

    [Fact]
    public void Crossfade_CompletesInConfiguredDuration_AndSettlesAtFullUserVolume()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.MasterVolume = 1f;
        audio.MusicVolume = 0.5f;
        audio.PlayTrack("a");

        audio.CrossfadeTo("b", 1.0f);               // half out (0.5s), half in (0.5s)

        // Drive comfortably past the full second (float accumulation can cost the switch-frame one extra tick).
        // By the end the fade must be idle and the backend gain back at the full settings-derived volume.
        for (int i = 0; i < 14; i++) audio.Update(0.1f);

        Assert.Equal("b", audio.CurrentTrack);
        float finalGain = backend.Volumes[^1];
        Assert.Equal(0.5f, finalGain, 3);           // 1.0 * 0.5 * factor(1.0)

        // One more update with no active fade must not change the gain further (idle: fade path is inert).
        int volsBefore = backend.Volumes.Count;
        audio.Update(0.1f);
        Assert.Equal(volsBefore, backend.Volumes.Count);   // idle: no extra SetVolume from the fade path
    }

    [Fact]
    public void Crossfade_MidFade_GainStaysBelowFullDuringTheDip()
    {
        // Mid fade-out the composed gain must dip below the full user volume (proving the factor multiplies in).
        var (audio, backend) = NewLoaded("a", "b");
        audio.MasterVolume = 1f;
        audio.MusicVolume = 1f;
        audio.PlayTrack("a");
        backend.Volumes.Clear();

        audio.CrossfadeTo("b", 1.0f);
        audio.Update(0.25f);                        // halfway through the fade-out half

        float mid = backend.Volumes[^1];
        Assert.True(mid > 0f && mid < 1f, $"expected a mid-fade dip in (0,1), got {mid}");
        Assert.Equal(0.5f, mid, 2);                 // linear: 0.25s of a 0.5s fade-out -> factor 0.5
    }

    [Fact]
    public void FadeComposesWithMusicVolumeChangedMidFade()
    {
        // Changing MusicVolume mid-fade must still take effect: the fade factor multiplies the NEW user volume.
        var (audio, backend) = NewLoaded("a", "b");
        audio.MasterVolume = 1f;
        audio.MusicVolume = 1f;
        audio.PlayTrack("a");

        audio.CrossfadeTo("b", 1.0f);
        audio.Update(0.25f);                        // factor ~0.5 during fade-out
        audio.MusicVolume = 0.4f;                   // user drops music volume mid-fade
        backend.Volumes.Clear();
        audio.Update(0.0f);                         // re-apply at dt 0 (no factor change): gain = 1 * 0.4 * 0.5

        Assert.Equal(0.2f, backend.Volumes[^1], 2);
    }

    [Fact]
    public void MidFadeRetarget_NewestTrackWins()
    {
        // Requesting a third track mid-fade must land on that newest track, not the one first requested.
        var (audio, backend) = NewLoaded("a", "b", "c");
        audio.PlayTrack("a");

        audio.CrossfadeTo("b", 1.0f);
        audio.Update(0.25f);                        // partway through the fade-out toward "b"
        audio.CrossfadeTo("c", 1.0f);               // retarget to "c" before the switch fired

        // Drive to completion; the stream must switch to "c", never "b".
        for (int i = 0; i < 20; i++) audio.Update(0.1f);

        Assert.Equal("c", audio.CurrentTrack);
        Assert.DoesNotContain(1, TailSwitches(backend));   // "b" (index 1) never became the active track
    }

    // Indices the backend actually switched TO after the initial PlayTrack("a") (index 0). Used to prove a
    // retargeted track was never activated.
    private static List<int> TailSwitches(FakeMusicBackend backend)
    {
        var switches = new List<int>();
        for (int i = 1; i < backend.PlayedIndices.Count; i++) switches.Add(backend.PlayedIndices[i]);
        return switches;
    }

    [Fact]
    public void CrossfadeFromSilence_FadesInOnlyNoDoubleStart()
    {
        // Nothing playing yet: the crossfade should skip the fade-out and just fade the new track in from 0.
        var (audio, backend) = NewLoaded("a", "b");
        // No PlayTrack: nothing is sounding (CurrentTrack null).
        Assert.Null(audio.CurrentTrack);

        audio.CrossfadeTo("b", 1.0f);
        audio.Update(0.01f);                        // first advance: immediate switch at factor 0, fade-in begins
        Assert.Equal("b", audio.CurrentTrack);
        Assert.Single(backend.PlayedIndices);       // exactly one start, no double-start

        // First applied gain should be near zero (fading in from silence).
        float firstGain = backend.Volumes[^1];
        Assert.True(firstGain < 0.2f, $"expected near-zero fade-in start, got {firstGain}");
    }

    [Fact]
    public void MusicCrossfadeDuration_AppliesToAutoAdvanceAndPlayTrack()
    {
        // Setting the default duration makes PlayTrack use the fade path (deferred switch), not a hard cut.
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");
        audio.MusicCrossfadeDuration = 1.0f;
        int before = backend.PlayedIndices.Count;

        audio.PlayTrack("b");                       // now routes through the crossfade
        Assert.Equal(before, backend.PlayedIndices.Count);   // deferred, not immediate
        Assert.Equal("a", audio.CurrentTrack);

        for (int i = 0; i < 12; i++) audio.Update(0.1f);
        Assert.Equal("b", audio.CurrentTrack);
    }

    [Fact]
    public void NoArgUpdate_DoesNotProgressFade()
    {
        // The no-arg Update() passes dt=0, so a fade requested through it never advances (documented: a game
        // using crossfade must call Update(dt)). This proves the dt=0 path is inert for the fade.
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");
        audio.CrossfadeTo("b", 1.0f);

        for (int i = 0; i < 100; i++) audio.Update();   // dt = 0 each time
        Assert.Equal("a", audio.CurrentTrack);          // fade never crossed the fade-out: no switch
    }

    [Fact]
    public void DisablingMusicMidFade_CancelsCleanly()
    {
        var (audio, backend) = NewLoaded("a", "b");
        audio.PlayTrack("a");
        audio.CrossfadeTo("b", 1.0f);
        audio.Update(0.25f);                        // mid fade-out

        audio.MusicEnabled = false;                 // must cancel the fade and stop cleanly
        Assert.Null(audio.CurrentTrack);

        // Re-enabling starts fresh at full volume (no lingering fade factor scaling the resume).
        audio.MusicEnabled = true;
        float resumeGain = backend.Volumes[^1];
        Assert.True(resumeGain > 0f, $"expected a normal resume gain, got {resumeGain}");
    }
}
