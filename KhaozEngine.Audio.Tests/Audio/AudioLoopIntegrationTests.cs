using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.Audio;
using Silk.NET.OpenAL;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class AudioLoopIntegrationTests : IDisposable
{
    readonly string _directory = Path.Combine(Path.GetTempPath(), "khaozengine-protected-loops", "task-4", Guid.NewGuid().ToString("N"));
    readonly AudioLoopLogger _logger = new();

    public AudioLoopIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        WavSynth.WriteTone(Path.Combine(_directory, "tone.wav"), 220f, 0.01f);
        WavSynth.WriteTone(Path.Combine(_directory, "other.wav"), 440f, 0.01f);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    AudioSystem Loaded(ISfxBackend backend, AudioLoopLogger? logger = null)
    {
        var audio = new AudioSystem(new NullMusicBackend(), backend, logger: logger ?? _logger);
        audio.RegisterSfxes(new[] { "tone", "other" });
        audio.LoadContent(_directory);
        return audio;
    }

    static SfxLoopHandle Start(AudioSystem audio, string key = "tone")
    {
        var result = audio.TryPlayLoop(new(key));
        Assert.Equal(SfxLoopStartStatus.Started, result.Status);
        return result.Handle;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedUnloadRetainsRegistration(bool stopThrows)
    {
        var backend = new RecordingLoopBackend(_logger);
        using var audio = Loaded(backend);
        var handle = Start(audio);
        backend.Device.StopOutcomes.Enqueue(() => stopThrows ? throw new InvalidOperationException("stop failed") : false);

        bool released = audio.UnregisterSfx("tone");

        // Retaining the mapping is the causal regression assertion for remove-before-unload.
        Assert.True(audio.IsSfxLoaded("tone"));
        Assert.False(released);
        Assert.True(audio.IsLoopActive(handle));
        Assert.Contains(0, backend.LoadedBuffers);
        audio.RegisterSfx("tone");
        Assert.Equal(2, backend.LoadedBuffers.Count);
        Assert.Single(backend.Device.Starts);

        Assert.True(audio.UnregisterSfx("tone"));
        Assert.False(audio.IsSfxLoaded("tone"));
        Assert.False(audio.IsLoopActive(handle));
        Assert.DoesNotContain(0, backend.LoadedBuffers);
        Assert.False(audio.UnregisterSfx("tone"));
    }

    [Fact]
    public void PartialUnloadKeepsRemainingOwnership()
    {
        var backend = new RecordingLoopBackend(_logger);
        using var audio = Loaded(backend);
        var stopped = Start(audio);
        var remaining = Start(audio);
        var other = Start(audio, "other");
        backend.Device.StopOutcomes.Enqueue(() => true);
        backend.Device.StopOutcomes.Enqueue(() => false);

        Assert.False(audio.UnregisterSfx("tone"));
        Assert.True(audio.IsSfxLoaded("tone"));
        Assert.Equal(2, backend.LoadedBuffers.Count);
        // Reconciliation must already have forgotten the ended mixer record, before a public query.
        backend.CompanionCalls.Clear();
        Assert.False(audio.SetLoopVolume(stopped, 0.2f));
        Assert.Empty(backend.CompanionCalls);
        Assert.True(audio.IsLoopActive(remaining));
        Assert.True(audio.IsLoopActive(other));
        Assert.Equal(3, backend.Device.Starts.Count);

        Assert.True(audio.UnregisterSfx("tone"));
        Assert.False(audio.IsLoopActive(stopped));
        Assert.False(audio.IsLoopActive(remaining));
        Assert.True(audio.IsLoopActive(other));
        Assert.True(audio.IsSfxLoaded("other"));
        Assert.Equal(new[] { 1 }, backend.LoadedBuffers);
        Assert.Equal(3, backend.Device.Starts.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowingCompanionReconcilesPartialCleanupAndRetainsRegistration(bool loggerThrows)
    {
        var logger = new AudioLoopLogger { ThrowOnWarning = loggerThrows };
        var inner = new RecordingLoopBackend(logger);
        var backend = new ThrowingUnloadBackend(inner);
        using var audio = Loaded(backend, logger);
        var stopped = Start(audio);
        var remaining = Start(audio);
        backend.BeforeUnload = () =>
        {
            inner.StopLoop(stopped.BackendHandle);
            throw new InvalidOperationException("unload failed after partial cleanup");
        };

        Assert.False(audio.UnregisterSfx("tone"));
        Assert.True(audio.IsSfxLoaded("tone"));
        inner.CompanionCalls.Clear();
        Assert.False(audio.StopLoop(stopped));
        Assert.Empty(inner.CompanionCalls);
        Assert.True(audio.IsLoopActive(remaining));
        Assert.Contains(0, inner.LoadedBuffers);
        Assert.Equal(0, backend.LegacyUnloadCalls);

        backend.BeforeUnload = null;
        Assert.True(audio.UnregisterSfx("tone"));
        Assert.False(audio.IsLoopActive(remaining));
        Assert.Equal(0, backend.LegacyUnloadCalls);
    }

    [Fact]
    public void BatchCountsOnlyConfirmedReleases()
    {
        var backend = new RecordingLoopBackend(_logger);
        using var audio = Loaded(backend);
        var retained = Start(audio);
        var released = Start(audio, "other");
        backend.Device.StopOutcomes.Enqueue(() => false);

        Assert.Equal(1, audio.UnregisterSfxes(new[] { "tone", "missing", "other", "other" }));
        Assert.True(audio.IsSfxLoaded("tone"));
        Assert.True(audio.IsLoopActive(retained));
        Assert.False(audio.IsSfxLoaded("other"));
        Assert.False(audio.IsLoopActive(released));
        Assert.Equal(1, audio.UnregisterSfxes(new[] { "tone", "tone" }));
        Assert.False(audio.IsLoopActive(retained));
        Assert.Empty(backend.LoadedBuffers);
    }

    [Fact]
    public void NativeUnloadDetachesAllReferencingSourcesBeforeDeletingBuffer()
    {
        var commands = new RecordingSfxAl();
        var backend = new OpenAlSfxBackend(commands, _logger);
        using var audio = Loaded(backend);
        var first = Start(audio);
        var second = Start(audio);
        var other = Start(audio, "other");
        audio.PlaySfx("tone");
        uint buffer = commands.Plays[0].State.Buffer;
        uint otherSource = commands.Plays[2].Source;
        uint[] referencing = commands.Sources.Where(pair => pair.Value.Buffer == buffer).Select(pair => pair.Key).ToArray();
        Assert.Equal(3, referencing.Length);
        commands.Calls.Clear();

        Assert.True(audio.UnregisterSfx("tone"));

        int deletion = commands.Calls.FindIndex(call => call == ("DeleteBuffer", buffer));
        Assert.True(deletion >= 0);
        foreach (uint source in referencing)
        {
            int stop = commands.Calls.FindIndex(call => call == ("SourceStop", source));
            int detach = commands.Calls.FindIndex(call => call == ("BindBuffer", source));
            Assert.True(stop >= 0 && stop < detach && detach < deletion);
            Assert.Equal(0u, commands.Sources[source].Buffer);
            Assert.Equal(SourceState.Stopped, commands.Sources[source].State);
        }
        Assert.DoesNotContain(buffer, commands.LiveBuffers);
        Assert.Single(commands.LiveBuffers);
        Assert.False(audio.IsLoopActive(first));
        Assert.False(audio.IsLoopActive(second));
        Assert.True(audio.IsLoopActive(other));
        Assert.Equal(SourceState.Playing, commands.Sources[otherSource].State);
        Assert.DoesNotContain(commands.Calls, call => call == ("SourceStop", otherSource));
    }

    [Fact]
    public void ReusedNumericBufferCannotReviveOldFacadeIdentity()
    {
        var commands = new RecordingSfxAl();
        var backend = new OpenAlSfxBackend(commands, _logger);
        using var audio = Loaded(backend);
        var old = Start(audio);
        uint originalBuffer = commands.Plays[0].State.Buffer;
        Assert.True(audio.UnregisterSfx("tone"));
        audio.RegisterSfx("tone");
        var current = Start(audio);
        Assert.Equal(old.BackendHandle.Slot, current.BackendHandle.Slot);
        Assert.NotEqual(old, current);
        Assert.NotEqual(originalBuffer, commands.Plays[^1].State.Buffer);
        commands.Calls.Clear();

        Assert.False(audio.StopLoop(old));
        Assert.False(audio.SetLoopVolume(old, 0));
        Assert.False(audio.SetLoopPosition(old, Vector3.One));
        Assert.False(audio.IsLoopActive(old));
        Assert.Empty(commands.Calls);
        Assert.True(audio.IsLoopActive(current));
        // The initial key occupied numeric backend buffer zero. Its reload must reuse that slot.
        Assert.True(backend.TryUnloadSfx(0));
        Assert.False(audio.IsLoopActive(current));
        Assert.True(audio.IsSfxLoaded("other"));
    }

    [Fact]
    public void NativeFacadeDisposalAfterFailedUnloadReleasesSourcesBeforeBuffers()
    {
        var commands = new RecordingSfxAl();
        var backend = new OpenAlSfxBackend(commands, _logger);
        using var audio = Loaded(backend);
        var handle = Start(audio);
        commands.FailNext("SourceStop", AudioError.InvalidOperation);
        Assert.False(audio.UnregisterSfx("tone"));
        Assert.True(audio.IsSfxLoaded("tone"));
        Assert.True(audio.IsLoopActive(handle));
        commands.Calls.Clear();

        audio.Dispose();

        Assert.Empty(commands.LiveSources);
        Assert.Empty(commands.LiveBuffers);
        int lastSource = commands.Calls.FindLastIndex(call => call.Operation == "DeleteSource");
        int firstBuffer = commands.Calls.FindIndex(call => call.Operation == "DeleteBuffer");
        Assert.True(lastSource >= 0 && firstBuffer > lastSource);
        int calls = commands.Calls.Count;
        Assert.False(audio.IsLoopActive(handle));
        Assert.False(audio.StopLoop(handle));
        Assert.Equal(SfxLoopStartStatus.Disposed, audio.TryPlayLoop(new("tone")).Status);
        Assert.Equal(calls, commands.Calls.Count);
    }

    [Fact]
    public void LegacyBackendKeepsVoidUnloadAndUnsupportedLoopBehavior()
    {
        var backend = new FakeSfxBackend();
        using var audio = Loaded(backend);
        Assert.Equal(0, audio.LoopCapacity);
        Assert.Equal(SfxLoopStartStatus.Unsupported, audio.TryPlayLoop(new("tone")).Status);
        Assert.True(audio.UnregisterSfx("tone"));
        Assert.Equal(new[] { 0 }, backend.Unloaded);
        Assert.False(audio.IsSfxLoaded("tone"));
        audio.PlaySfx("tone");
        Assert.Empty(backend.Plays);
        audio.RegisterSfx("tone");
        audio.PlaySfx("tone");
        Assert.Equal(2, Assert.Single(backend.Plays).Handle);
        Assert.Equal(1, audio.UnregisterSfxes(new[] { "tone", "tone", "missing" }));
        Assert.Equal(new[] { 0, 2 }, backend.Unloaded);
    }

    [Fact]
    public void RegisteredButUnloadedNameIsRemovedWithoutClaimingRelease()
    {
        var backend = new RecordingLoopBackend(_logger);
        using var audio = Loaded(backend);
        audio.RegisterSfx("late");
        Assert.False(audio.UnregisterSfx("late"));
        Assert.False(audio.UnregisterSfx("unknown"));
        WavSynth.WriteTone(Path.Combine(_directory, "late.wav"), 660f, 0.01f);
        audio.LoadContent(_directory);
        Assert.False(audio.IsSfxLoaded("late"));
        Assert.Equal(2, backend.LoadedBuffers.Count);
        audio.RegisterSfx("late");
        Assert.True(audio.IsSfxLoaded("late"));
    }

    [Fact]
    public void NullFacadeUnloadExpiresLoopsAndReturnsProtectedCapacity()
    {
        using var audio = Loaded(new NullSfxBackend());
        var handles = Enumerable.Range(0, 4).Select(_ => Start(audio)).ToArray();
        Assert.Equal(4, audio.LoopCapacity);
        Assert.Equal(SfxLoopStartStatus.Capacity, audio.TryPlayLoop(new("other")).Status);
        Assert.True(audio.UnregisterSfx("tone"));
        Assert.All(handles, handle => Assert.False(audio.IsLoopActive(handle)));
        Assert.True(audio.IsLoopActive(Start(audio, "other")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResourcePreservingDeviceFollowingKeepsFullLoopOwnership(bool failFirstReopen)
    {
        var commands = new RecordingSfxAl();
        using var audio = Loaded(new OpenAlSfxBackend(commands, _logger));
        var handles = Enumerable.Range(0, 4).Select(_ => Start(audio)).ToArray();
        uint[] sources = commands.LiveSources.Order().ToArray();
        uint[] buffers = commands.LiveBuffers.Order().ToArray();
        Assert.Equal(20, sources.Length);
        var device = new FakeAlcOutputDevice();
        var follower = new AudioDeviceFollower(device, true, "Speakers", new AlErrorLog(_logger), _logger, TimeSpan.Zero);
        device.DefaultDeviceName = "Headphones";
        if (failFirstReopen)
        {
            device.IsConnected = false;
            device.ReopenFailures.Enqueue(ContextError.InvalidDevice);
        }
        commands.Calls.Clear();

        follower.Poll(TimeSpan.FromSeconds(1));

        Assert.Equal(failFirstReopen ? 0 : 1, follower.ReopenCount);
        Assert.Equal(failFirstReopen ? 1 : 0, follower.FailedReopenCount);
        Assert.All(handles, handle => Assert.True(audio.IsLoopActive(handle)));
        Assert.Equal(sources, commands.LiveSources.Order().ToArray());
        Assert.Equal(buffers, commands.LiveBuffers.Order().ToArray());
        Assert.Equal(SfxLoopStartStatus.Capacity, audio.TryPlayLoop(new("other")).Status);
        Assert.Empty(commands.Calls);

        follower.Poll(TimeSpan.FromSeconds(2));

        Assert.Equal(1, follower.ReopenCount);
        Assert.Equal(failFirstReopen ? 2 : 1, device.ReopenCalls);
        Assert.Equal("Headphones", device.CurrentDeviceName);
        Assert.All(handles, handle => Assert.True(audio.IsLoopActive(handle)));
        Assert.Equal(sources, commands.LiveSources.Order().ToArray());
        Assert.Equal(buffers, commands.LiveBuffers.Order().ToArray());
        Assert.Equal(4, commands.Plays.Count);
        Assert.All(commands.Plays, play => Assert.Equal(SourceState.Playing, commands.Sources[play.Source].State));
        Assert.Equal(SfxLoopStartStatus.Capacity, audio.TryPlayLoop(new("other")).Status);
        Assert.Empty(commands.Calls);
    }

    sealed class ThrowingUnloadBackend(RecordingLoopBackend inner) : ISfxBackend, ISfxLoopBackend
    {
        public Action? BeforeUnload { get; set; }
        public int LegacyUnloadCalls { get; private set; }
        public string Name => inner.Name;
        public int LoopCapacity => inner.LoopCapacity;
        public int Load(string path) => inner.Load(path);
        public void Unload(int handle) { LegacyUnloadCalls++; inner.Unload(handle); }
        public bool TryUnloadSfx(int handle) { BeforeUnload?.Invoke(); return inner.TryUnloadSfx(handle); }
        public void Play(int handle, float gain, float pitch, bool positional, Vector3 position) => inner.Play(handle, gain, pitch, positional, position);
        public void SetListener(Vector3 position, Vector3 forward, Vector3 up) => inner.SetListener(position, forward, up);
        public void StopAll() => inner.StopAll();
        public SfxLoopBackendStartResult TryStartLoop(in SfxLoopPlayback playback) => inner.TryStartLoop(playback);
        public bool StopLoop(SfxLoopBackendHandle handle) => inner.StopLoop(handle);
        public bool SetLoopGain(SfxLoopBackendHandle handle, float gain) => inner.SetLoopGain(handle, gain);
        public bool SetLoopPosition(SfxLoopBackendHandle handle, Vector3 position) => inner.SetLoopPosition(handle, position);
        public bool IsLoopActive(SfxLoopBackendHandle handle) => inner.IsLoopActive(handle);
        public void Dispose() => inner.Dispose();
    }
}
