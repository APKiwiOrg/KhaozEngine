using System;
using System.Collections.Generic;
using System.Threading;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class AudioDisposalTests
{
    [Fact]
    public void CleanupRunsInBorrowerOrder()
    {
        var order = new List<string>();
        AudioDisposal.Run(() => order.Add("sfx"), () => order.Add("music"), () => order.Add("context"), new AudioLoopLogger());
        Assert.Equal(new[] { "sfx", "music", "context" }, order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllResourcesAreAttemptedAndTheFirstErrorSurvivesDiagnostics(bool brokenDiagnostics)
    {
        var order = new List<string>();
        var logger = new AudioLoopLogger { ThrowOnWarning = brokenDiagnostics };
        var first = new InvalidOperationException("first cleanup");
        var second = new InvalidOperationException("second cleanup");
        var third = new InvalidOperationException("third cleanup");
        var caught = Assert.Throws<InvalidOperationException>(() => AudioDisposal.Run(
            () => { order.Add("sfx"); throw first; },
            () => { order.Add("music"); throw second; },
            () => { order.Add("context"); throw third; }, logger));
        Assert.Same(first, caught);
        Assert.Equal(new[] { "sfx", "music", "context" }, order);
        Assert.Equal(new Exception[] { second, third }, logger.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaterFailureIsRethrownAfterRemainingCleanup(bool failMusic)
    {
        var order = new List<string>();
        var failure = new InvalidOperationException("later cleanup");
        var caught = Assert.Throws<InvalidOperationException>(() => AudioDisposal.Run(
            () => order.Add("sfx"),
            () => { order.Add("music"); if (failMusic) throw failure; },
            () => { order.Add("context"); if (!failMusic) throw failure; }, new AudioLoopLogger()));
        Assert.Same(failure, caught);
        Assert.Equal(new[] { "sfx", "music", "context" }, order);
    }

    [Fact]
    public void FacadeIsDisposedBeforeFailingBackendCleanupAndDoesNotRepeatTeardown()
    {
        var order = new List<string>();
        var first = new InvalidOperationException("sfx cleanup");
        var sfx = new RecordingLoopBackend { DisposalOrder = order, DisposeFailure = first };
        var music = new DisposalMusicBackend { DisposalOrder = order, DisposeFailure = new InvalidOperationException("music cleanup") };
        var logger = new AudioLoopLogger { ThrowOnWarning = true };
        var audio = new AudioSystem(music, sfx, logger: logger);
        sfx.OnDispose = () => Assert.Equal(SfxLoopStartStatus.Disposed, audio.TryPlayLoop(default).Status);
        Assert.Same(first, Assert.Throws<InvalidOperationException>(audio.Dispose));
        Assert.Equal(new[] { "sfx", "music" }, order);
        audio.Dispose();
        Assert.Equal(1, sfx.DisposeCount);
        Assert.Equal(1, music.DisposeCount);
        Assert.Equal(SfxLoopStartStatus.Disposed, audio.TryPlayLoop(default).Status);
    }

    [Fact]
    public void OffThreadDisposalWithoutALiveTickStillTearsDownBothBackends()
    {
        var order = new List<string>();
        var sfx = new RecordingLoopBackend { DisposalOrder = order };
        var music = new DisposalMusicBackend { DisposalOrder = order };
        var audio = new AudioSystem(music, sfx, logger: new AudioLoopLogger());
        Exception? caught = null;
        var thread = new Thread(() => caught = Record.Exception(audio.Dispose));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.Null(caught);
        Assert.Equal(new[] { "sfx", "music" }, order);
        Assert.True(sfx.Device.Disposed);
        Assert.Equal(SfxLoopStartStatus.Disposed, audio.TryPlayLoop(default).Status);
    }
}
