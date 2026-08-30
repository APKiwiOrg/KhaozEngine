using System;
using System.IO;
using System.Numerics;
using System.Threading;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless tests for the main-thread-only contract on <see cref="AudioSystem"/> (#115): every mutating entry
/// point throws when called off the thread that constructed the instance, the owning thread keeps working, and
/// <see cref="AudioSystem.Dispose"/> stays exempt so a shutdown path elsewhere is not turned into a crash.
/// <para>The off-thread call runs on a dedicated <see cref="Thread"/> rather than the pool, so the assertion
/// cannot accidentally land back on the constructing thread and pass for the wrong reason.</para>
/// </summary>
public sealed class AudioSystemThreadAffinityTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemThreadAffinityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-audio-thread-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        WavSynth.WriteTone(Path.Combine(_dir, "blip.wav"), 440f, 0.05f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private AudioSystem NewLoaded()
    {
        var audio = new AudioSystem(new FakeMusicBackend(), new FakeSfxBackend());
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);
        return audio;
    }

    /// <summary>Runs <paramref name="action"/> on a fresh thread and returns whatever it threw, or null.</summary>
    private static Exception? RunOffThread(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { caught = ex; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the off-thread call never finished");
        return caught;
    }

    private static void AssertThrowsOffThread(Action action)
    {
        Exception? caught = RunOffThread(action);
        var invalid = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("main-thread-only", invalid.Message);
    }

    [Fact]
    public void PlaySfxOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.PlaySfx("blip"));
    }

    [Fact]
    public void PlaySfx3DOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.PlaySfx3D("blip", Vector3.Zero));
    }

    [Fact]
    public void FirstAvailablePlayOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.PlaySfx(new[] { "blip" }));
    }

    [Fact]
    public void UnknownSfxNameStillThrowsOffThread()
    {
        // The guard runs BEFORE the name lookup, so the off-thread caller is told about the thread rather than
        // getting the unknown-name warn-once no-op it would get on the owning thread.
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.PlaySfx("not-registered"));
    }

    [Fact]
    public void RegistryMutationOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.RegisterSfx("second"));
        AssertThrowsOffThread(() => audio.UnregisterSfx("blip"));
        AssertThrowsOffThread(() => audio.RegisterTrack("theme"));
        AssertThrowsOffThread(() => audio.LoadContent(_dir));
    }

    [Fact]
    public void PlaybackAndTickOffTheOwningThreadThrow()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.Update());
        AssertThrowsOffThread(() => audio.Update(0.016f));
        AssertThrowsOffThread(audio.PlayRandomTrack);
        AssertThrowsOffThread(() => audio.PlayTrack(0));
        AssertThrowsOffThread(() => audio.PlayTrack("theme"));
        AssertThrowsOffThread(() => audio.CrossfadeTo("theme", 1f));
        AssertThrowsOffThread(() => audio.CrossfadeTo(0, 1f));
        AssertThrowsOffThread(audio.StopAllSfx);
        AssertThrowsOffThread(() => audio.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
    }

    [Fact]
    public void SettingsMutationOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.MasterVolume = 0.5f);
        AssertThrowsOffThread(() => audio.MusicVolume = 0.5f);
        AssertThrowsOffThread(() => audio.SfxVolume = 0.5f);
        AssertThrowsOffThread(() => audio.MusicEnabled = false);
        AssertThrowsOffThread(() => audio.MusicCrossfadeDuration = 1f);
        AssertThrowsOffThread(() => audio.PlayMode = PlayMode.RepeatOne);
        AssertThrowsOffThread(() => audio.SetRotationPool(new[] { "theme" }));
        AssertThrowsOffThread(() => audio.SetRng(new KhaozEngine.Primitives.DeterministicRng(7)));
        AssertThrowsOffThread(() => audio.DefineBus("ui"));
        AssertThrowsOffThread(() => audio.SetBusVolume("ui", 0.5f));
    }

    [Fact]
    public void TheMessageNamesTheCalledMemberAndBothThreads()
    {
        using AudioSystem audio = NewLoaded();
        int owner = Environment.CurrentManagedThreadId;

        Exception? caught = RunOffThread(() => audio.DefineBus("ui"));

        var invalid = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("AudioSystem.DefineBus", invalid.Message);
        Assert.Contains($"owned by thread {owner}", invalid.Message);
    }

    [Fact]
    public void PositionalPlayIsNamedAsSuchInTheMessage()
    {
        // PlaySfx and PlaySfx3D share one private play path, so the guard has to say which public overload the
        // caller actually used rather than leaking the helper's name.
        using AudioSystem audio = NewLoaded();

        Exception? caught = RunOffThread(() => audio.PlaySfx3D("blip", Vector3.One));

        var invalid = Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains("AudioSystem.PlaySfx3D", invalid.Message);
    }

    [Fact]
    public void TheOwningThreadIsUnaffected()
    {
        // The guard must not cost the ordinary main-thread frame loop anything, including after an off-thread
        // call has already been rejected.
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(() => audio.PlaySfx("blip"));

        audio.DefineBus("ui");
        audio.SetBusVolume("ui", 0.25f);
        audio.MasterVolume = 0.5f;
        audio.PlaySfx("blip", bus: "ui");
        audio.Update(0.016f);

        Assert.Equal(0.25f, audio.GetBusVolume("ui"));
        Assert.Equal(0.5f, audio.MasterVolume);
    }

    [Fact]
    public void ConstructingThreadIsTheOwnerNotTheFirstCaller()
    {
        // The owner latches in the constructor, so an instance built on a loader thread stays owned by THAT
        // thread. This pins which of the two possible affinity rules the class implements.
        AudioSystem? built = null;
        Exception? ctorFailure = RunOffThread(() => built = new AudioSystem(new FakeMusicBackend(), new FakeSfxBackend()));
        Assert.Null(ctorFailure);
        Assert.NotNull(built);

        Assert.Throws<InvalidOperationException>(() => built!.RegisterTrack("theme"));
        built!.Dispose();
    }

    [Fact]
    public void DisposeIsExemptFromTheGuard()
    {
        // A shutdown path on another thread must not be turned into an unhandled exception (see Dispose's
        // remarks). This is the one deliberate hole in the contract.
        AudioSystem audio = NewLoaded();

        Exception? caught = RunOffThread(audio.Dispose);

        Assert.Null(caught);
    }
}
