using System;
using System.IO;
using System.Numerics;
using System.Threading;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class AudioSystemLoopTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "khaozengine-protected-loops", "task-3", Guid.NewGuid().ToString("N"));
    readonly AudioLoopLogger _logger = new();

    public AudioSystemLoopTests()
    {
        Directory.CreateDirectory(_directory);
        WavSynth.WriteTone(Path.Combine(_directory, "wind.wav"), 220f, 0.01f);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    AudioSystem Loaded(RecordingLoopBackend backend, IMusicBackend? music = null)
    {
        var audio = new AudioSystem(music ?? new NullMusicBackend(), backend, logger: _logger);
        audio.RegisterSfx("wind");
        audio.LoadContent(_directory);
        return audio;
    }

    [Fact]
    public void HandleAndThreadBoundariesPrecedeBackendCalls()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        using var other = Loaded(new RecordingLoopBackend(_logger));
        var foreign = other.TryPlayLoop(new("wind")).Handle;
        var expired = audio.TryPlayLoop(new("wind")).Handle;
        Assert.True(audio.StopLoop(expired));
        var current = audio.TryPlayLoop(new("wind")).Handle;
        int calls = sfx.Device.Calls.Count;
        int queries = sfx.IdentityQueries;
        foreach (var invalid in new[] { default(SfxLoopHandle), foreign, expired })
        {
            Assert.False(audio.StopLoop(invalid));
            Assert.False(audio.SetLoopVolume(invalid, 0.5f));
            Assert.False(audio.SetLoopPosition(invalid, Vector3.One));
            Assert.False(audio.IsLoopActive(invalid));
        }
        Assert.Equal(queries, sfx.IdentityQueries);
        Assert.Equal(calls, sfx.Device.Calls.Count);
        Assert.True(audio.IsLoopActive(current));

        AssertOffThread(() => audio.TryPlayLoop(new("missing")));
        AssertOffThread(() => audio.TryPlayLoop(default));
        AssertOffThread(() => audio.StopLoop(default));
        AssertOffThread(() => audio.SetLoopVolume(default, float.NaN));
        AssertOffThread(() => audio.SetLoopPosition(default, new(float.NaN, 0, 0)));
        AssertOffThread(() => audio.IsLoopActive(default));
        AssertOffThread(() => _ = audio.LoopCapacity);
        Assert.Equal(calls, sfx.Device.Calls.Count);
        audio.Dispose();
        AssertOffThread(() => audio.TryPlayLoop(default));
        AssertOffThread(() => audio.StopLoop(default));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void BlankKeysAreRejected(string? key)
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        Assert.Throws<ArgumentException>(() => audio.TryPlayLoop(new(key!)));
        Assert.Empty(sfx.Device.Calls);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteRequestsAndUpdatesAreRejectedBeforeBackendWork(float value)
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.TryPlayLoop(new("wind", Volume: value)));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.TryPlayLoop(new("wind", Pitch: value)));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.TryPlayLoop(new("wind", Position: new(value, 0, 0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.TryPlayLoop(new("wind", Position: new(0, value, 0))));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.TryPlayLoop(new("wind", Position: new(0, 0, value))));
        Assert.Empty(sfx.Device.Calls);
        var handle = audio.TryPlayLoop(new("wind", Position: Vector3.Zero)).Handle;
        int queries = sfx.IdentityQueries;
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.SetLoopVolume(handle, value));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.SetLoopPosition(handle, new(value, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.SetLoopPosition(handle, new(0, value, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.SetLoopPosition(handle, new(0, 0, value)));
        Assert.Equal(queries, sfx.IdentityQueries);
        Assert.Single(sfx.Device.Calls);
    }

    [Fact]
    public void MissingLegacyAndDisposedResultsHaveNoIdentity()
    {
        using var legacy = new AudioSystem(new NullMusicBackend(), new FakeSfxBackend(), logger: _logger);
        legacy.RegisterSfx("wind");
        legacy.LoadContent(_directory);
        Assert.Equal(0, legacy.LoopCapacity);
        AssertFailure(legacy.TryPlayLoop(new("missing")), SfxLoopStartStatus.NotLoaded);
        AssertFailure(legacy.TryPlayLoop(new("wind")), SfxLoopStartStatus.Unsupported);
        var sfx = new RecordingLoopBackend(_logger);
        var audio = Loaded(sfx);
        Assert.Equal(4, audio.LoopCapacity);
        var handle = audio.TryPlayLoop(new("wind")).Handle;
        audio.Dispose();
        int calls = sfx.Device.Calls.Count;
        AssertFailure(audio.TryPlayLoop(default), SfxLoopStartStatus.Disposed);
        Assert.False(audio.StopLoop(handle));
        Assert.False(audio.SetLoopVolume(handle, float.NaN));
        Assert.False(audio.SetLoopPosition(handle, new(float.NaN, 0, 0)));
        Assert.False(audio.IsLoopActive(handle));
        audio.Update(0);
        Assert.Equal(calls, sfx.Device.Calls.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void DefaultAndUnknownBusesUseUnitGain(string? bus)
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        Assert.Equal(SfxLoopStartStatus.Started, audio.TryPlayLoop(new("wind", bus)).Status);
        Assert.Equal(0.66f * 0.7f, Assert.Single(sfx.Device.Starts).Playback.Gain);
    }

    [Fact]
    public void DistinctStartsCaptureAttenuationAndFlatLoopsRefusePosition()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var curve = new SfxAttenuation(3, 0.5f, 90);
        audio.DefineBus("ambient", curve);
        var positional = audio.TryPlayLoop(new("wind", "ambient", Pitch: 1.25f, Position: Vector3.One)).Handle;
        var flat = audio.TryPlayLoop(new("wind", "ambient")).Handle;
        Assert.NotEqual(positional, flat);
        Assert.Equal(curve, sfx.Device.Starts[0].Playback.Attenuation);
        Assert.Equal(1.25f, sfx.Device.Starts[0].Playback.Pitch);
        Assert.True(sfx.Device.Starts[0].Playback.Positional);
        Assert.Equal(Vector3.One, sfx.Device.Starts[0].Playback.Position);
        audio.DefineBus("ambient", new SfxAttenuation(1, 0, 2));
        Assert.True(audio.SetLoopPosition(positional, Vector3.UnitX));
        Assert.Equal(curve, sfx.Device.Starts[0].Playback.Attenuation);
        Assert.False(audio.SetLoopPosition(flat, Vector3.One));
        Assert.Single(sfx.Device.PositionWrites);
    }

    [Fact]
    public void LiveMixerNeverReplays()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        audio.MasterVolume = 0.5f;
        audio.SfxVolume = 0.4f;
        audio.SetBusVolume("ambient", 0.25f);
        var started = audio.TryPlayLoop(new("wind", "ambient", 0.8f));
        Assert.Equal(SfxLoopStartStatus.Started, started.Status);
        Assert.Equal(0.04f, Assert.Single(sfx.Device.Starts).Playback.Gain, 6);
        Assert.True(audio.SetLoopVolume(started.Handle, 0.5f));
        Assert.Equal(0.025f, sfx.Device.GainWrites[^1].Gain, 6);
        audio.SetBusVolume("ambient", 0);
        Assert.Equal(0, sfx.Device.GainWrites[^1].Gain);
        Assert.True(audio.IsLoopActive(started.Handle));
        audio.SetBusVolume("ambient", 0.25f);
        audio.MasterVolume = 0;
        Assert.Equal(0, sfx.Device.GainWrites[^1].Gain);
        audio.MasterVolume = 0.5f;
        audio.SfxVolume = 0;
        Assert.Equal(0, sfx.Device.GainWrites[^1].Gain);
        audio.SfxVolume = 0.4f;
        Assert.Equal(0.025f, sfx.Device.GainWrites[^1].Gain, 6);
        Assert.True(audio.IsLoopActive(started.Handle));
        Assert.Single(sfx.Device.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirtyGainRecoversWithoutMusicOrRestart(bool disabledMusic)
    {
        var sfx = new RecordingLoopBackend(_logger);
        var music = new FakeMusicBackend();
        using var audio = Loaded(sfx, music);
        if (disabledMusic)
        {
            audio.RegisterTrack("track");
            audio.MusicEnabled = false;
            Assert.Equal(1, music.TrackCount);
        }
        else Assert.Equal(0, music.TrackCount);
        audio.SetBusVolume("ambient", 1);
        var handle = audio.TryPlayLoop(new("wind", "ambient")).Handle;
        sfx.Device.GainOutcomes.Enqueue(() => false);
        audio.SetBusVolume("ambient", 0);
        Assert.Equal(0, Assert.Single(sfx.Device.GainWrites).Gain);
        audio.Update(0);
        Assert.Equal(2, sfx.Device.GainWrites.Count);
        Assert.Equal(0, sfx.Device.GainWrites[1].Gain);
        audio.Update(0);
        Assert.Equal(2, sfx.Device.GainWrites.Count);
        Assert.True(audio.IsLoopActive(handle));
        Assert.Single(sfx.Device.Starts);
        Assert.Equal(0, music.UpdateCount);
    }

    [Fact]
    public void PersistentGainFailureRetriesOnlyOncePerExplicitUpdate()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var handle = audio.TryPlayLoop(new("wind")).Handle;
        sfx.Device.GainOutcomes.Enqueue(() => false);
        sfx.Device.GainOutcomes.Enqueue(() => false);
        Assert.False(audio.SetLoopVolume(handle, 0.5f));
        audio.Update(0);
        Assert.Equal(2, sfx.Device.GainWrites.Count);
        audio.Update(0);
        Assert.Equal(3, sfx.Device.GainWrites.Count);
        audio.Update(0);
        Assert.Equal(3, sfx.Device.GainWrites.Count);
        Assert.Single(sfx.Device.Starts);
    }

    [Fact]
    public void FailedGainWriteCannotMakeRestoringThePreviousGainLookClean()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var handle = audio.TryPlayLoop(new("wind")).Handle;
        float originalGain = sfx.Device.Starts[0].Playback.Gain;
        float deviceGain = originalGain;
        sfx.Device.GainOutcomes.Enqueue(() =>
        {
            deviceGain = sfx.Device.GainWrites[^1].Gain;
            return false;
        });
        Assert.False(audio.SetLoopVolume(handle, 0));
        Assert.Equal(0, deviceGain);
        sfx.Device.GainOutcomes.Enqueue(() =>
        {
            deviceGain = sfx.Device.GainWrites[^1].Gain;
            return true;
        });
        Assert.True(audio.SetLoopVolume(handle, 1));
        Assert.Equal(originalGain, deviceGain);
        Assert.Equal(2, sfx.Device.GainWrites.Count);
        audio.Update(0);
        Assert.Equal(2, sfx.Device.GainWrites.Count);
        Assert.Single(sfx.Device.Starts);
    }

    [Theory]
    [InlineData("master")]
    [InlineData("sfx")]
    [InlineData("bus")]
    public void NonFiniteLegacyMixerValuesSilenceLoopsButKeepOneShotSampling(string setting)
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var first = audio.TryPlayLoop(new("wind", "ambient")).Handle;
        switch (setting)
        {
            case "master": audio.MasterVolume = float.NaN; break;
            case "sfx": audio.SfxVolume = float.NaN; break;
            case "bus": audio.SetBusVolume("ambient", float.NaN); break;
        }
        Assert.Equal(0, sfx.Device.GainWrites[^1].Gain);
        Assert.Equal(SfxLoopStartStatus.Started, audio.TryPlayLoop(new("wind", "ambient")).Status);
        Assert.Equal(0, sfx.Device.Starts[1].Playback.Gain);
        audio.PlaySfx("wind", bus: "ambient");
        Assert.True(float.IsNaN(Assert.Single(sfx.OneShotGains)));
        Assert.True(audio.IsLoopActive(first));
    }

    [Fact]
    public void CapacityAndFailedStartPublishNoMixerIdentityOrHiddenRetry()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        sfx.Device.StartOutcomes.Enqueue(() => false);
        AssertFailure(audio.TryPlayLoop(new("wind")), SfxLoopStartStatus.BackendFailure);
        audio.Update(0);
        Assert.Single(sfx.Device.Starts);
        for (int i = 0; i < 4; i++) Assert.Equal(SfxLoopStartStatus.Started, audio.TryPlayLoop(new("wind")).Status);
        int calls = sfx.Device.Calls.Count;
        AssertFailure(audio.TryPlayLoop(new("wind")), SfxLoopStartStatus.Capacity);
        Assert.Equal(calls, sfx.Device.Calls.Count);
    }

    [Fact]
    public void StopAllReconcilesEndedLoopsAndRetainsFailedStopsForExplicitRetry()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var first = audio.TryPlayLoop(new("wind")).Handle;
        var second = audio.TryPlayLoop(new("wind")).Handle;
        sfx.Device.StopOutcomes.Enqueue(() => false);
        audio.StopAllSfx();
        Assert.True(audio.IsLoopActive(first));
        Assert.False(audio.IsLoopActive(second));
        Assert.True(audio.StopLoop(first));
        var replacement = audio.TryPlayLoop(new("wind")).Handle;
        Assert.False(audio.SetLoopVolume(first, 0));
        Assert.True(audio.IsLoopActive(replacement));
    }

    [Fact]
    public void BackendEndedIdentityCannotControlAReplacement()
    {
        var sfx = new RecordingLoopBackend(_logger);
        using var audio = Loaded(sfx);
        var old = audio.TryPlayLoop(new("wind")).Handle;
        sfx.StopAll();
        var current = audio.TryPlayLoop(new("wind")).Handle;
        int calls = sfx.Device.Calls.Count;
        Assert.False(audio.SetLoopVolume(old, 0));
        Assert.False(audio.SetLoopPosition(old, Vector3.One));
        Assert.False(audio.StopLoop(old));
        Assert.True(audio.IsLoopActive(current));
        Assert.Equal(calls, sfx.Device.Calls.Count);
    }

    static void AssertFailure(SfxLoopStartResult result, SfxLoopStartStatus status)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(default, result.Handle);
    }

    static void AssertOffThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() => caught = Record.Exception(action));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.IsType<InvalidOperationException>(caught);
    }
}
