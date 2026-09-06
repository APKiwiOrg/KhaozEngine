using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// Headless tests for the SFX bus registry: gain composition (master*sfx*bus*volume), unknown-bus fallback,
/// default-bus equivalence with the pre-bus behavior, and the documented "applies on next play" limitation
/// (the fire-and-forget <see cref="ISfxBackend"/> seam has no per-voice re-gain).
/// </summary>
public sealed class AudioSystemBusTests : IDisposable
{
    private readonly string _dir;

    public AudioSystemBusTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ke-bus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        WavSynth.WriteTone(Path.Combine(_dir, "blip.wav"), 440f, 0.05f);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (AudioSystem audio, FakeSfxBackend sfx) NewLoaded()
    {
        var sfx = new FakeSfxBackend();
        var audio = new AudioSystem(new NullMusicBackend(), sfx);
        audio.RegisterSfx("blip");
        audio.LoadContent(_dir);
        return (audio, sfx);
    }

    [Fact]
    public void DefinedBusDefaultsToUnitVolume()
    {
        var (audio, _) = NewLoaded();
        audio.DefineBus("ui");
        Assert.Equal(1f, audio.GetBusVolume("ui"));
    }

    [Fact]
    public void UnknownBusVolumeIsUnity()
    {
        var (audio, _) = NewLoaded();
        Assert.Equal(1f, audio.GetBusVolume("never-defined"));
        Assert.Equal(1f, audio.GetBusVolume(""));
        Assert.Equal(1f, audio.GetBusVolume(null!));
    }

    [Fact]
    public void SetBusVolumeClampsToUnitRange()
    {
        var (audio, _) = NewLoaded();
        audio.DefineBus("combat");

        audio.SetBusVolume("combat", 2.5f);
        Assert.Equal(1f, audio.GetBusVolume("combat"));

        audio.SetBusVolume("combat", -1f);
        Assert.Equal(0f, audio.GetBusVolume("combat"));

        audio.SetBusVolume("combat", 0.25f);
        Assert.Equal(0.25f, audio.GetBusVolume("combat"));
    }

    [Fact]
    public void SetBusVolumeDefinesTheBusIfMissing()
    {
        var (audio, _) = NewLoaded();
        // No DefineBus first: SetBusVolume creates it.
        audio.SetBusVolume("ambience", 0.3f);
        Assert.Equal(0.3f, audio.GetBusVolume("ambience"));
    }

    [Fact]
    public void ReDefiningBusPreservesItsVolume()
    {
        var (audio, _) = NewLoaded();
        audio.DefineBus("ui");
        audio.SetBusVolume("ui", 0.4f);

        audio.DefineBus("ui");   // must NOT reset to 1.0
        Assert.Equal(0.4f, audio.GetBusVolume("ui"));
    }

    [Fact]
    public void PlayWithoutBusMatchesPreBusGain()
    {
        var (audio, sfx) = NewLoaded();
        // Defaults: Master 0.66, Sfx 0.7. No bus argument => byte-for-byte the old master*sfx*volume.
        audio.PlaySfx("blip", 0.5f);

        var call = Assert.Single(sfx.Plays);
        Assert.Equal(0.66f * 0.7f * 0.5f, call.Gain, 4);
    }

    [Fact]
    public void PlayOnBusMultipliesByBusVolume()
    {
        var (audio, sfx) = NewLoaded();
        audio.DefineBus("ui");
        audio.SetBusVolume("ui", 0.5f);

        audio.PlaySfx("blip", 1f, 1f, bus: "ui");

        var call = Assert.Single(sfx.Plays);
        // master * sfx * bus * volume
        Assert.Equal(0.66f * 0.7f * 0.5f * 1f, call.Gain, 4);
    }

    [Fact]
    public void Play3DOnBusMultipliesByBusVolume()
    {
        var (audio, sfx) = NewLoaded();
        audio.DefineBus("combat");
        audio.SetBusVolume("combat", 0.25f);
        var pos = new Vector3(1, 2, 3);

        audio.PlaySfx3D("blip", pos, 0.8f, 1f, bus: "combat");

        var call = Assert.Single(sfx.Plays);
        Assert.True(call.Positional);
        Assert.Equal(pos, call.Position);
        Assert.Equal(0.66f * 0.7f * 0.25f * 0.8f, call.Gain, 4);
    }

    [Fact]
    public void UnknownBusOnPlayFallsBackToDefaultUnityBus()
    {
        var (audio, sfx) = NewLoaded();
        // Never defined "typo" bus: must NOT throw and must play at the default-bus (1.0) gain.
        audio.PlaySfx("blip", 1f, 1f, bus: "typo");

        var call = Assert.Single(sfx.Plays);
        Assert.Equal(0.66f * 0.7f * 1f, call.Gain, 4);
    }

    [Fact]
    public void BusVolumeChangeAppliesToSubsequentPlaysNotPastOnes()
    {
        var (audio, sfx) = NewLoaded();
        audio.DefineBus("ui");

        // First play at full bus.
        audio.PlaySfx("blip", 1f, 1f, bus: "ui");
        // Lower the bus, then play again.
        audio.SetBusVolume("ui", 0.2f);
        audio.PlaySfx("blip", 1f, 1f, bus: "ui");

        Assert.Equal(2, sfx.Plays.Count);
        // The FIRST voice keeps the gain it was started with (fire-and-forget seam: no live re-gain).
        Assert.Equal(0.66f * 0.7f * 1f, sfx.Plays[0].Gain, 4);
        // The SECOND voice reflects the new bus volume.
        Assert.Equal(0.66f * 0.7f * 0.2f, sfx.Plays[1].Gain, 4);
    }

    [Fact]
    public void FirstAvailableOverloadHonoursBus()
    {
        var (audio, sfx) = NewLoaded();
        audio.DefineBus("ui");
        audio.SetBusVolume("ui", 0.5f);

        bool played = audio.PlaySfx(new[] { "missing", "blip" }, 1f, 1f, bus: "ui");

        Assert.True(played);
        var call = Assert.Single(sfx.Plays);
        Assert.Equal(0.66f * 0.7f * 0.5f, call.Gain, 4);
    }

    [Fact]
    public void MasterAndSfxVolumeStillComposeWithBus()
    {
        var (audio, sfx) = NewLoaded();
        audio.MasterVolume = 0.5f;
        audio.SfxVolume = 0.8f;
        audio.DefineBus("ambience");
        audio.SetBusVolume("ambience", 0.6f);

        audio.PlaySfx("blip", 0.9f, 1f, bus: "ambience");

        var call = Assert.Single(sfx.Plays);
        Assert.Equal(0.5f * 0.8f * 0.6f * 0.9f, call.Gain, 4);
    }

    [Fact]
    public void PositionalPlayOnDefaultBusUsesLegacyAttenuation()
    {
        var (audio, sfx) = NewLoaded();

        audio.PlaySfx3D("blip", Vector3.One);

        Assert.Equal(new SfxAttenuation(1f, 1f, 50f), Assert.Single(sfx.Plays).Attenuation);
    }

    [Fact]
    public void PositionalPlayOnDefinedBusCarriesCustomAttenuation()
    {
        var (audio, sfx) = NewLoaded();
        var attenuation = new SfxAttenuation(8f, 0.4f, 120f);
        audio.DefineBus("world", attenuation);

        audio.PlaySfx3D("blip", Vector3.One, bus: "world");

        Assert.Equal(attenuation, Assert.Single(sfx.Plays).Attenuation);
    }

    [Fact]
    public void PositionalPlayOnUnknownBusUsesLegacyAttenuation()
    {
        var (audio, sfx) = NewLoaded();

        audio.PlaySfx3D("blip", Vector3.One, bus: "missing");

        Assert.Equal(SfxAttenuation.Default, Assert.Single(sfx.Plays).Attenuation);
    }

    [Fact]
    public void NonPositionalPlayDoesNotSendAttenuation()
    {
        var (audio, sfx) = NewLoaded();
        audio.DefineBus("ui", new SfxAttenuation(8f, 0.4f, 120f));

        audio.PlaySfx("blip", bus: "ui");

        Assert.Null(Assert.Single(sfx.Plays).Attenuation);
    }

    [Theory]
    [InlineData(0f, 1f, 50f)]
    [InlineData(-1f, 1f, 50f)]
    [InlineData(1f, -1f, 50f)]
    [InlineData(2f, 1f, 1f)]
    public void AttenuationRejectsInvalidCurve(float referenceDistance, float rolloffFactor, float maxDistance)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SfxAttenuation(referenceDistance, rolloffFactor, maxDistance));
    }
}
