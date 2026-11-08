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

    private static void AssertThrowsOffThread(string member, Action action)
    {
        Exception? caught = RunOffThread(action);
        AudioThreadAffinityException refused = Assert.IsType<AudioThreadAffinityException>(caught);
        Assert.Equal(AudioThreadAffinityRefusal.ThreadMismatch, refused.Reason);
        Assert.Equal(member, refused.MemberName);
    }

    [Theory]
    [InlineData("blip")]
    [InlineData("not-registered")]
    public void PlaySfxOffTheOwningThreadThrows(string name)
    {
        // Even an unknown name must fail the thread guard before reaching the warn-once lookup path.
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.PlaySfx), () => audio.PlaySfx(name));
        AssertThrowsOffThread("PlaySfx", () => audio.PlaySfx(name, SfxPriority.Normal));
    }

    [Fact]
    public void PlaySfx3DOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.PlaySfx3D), () => audio.PlaySfx3D("blip", Vector3.Zero));
        AssertThrowsOffThread("PlaySfx3D", () => audio.PlaySfx3D("blip", Vector3.Zero, SfxPriority.Normal));
    }

    [Fact]
    public void FirstAvailablePlayOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.PlaySfx), () => audio.PlaySfx(new[] { "blip" }));
        AssertThrowsOffThread("PlaySfx", () => audio.PlaySfx(new[] { "blip" }, SfxPriority.Normal));
        AssertThrowsOffThread("PlaySfx3D", () => audio.PlaySfx3D(new[] { "blip" }, Vector3.Zero, SfxPriority.Normal));
    }

    [Fact]
    public void RegistryMutationOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.RegisterSfx), () => audio.RegisterSfx("second"));
        AssertThrowsOffThread(nameof(AudioSystem.UnregisterSfx), () => audio.UnregisterSfx("blip"));
        AssertThrowsOffThread(nameof(AudioSystem.RegisterTrack), () => audio.RegisterTrack("theme"));
        // The bulk members have no guard of their own, so the refusal names the per-item member they call.
        // The sequences are non-empty because an empty one never reaches that member.
        AssertThrowsOffThread("RegisterSfx", () => audio.RegisterSfxes(new[] { "second" }));
        AssertThrowsOffThread("UnregisterSfx", () => audio.UnregisterSfxes(new[] { "blip" }));
        AssertThrowsOffThread("RegisterTrack", () => audio.RegisterTracks(new[] { "theme" }));
        AssertThrowsOffThread(nameof(AudioSystem.LoadContent), () => audio.LoadContent(_dir));
    }

    [Fact]
    public void PlaybackAndTickOffTheOwningThreadThrow()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.Update), () => audio.Update());
        AssertThrowsOffThread(nameof(AudioSystem.Update), () => audio.Update(0.016f));
        AssertThrowsOffThread(nameof(AudioSystem.PlayRandomTrack), audio.PlayRandomTrack);
        AssertThrowsOffThread(nameof(AudioSystem.PlayTrack), () => audio.PlayTrack(0));
        AssertThrowsOffThread(nameof(AudioSystem.PlayTrack), () => audio.PlayTrack("theme"));
        AssertThrowsOffThread(nameof(AudioSystem.CrossfadeTo), () => audio.CrossfadeTo("theme", 1f));
        AssertThrowsOffThread(nameof(AudioSystem.CrossfadeTo), () => audio.CrossfadeTo(0, 1f));
        AssertThrowsOffThread(nameof(AudioSystem.StopAllSfx), audio.StopAllSfx);
        AssertThrowsOffThread(nameof(AudioSystem.SetListener), () => audio.SetListener(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY));
    }

    [Fact]
    public void SettingsMutationOffTheOwningThreadThrows()
    {
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.MasterVolume), () => audio.MasterVolume = 0.5f);
        AssertThrowsOffThread(nameof(AudioSystem.MusicVolume), () => audio.MusicVolume = 0.5f);
        AssertThrowsOffThread(nameof(AudioSystem.SfxVolume), () => audio.SfxVolume = 0.5f);
        AssertThrowsOffThread(nameof(AudioSystem.MusicEnabled), () => audio.MusicEnabled = false);
        AssertThrowsOffThread(nameof(AudioSystem.MusicCrossfadeDuration), () => audio.MusicCrossfadeDuration = 1f);
        AssertThrowsOffThread(nameof(AudioSystem.PlayMode), () => audio.PlayMode = PlayMode.RepeatOne);
        AssertThrowsOffThread(nameof(AudioSystem.SetRotationPool), () => audio.SetRotationPool(new[] { "theme" }));
        AssertThrowsOffThread(nameof(AudioSystem.SetRng), () => audio.SetRng(new KhaozEngine.Primitives.DeterministicRng(7)));
        AssertThrowsOffThread(nameof(AudioSystem.DefineBus), () => audio.DefineBus("ui"));
        AssertThrowsOffThread("DefineBus", () => audio.DefineBus("ui", SfxAttenuation.Default));
        AssertThrowsOffThread(nameof(AudioSystem.SetBusVolume), () => audio.SetBusVolume("ui", 0.5f));
    }

    [Fact]
    public void TheRefusalNamesTheCalledMemberAndBothThreads_AndChangesNothing()
    {
        using AudioSystem audio = NewLoaded();
        audio.SetBusVolume("ui", 0.25f);
        int owner = Environment.CurrentManagedThreadId;
        int caller = 0;

        Exception? caught = RunOffThread(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            audio.SetBusVolume("ui", 0.5f);
        });

        Assert.IsAssignableFrom<InvalidOperationException>(caught);
        var refused = Assert.IsType<AudioThreadAffinityException>(caught);
        Assert.Equal(AudioThreadAffinityRefusal.ThreadMismatch, refused.Reason);
        Assert.Equal("SetBusVolume", refused.MemberName);
        Assert.Equal(owner, refused.OwnerThreadId);
        Assert.Equal(caller, refused.CallerThreadId);
        Assert.Equal(0.25f, audio.GetBusVolume("ui"));

        // Control: the same call from the thread the refusal names as the owner is admitted.
        audio.SetBusVolume("ui", 0.5f);
        Assert.Equal(0.5f, audio.GetBusVolume("ui"));
    }

    [Fact]
    public void PositionalPlayIsNamedAsSuch()
    {
        // PlaySfx and PlaySfx3D share one private play path, so the guard has to say which public overload the
        // caller actually used rather than leaking the helper's name.
        using AudioSystem audio = NewLoaded();

        AssertThrowsOffThread("PlaySfx3D", () => audio.PlaySfx3D("blip", Vector3.One));
        AssertThrowsOffThread("PlaySfx3D", () => audio.PlaySfx3D(new[] { "blip" }, Vector3.One));

        // Control: the same positional call from the owning thread is admitted.
        audio.PlaySfx3D("blip", Vector3.One);
    }

    [Fact]
    public void TheOwningThreadIsUnaffected()
    {
        // The guard must not cost the ordinary main-thread frame loop anything, including after an off-thread
        // call has already been rejected.
        using AudioSystem audio = NewLoaded();
        AssertThrowsOffThread(nameof(AudioSystem.PlaySfx), () => audio.PlaySfx("blip"));

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
        int builder = 0;
        Exception? ctorFailure = RunOffThread(() =>
        {
            builder = Environment.CurrentManagedThreadId;
            built = new AudioSystem(new FakeMusicBackend(), new FakeSfxBackend());
        });
        Assert.Null(ctorFailure);
        Assert.NotNull(built);

        var refused = Assert.Throws<AudioThreadAffinityException>(() => built!.RegisterTrack("theme"));
        Assert.Equal(AudioThreadAffinityRefusal.ThreadMismatch, refused.Reason);
        Assert.Equal(builder, refused.OwnerThreadId);
        Assert.Equal(Environment.CurrentManagedThreadId, refused.CallerThreadId);
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
