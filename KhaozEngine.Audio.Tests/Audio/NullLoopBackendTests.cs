using System;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class NullLoopBackendTests
{
    static SfxLoopPlayback Playback(int buffer, bool positional = false) =>
        new(buffer, 1, 1, positional, Vector3.Zero, SfxAttenuation.Default);

    [Fact]
    public void NonexistentFileLoadsSilentlyAndFourSlotsAreProtected()
    {
        using var backend = new NullSfxBackend();
        int buffer = backend.Load("nonexistent-" + Guid.NewGuid() + ".wav");
        Assert.True(buffer >= 0);
        ISfxLoopBackend loops = backend;
        Assert.Equal(4, loops.LoopCapacity);
        var handles = new SfxLoopBackendHandle[4];
        for (int i = 0; i < 4; i++)
        {
            var started = loops.TryStartLoop(Playback(buffer));
            Assert.Equal(SfxLoopStartStatus.Started, started.Status);
            handles[i] = started.Handle;
        }

        Assert.Equal(SfxLoopStartStatus.Capacity, loops.TryStartLoop(Playback(buffer)).Status);
        backend.Play(buffer, 1, 1, false, Vector3.Zero);
        Assert.All(handles, handle => Assert.True(loops.IsLoopActive(handle)));
        Assert.True(loops.StopLoop(handles[1]));
        var replacement = loops.TryStartLoop(Playback(buffer));
        Assert.Equal(SfxLoopStartStatus.Started, replacement.Status);
        Assert.NotEqual(handles[1], replacement.Handle);
        Assert.False(loops.StopLoop(handles[1]));
        Assert.True(loops.IsLoopActive(replacement.Handle));
    }

    [Fact]
    public void UnknownAndUnloadedBuffersCannotStartAndUnloadExpiresHandles()
    {
        using var backend = new NullSfxBackend();
        ISfxLoopBackend loops = backend;
        Assert.Equal(SfxLoopStartStatus.NotLoaded, loops.TryStartLoop(Playback(0)).Status);
        Assert.Equal(SfxLoopStartStatus.NotLoaded, loops.TryStartLoop(Playback(-1)).Status);
        int first = backend.Load("absent.wav");
        int second = backend.Load("also-absent.wav");
        var a = loops.TryStartLoop(Playback(first)).Handle;
        var b = loops.TryStartLoop(Playback(second)).Handle;

        backend.Unload(first);

        Assert.False(loops.IsLoopActive(a));
        Assert.False(loops.SetLoopGain(a, 0.2f));
        Assert.True(loops.IsLoopActive(b));
        Assert.Equal(SfxLoopStartStatus.NotLoaded, loops.TryStartLoop(Playback(first)).Status);
        Assert.True(loops.TryUnloadSfx(first));
        Assert.True(loops.TryUnloadSfx(second));
        Assert.False(loops.IsLoopActive(b));
    }

    [Fact]
    public void StopAllAndDisposeExpireIdentitiesAndRejectFurtherWork()
    {
        var backend = new NullSfxBackend();
        ISfxLoopBackend loops = backend;
        int buffer = backend.Load("absent.wav");
        var flat = loops.TryStartLoop(Playback(buffer)).Handle;
        var positioned = loops.TryStartLoop(Playback(buffer, true)).Handle;
        Assert.True(loops.SetLoopGain(flat, 0.3f));
        Assert.False(loops.SetLoopPosition(flat, Vector3.One));
        Assert.True(loops.SetLoopPosition(positioned, Vector3.One));
        backend.StopAll();
        Assert.False(loops.IsLoopActive(flat));
        Assert.False(loops.IsLoopActive(positioned));
        var last = loops.TryStartLoop(Playback(buffer)).Handle;

        backend.Dispose();
        backend.Dispose();

        Assert.False(loops.IsLoopActive(last));
        Assert.False(loops.StopLoop(last));
        Assert.False(loops.SetLoopGain(last, 1));
        Assert.False(loops.SetLoopPosition(last, Vector3.Zero));
        Assert.Equal(SfxLoopStartStatus.Disposed, loops.TryStartLoop(Playback(buffer)).Status);
        Assert.Equal(-1, backend.Load("absent.wav"));
    }
}
