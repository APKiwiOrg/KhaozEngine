using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Audio;
using KhaozEngine.Diagnostics;
using Silk.NET.OpenAL;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class OpenAlLoopBackendTests
{
    [Fact]
    public void OneShotPressureCannotReachLoopSources()
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var al = fixture.Commands;
        uint[] effects = al.LiveSources.Order().ToArray();
        Assert.Equal(16, effects.Length);
        Assert.Equal(4, backend.LoopCapacity);
        var handles = new SfxLoopBackendHandle[4];
        for (int i = 0; i < 4; i++)
        {
            var result = backend.TryStartLoop(fixture.Playback());
            Assert.Equal(SfxLoopStartStatus.Started, result.Status);
            handles[i] = result.Handle;
        }
        uint[] protectedSources = al.Plays.Select(p => p.Source).ToArray();
        var beforeRefusal = al.Calls.ToArray();
        var refused = backend.TryStartLoop(fixture.Playback());
        Assert.Equal(SfxLoopStartStatus.Capacity, refused.Status);
        Assert.Equal(default, refused.Handle);
        Assert.Equal(beforeRefusal, al.Calls);
        al.Calls.Clear();
        al.Plays.Clear();

        for (int i = 0; i < 17; i++)
            backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero);

        Assert.Equal(17, al.Plays.Count);
        Assert.All(al.Plays, play => Assert.Contains(play.Source, effects));
        Assert.Equal(effects, al.Plays.Take(16).Select(p => p.Source));
        Assert.Equal(effects[0], al.Plays[16].Source);
        Assert.All(handles, handle => Assert.True(backend.IsLoopActive(handle)));
        Assert.All(protectedSources, source => Assert.DoesNotContain(al.Calls, call => call.Object == source));
        Assert.Equal(20, al.LiveSources.Count);
    }

    [Fact]
    public void NativeStartRollbackIsBounded()
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        var backend = fixture.Backend;
        al.Calls.Clear();
        al.FailNext("SourcePlay", AudioError.InvalidOperation);
        al.FailNext("SourceStop", AudioError.InvalidOperation);

        var failed = backend.TryStartLoop(fixture.Playback());

        Assert.Equal(SfxLoopStartStatus.BackendFailure, failed.Status);
        Assert.Equal(default, failed.Handle);
        uint quarantined = Assert.Single(al.Calls, c => c.Operation == "GenSource").Object;
        Assert.Equal(1, al.Calls.Count(c => c.Operation == "SourcePlay"));
        Assert.Equal(1, al.Calls.Count(c => c.Operation == "SourceStop"));
        Assert.NotEqual(0U, al.Sources[quarantined].Buffer);
        Assert.DoesNotContain(al.Calls, c => c.Operation.StartsWith("Delete", StringComparison.Ordinal));
        for (int i = 0; i < 3; i++)
            Assert.Equal(SfxLoopStartStatus.Started, backend.TryStartLoop(fixture.Playback()).Status);
        Assert.Equal(SfxLoopStartStatus.Capacity, backend.TryStartLoop(fixture.Playback()).Status);
        Assert.Equal(20, al.LiveSources.Count);
        Assert.Equal(4, al.Calls.Count(c => c.Operation == "GenSource"));
        al.Calls.Clear();

        backend.StopAll();

        Assert.Equal(1, al.Calls.Count(c => c == ("SourceStop", quarantined)));
        Assert.Equal(0U, al.Sources[quarantined].Buffer);
        Assert.Equal(SfxLoopStartStatus.Started, backend.TryStartLoop(fixture.Playback()).Status);
        Assert.Equal(quarantined, al.Plays.Last().Source);
        Assert.DoesNotContain(al.Calls, c => c.Operation == "GenSource");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCommandsSetCompleteLoopState(bool positional)
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        al.Calls.Clear();

        Assert.Equal(SfxLoopStartStatus.Started, fixture.Backend.TryStartLoop(fixture.Playback(positional)).Status);

        var play = Assert.Single(al.Plays);
        Assert.Equal(Assert.Single(al.LiveBuffers), play.State.Buffer);
        Assert.True(play.State.Booleans[SourceBoolean.Looping]);
        Assert.Equal(!positional, play.State.Booleans[SourceBoolean.SourceRelative]);
        Assert.Equal(0.7f, play.State.Floats[SourceFloat.Gain]);
        Assert.Equal(1.2f, play.State.Floats[SourceFloat.Pitch]);
        Assert.Equal(positional ? new Vector3(3, 4, 5) : Vector3.Zero, play.State.Vectors[SourceVector3.Position]);
        Assert.Equal(2, play.State.Floats[SourceFloat.ReferenceDistance]);
        Assert.Equal(0.5f, play.State.Floats[SourceFloat.RolloffFactor]);
        Assert.Equal(90, play.State.Floats[SourceFloat.MaxDistance]);
        Assert.DoesNotContain(al.Calls, c => c.Operation == "SetListener");
        Assert.Equal("SourcePlay", al.Calls.Last(c => c.Object == play.Source).Operation);
        var upload = Assert.Single(al.Uploads);
        Assert.Equal(BufferFormat.Mono16, upload.Format);
        Assert.Equal(8000, upload.Rate);
        Assert.Equal(80, upload.Samples.Length);
    }

    [Fact]
    public void StaleErrorsAreClearedAndControlsNeverReplay()
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        var backend = fixture.Backend;
        al.Calls.Clear();
        al.PendingError = AudioError.OutOfMemory;
        var started = backend.TryStartLoop(fixture.Playback(true));
        Assert.Equal(SfxLoopStartStatus.Started, started.Status);
        Assert.Equal(("GetError", 0U), al.Calls[0]);
        Assert.Contains(fixture.Sink.Entries, e => e.Message.Contains("OutOfMemory", StringComparison.Ordinal));
        uint source = Assert.Single(al.Plays).Source;
        al.FailNext("Gain", AudioError.InvalidValue);
        Assert.False(backend.SetLoopGain(started.Handle, 0.2f));
        Assert.Equal(0.7f, al.Sources[source].Floats[SourceFloat.Gain]);
        Assert.True(backend.SetLoopGain(started.Handle, 0.2f));
        al.PendingError = AudioError.InvalidEnum;
        Assert.True(backend.SetLoopPosition(started.Handle, new(9, 8, 7)));
        Assert.Equal(0.2f, al.Sources[source].Floats[SourceFloat.Gain]);
        Assert.Equal(new Vector3(9, 8, 7), al.Sources[source].Vectors[SourceVector3.Position]);
        Assert.Single(al.Plays);
        Assert.Equal(3, al.Calls.Count(c => c.Operation == "Gain"));
    }

    [Theory]
    [InlineData("GenSource")]
    [InlineData("BindBuffer")]
    [InlineData("Looping")]
    [InlineData("Gain")]
    [InlineData("Pitch")]
    [InlineData("SourceRelative")]
    [InlineData("Position")]
    [InlineData("ReferenceDistance")]
    [InlineData("RolloffFactor")]
    [InlineData("MaxDistance")]
    [InlineData("SourcePlay")]
    public void NativeStartFailuresNeverPublishOrPlayIncompleteState(string operation)
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        al.Calls.Clear();
        al.FailNext(operation, AudioError.InvalidOperation);

        var failed = fixture.Backend.TryStartLoop(fixture.Playback());

        Assert.Equal(SfxLoopStartStatus.BackendFailure, failed.Status);
        Assert.Equal(default, failed.Handle);
        Assert.Single(al.Calls, c => c.Operation == "GenSource");
        Assert.Empty(al.Plays);
        Assert.Equal(operation == "SourcePlay" ? 1 : 0, al.Calls.Count(c => c.Operation == "SourcePlay"));
        Assert.Equal(operation == "GenSource" ? 0 : 1, al.Calls.Count(c => c.Operation == "SourceStop"));
        Assert.All(al.LiveSources, source => Assert.Equal(0U, al.Sources[source].Buffer));
        Assert.DoesNotContain(al.Calls, c => c.Operation == "DeleteBuffer");
        al.Calls.Clear();
        Assert.Equal(SfxLoopStartStatus.Started, fixture.Backend.TryStartLoop(fixture.Playback()).Status);
        Assert.Equal(operation == "GenSource" ? 1 : 0, al.Calls.Count(c => c.Operation == "GenSource"));
    }

    [Fact]
    public void NativeCommandExceptionsAreBoundedAndExplicitSuccessStartsNewEpisode()
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        var backend = fixture.Backend;
        var loop = backend.TryStartLoop(fixture.Playback(true));
        al.ThrowNext("Gain");
        Assert.False(backend.SetLoopGain(loop.Handle, 0.4f));
        al.ThrowNext("Gain");
        Assert.False(backend.SetLoopGain(loop.Handle, 0.4f));
        var reported = Assert.Single(fixture.Sink.Entries, e => e.Exception?.Message == "Scripted Gain");
        Assert.Equal(LogLevel.Warn, reported.Level);
        Assert.True(backend.SetLoopGain(loop.Handle, 0.4f));
        al.ThrowNext("Gain");
        Assert.False(backend.SetLoopGain(loop.Handle, 0.4f));
        Assert.Equal(2, fixture.Sink.Entries.Count(e => e.Exception?.Message == "Scripted Gain"));
        al.FailNext("Position", AudioError.InvalidValue);
        Assert.False(backend.SetLoopPosition(loop.Handle, Vector3.One));
        Assert.True(backend.SetLoopPosition(loop.Handle, Vector3.One));
        Assert.Single(al.Plays);
        Assert.True(backend.IsLoopActive(loop.Handle));
    }

    [Theory]
    [InlineData("Gain", false)]
    [InlineData("Position", false)]
    [InlineData("Gain", true)]
    [InlineData("Position", true)]
    public void HealthyPeerCannotResetAnotherLoopsExceptionEpisode(string operation, bool throwingDiagnostics)
    {
        var logger = throwingDiagnostics ? new ThrowingLogger() : null;
        using var fixture = new RecordedSfxFixture(logger);
        var al = fixture.Commands;
        var backend = fixture.Backend;
        var a = backend.TryStartLoop(fixture.Playback(true));
        var b = backend.TryStartLoop(fixture.Playback(true));
        Assert.Equal(SfxLoopStartStatus.Started, a.Status);
        Assert.Equal(SfxLoopStartStatus.Started, b.Status);
        uint first = al.Plays[0].Source;
        uint second = al.Plays[1].Source;
        al.Calls.Clear();

        bool Update(SfxLoopBackendHandle handle) => operation == "Gain"
            ? backend.SetLoopGain(handle, 0.4f)
            : backend.SetLoopPosition(handle, Vector3.One);
        int Reports() => logger?.Attempts ?? fixture.Sink.Entries.Count;

        al.ThrowNext(operation);
        Assert.False(Update(a.Handle));
        int firstEpisodeReports = Reports();
        Assert.True(firstEpisodeReports > 0);
        Assert.True(Update(b.Handle));
        al.ThrowNext(operation);
        Assert.False(Update(a.Handle));

        Assert.Equal(2, al.Calls.Count(c => c == (operation, first)));
        Assert.Equal(1, al.Calls.Count(c => c == (operation, second)));
        Assert.Equal(firstEpisodeReports, Reports());
        if (operation == "Gain")
        {
            Assert.Equal(0.7f, al.Sources[first].Floats[SourceFloat.Gain]);
            Assert.Equal(0.4f, al.Sources[second].Floats[SourceFloat.Gain]);
        }
        else
        {
            Assert.Equal(new Vector3(3, 4, 5), al.Sources[first].Vectors[SourceVector3.Position]);
            Assert.Equal(Vector3.One, al.Sources[second].Vectors[SourceVector3.Position]);
        }

        Assert.True(Update(a.Handle));
        al.ThrowNext(operation);
        Assert.False(Update(a.Handle));
        Assert.Equal(firstEpisodeReports + 1, Reports());
        Assert.Equal(4, al.Calls.Count(c => c == (operation, first)));
        Assert.Equal(2, al.Plays.Count);

        Assert.True(backend.StopLoop(a.Handle));
        var replacement = backend.TryStartLoop(fixture.Playback(true));
        Assert.Equal(SfxLoopStartStatus.Started, replacement.Status);
        Assert.Equal(a.Handle.Slot, replacement.Handle.Slot);
        Assert.NotEqual(a.Handle, replacement.Handle);
        Assert.Equal(first, al.Plays[2].Source);
        al.Calls.Clear();
        Assert.False(Update(a.Handle));
        Assert.Empty(al.Calls);
        al.ThrowNext(operation);
        Assert.False(Update(replacement.Handle));
        Assert.True(Update(b.Handle));
        al.ThrowNext(operation);
        Assert.False(Update(replacement.Handle));
        Assert.Equal(firstEpisodeReports + 2, Reports());
        Assert.Equal(2, al.Calls.Count(c => c == (operation, first)));
        Assert.Equal(1, al.Calls.Count(c => c == (operation, second)));
        Assert.Equal(3, al.Plays.Count);
        Assert.True(backend.IsLoopActive(replacement.Handle));
        Assert.True(backend.IsLoopActive(b.Handle));
    }

    [Fact]
    public void IndividualStopIsIsolatedAndExpiredHandlesDoNothing()
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var a = backend.TryStartLoop(fixture.Playback());
        var b = backend.TryStartLoop(fixture.Playback(true));
        uint first = fixture.Commands.Plays[0].Source;
        uint second = fixture.Commands.Plays[1].Source;
        Assert.True(backend.StopLoop(a.Handle));
        Assert.False(backend.IsLoopActive(a.Handle));
        Assert.Equal(0U, fixture.Commands.Sources[first].Buffer);
        Assert.Equal(SourceState.Playing, fixture.Commands.Sources[second].State);
        Assert.True(backend.IsLoopActive(b.Handle));
        var replacement = backend.TryStartLoop(fixture.Playback());
        Assert.NotEqual(a.Handle, replacement.Handle);
        fixture.Commands.Calls.Clear();

        Assert.False(backend.StopLoop(a.Handle));
        Assert.False(backend.SetLoopGain(a.Handle, 0));
        Assert.False(backend.SetLoopPosition(default, Vector3.One));
        Assert.False(backend.SetLoopPosition(replacement.Handle, Vector3.One));
        Assert.Equal(SfxLoopStartStatus.NotLoaded, backend.TryStartLoop(fixture.Playback() with { Buffer = -1 }).Status);
        Assert.Empty(fixture.Commands.Calls);
    }

    [Fact]
    public void PartialUnloadRefusesDeletionAndExplicitRetryDetachesBeforeDelete()
    {
        using var fixture = new RecordedSfxFixture();
        var al = fixture.Commands;
        var backend = fixture.Backend;
        var loop = backend.TryStartLoop(fixture.Playback());
        uint source = Assert.Single(al.Plays).Source;
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero);
        al.Calls.Clear();
        al.FailNext("BindBuffer", AudioError.InvalidOperation);

        Assert.False(backend.TryUnloadSfx(fixture.Buffer));

        Assert.True(backend.IsLoopActive(loop.Handle));
        Assert.NotEqual(0U, al.Sources[source].Buffer);
        Assert.Single(al.LiveBuffers);
        Assert.DoesNotContain(al.Calls, c => c.Operation == "DeleteBuffer");
        Assert.True(backend.TryUnloadSfx(fixture.Buffer));
        Assert.False(backend.IsLoopActive(loop.Handle));
        Assert.Empty(al.LiveBuffers);
        Assert.True(al.Calls.FindLastIndex(c => c.Operation == "BindBuffer") < al.Calls.FindIndex(c => c.Operation == "DeleteBuffer"));
        Assert.Equal(SfxLoopStartStatus.NotLoaded, backend.TryStartLoop(fixture.Playback()).Status);
    }

    [Theory]
    [InlineData("GetSourceProperty")]
    [InlineData("SourceStop")]
    [InlineData("BindBuffer")]
    [InlineData("DeleteBuffer")]
    public void OneShotUnloadErrorsRetainBufferForRetry(string operation)
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var al = fixture.Commands;
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero);
        al.Calls.Clear();
        al.FailNext(operation, AudioError.InvalidOperation);

        Assert.False(backend.TryUnloadSfx(fixture.Buffer));

        Assert.Single(al.LiveBuffers);
        if (operation != "DeleteBuffer") Assert.DoesNotContain(al.Calls, c => c.Operation == "DeleteBuffer");
        Assert.True(backend.TryUnloadSfx(fixture.Buffer));
        Assert.Empty(al.LiveBuffers);
    }

    [Fact]
    public void StopAllAttemptsEachQuarantineOnceAndContinuesPastErrors()
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var al = fixture.Commands;
        var a = backend.TryStartLoop(fixture.Playback());
        var b = backend.TryStartLoop(fixture.Playback());
        uint first = al.Plays[0].Source;
        uint second = al.Plays[1].Source;
        al.Calls.Clear();
        al.FailNext("SourceStop", AudioError.InvalidOperation);

        backend.StopAll();

        Assert.True(backend.IsLoopActive(a.Handle));
        Assert.False(backend.IsLoopActive(b.Handle));
        Assert.Equal(1, al.Calls.Count(c => c == ("SourceStop", first)));
        Assert.Equal(1, al.Calls.Count(c => c == ("SourceStop", second)));
        backend.StopAll();
        Assert.False(backend.IsLoopActive(a.Handle));
    }

    [Fact]
    public void DisposalDeletesSourcesBeforeBuffersDespiteStopErrorsAndExpiresEverything()
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var al = fixture.Commands;
        var loop = backend.TryStartLoop(fixture.Playback());
        al.Calls.Clear();
        al.FailNext("SourceStop", AudioError.InvalidOperation);
        al.FailNext("SourceStop", AudioError.InvalidOperation);

        backend.Dispose();

        Assert.Empty(al.LiveSources);
        Assert.Empty(al.LiveBuffers);
        Assert.True(al.Calls.FindLastIndex(c => c.Operation == "DeleteSource") < al.Calls.FindIndex(c => c.Operation == "DeleteBuffer"));
        al.Calls.Clear();
        backend.Dispose();
        backend.StopAll();
        backend.Unload(fixture.Buffer);
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero);
        Assert.False(backend.IsLoopActive(loop.Handle));
        Assert.False(backend.StopLoop(loop.Handle));
        Assert.False(backend.SetLoopGain(loop.Handle, 1));
        Assert.False(backend.SetLoopPosition(loop.Handle, Vector3.One));
        Assert.Equal(SfxLoopStartStatus.Disposed, backend.TryStartLoop(fixture.Playback()).Status);
        Assert.Equal(-1, backend.Load("nonexistent.wav"));
        Assert.Empty(al.Calls);
    }

    [Fact]
    public void FailedSourceDeletionRetainsBuffersAndThrowingDiagnosticsCannotInterruptTeardown()
    {
        using var fixture = new RecordedSfxFixture(new ThrowingLogger());
        var backend = fixture.Backend;
        var al = fixture.Commands;
        Assert.Equal(SfxLoopStartStatus.Started, backend.TryStartLoop(fixture.Playback()).Status);
        al.FailNext("DeleteSource", AudioError.InvalidOperation);
        al.FailNext("SourceStop", AudioError.InvalidOperation);

        backend.Dispose();

        Assert.Single(al.LiveSources);
        Assert.Single(al.LiveBuffers);
        Assert.DoesNotContain(al.Calls, c => c.Operation == "DeleteBuffer");
    }

    [Fact]
    public void ExistingOneShotPriorityAndRotationRemainIntact()
    {
        using var fixture = new RecordedSfxFixture();
        var backend = fixture.Backend;
        var al = fixture.Commands;
        uint[] effects = al.LiveSources.Order().ToArray();
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero, SfxPriority.High);
        for (int i = 1; i < 16; i++) backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero, SfxPriority.Low);
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero, SfxPriority.High);
        backend.Play(fixture.Buffer, 1, 1, false, Vector3.Zero, SfxPriority.High);
        Assert.Equal(effects[1], al.Plays[16].Source);
        Assert.Equal(effects[2], al.Plays[17].Source);
        backend.SetListener(Vector3.One, -Vector3.UnitZ, Vector3.UnitY);
        Assert.Equal(("SetListener", 0U), al.Calls.Last());
    }

    sealed class ThrowingLogger : ILogger
    {
        public int Attempts { get; private set; }
        public string Category => "throwing-test";
        public bool IsEnabled(LogLevel level) => true;
        public void Log(LogLevel level, string message, Exception? exception = null)
        {
            Attempts++;
            throw new InvalidOperationException();
        }
        public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message, exception);
        public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message, exception);
        public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message, exception);
        public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message, exception);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message, exception);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message, exception);
    }
}
