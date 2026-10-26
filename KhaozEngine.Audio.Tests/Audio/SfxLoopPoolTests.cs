using System;
using System.Numerics;
using KhaozEngine.Audio;
using Xunit;

namespace KhaozEngine.Tests.Audio;

public sealed class SfxLoopPoolTests
{
    static SfxLoopPlayback Playback => new(0, 1f, 1f, false, Vector3.Zero, SfxAttenuation.Default);

    [Fact]
    public void DefaultResultsAreFailureAndEmpty()
    {
        SfxLoopBackendStartResult backend = default;
        SfxLoopStartResult mixer = default;

        Assert.Equal(SfxLoopStartStatus.BackendFailure, backend.Status);
        Assert.Equal(default, backend.Handle);
        Assert.Equal(SfxLoopStartStatus.BackendFailure, mixer.Status);
        Assert.Equal(default, mixer.Handle);
    }

    [Fact]
    public void HandleEqualityUsesOwnerReferenceAndBackendIdentity()
    {
        var owner = new EqualOwner();
        var identity = new SfxLoopBackendHandle(0, 1);
        var handle = new SfxLoopHandle(owner, identity);
        var same = new SfxLoopHandle(owner, identity);
        var foreign = new SfxLoopHandle(new EqualOwner(), identity);

        Assert.Equal(handle, same);
        Assert.True(handle == same);
        Assert.False(handle != same);
        Assert.Equal(handle.GetHashCode(), same.GetHashCode());
        Assert.True(handle.Equals((object)same));
        Assert.False(handle.Equals(null));
        Assert.NotEqual(handle, foreign);
        Assert.True(handle != foreign);
        Assert.NotEqual(handle, new SfxLoopHandle(owner, new(0, 2)));
        Assert.NotEqual(handle, new SfxLoopHandle(owner, new(1, 1)));
        Assert.NotEqual(default, handle);
        Assert.Equal(default(SfxLoopHandle), default(SfxLoopHandle));
    }

    [Fact]
    public void CapacityHasNoSideEffects()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(SfxLoopPool.StockCapacity, device);
        var admitted = new SfxLoopBackendHandle[4];
        for (int i = 0; i < admitted.Length; i++)
        {
            var started = pool.TryStartLoop(Playback);
            Assert.Equal(SfxLoopStartStatus.Started, started.Status);
            Assert.Equal(i, started.Handle.Slot);
            Assert.NotEqual(0UL, started.Handle.Generation);
            admitted[i] = started.Handle;
        }
        var calls = device.Calls.ToArray();

        var refused = pool.TryStartLoop(Playback);

        Assert.Equal(4, pool.Capacity);
        Assert.Equal(SfxLoopStartStatus.Capacity, refused.Status);
        Assert.Equal(default, refused.Handle);
        Assert.Equal(calls, device.Calls);
        Assert.All(admitted, handle => Assert.True(pool.IsLoopActive(handle)));
        Assert.All(device.Starts, start => Assert.Equal(Playback, start.Playback));
    }

    [Fact]
    public void StopReusesSlotWithNewGeneration()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device);
        var first = pool.TryStartLoop(Playback with { Positional = true }).Handle;
        Assert.True(pool.StopLoop(first));
        var second = pool.TryStartLoop(Playback with { Positional = true }).Handle;
        var calls = device.Calls.ToArray();

        Assert.Equal(first.Slot, second.Slot);
        Assert.True(second.Generation > first.Generation);
        Assert.False(pool.IsLoopActive(first));
        Assert.False(pool.StopLoop(first));
        Assert.False(pool.SetLoopGain(first, 0.5f));
        Assert.False(pool.SetLoopPosition(first, Vector3.One));
        Assert.Equal(calls, device.Calls);
        Assert.True(pool.IsLoopActive(second));
        Assert.True(pool.SetLoopGain(second, 0.5f));
        Assert.True(pool.SetLoopPosition(second, Vector3.One));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FailedRollbackConsumesCapacity(bool startThrows, bool stopThrows)
    {
        var device = new ScriptedLoopDevice();
        device.StartOutcomes.Enqueue(() => Fail(startThrows));
        device.StopOutcomes.Enqueue(() => Fail(stopThrows));
        using var pool = new SfxLoopPool(4, device);

        var failed = pool.TryStartLoop(Playback);

        Assert.Equal(SfxLoopStartStatus.BackendFailure, failed.Status);
        Assert.Equal(default, failed.Handle);
        Assert.Equal(new[] { ("start", 0), ("stop", 0) }, device.Calls);
        Assert.False(pool.IsLoopActive(new(0, 1)));
        for (int i = 1; i < 4; i++)
        {
            var started = pool.TryStartLoop(Playback);
            Assert.Equal(SfxLoopStartStatus.Started, started.Status);
            Assert.Equal(i, started.Handle.Slot);
        }
        var calls = device.Calls.ToArray();
        var refused = pool.TryStartLoop(Playback);
        Assert.Equal(SfxLoopStartStatus.Capacity, refused.Status);
        Assert.Equal(default, refused.Handle);
        Assert.Equal(calls, device.Calls);

        Assert.True(pool.StopAllLoops());
        var recovered = pool.TryStartLoop(Playback);
        Assert.Equal(SfxLoopStartStatus.Started, recovered.Status);
        Assert.Equal(0, recovered.Handle.Slot);
        Assert.Equal(2UL, recovered.Handle.Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedRollbackReleasesSlot(bool throws)
    {
        var device = new ScriptedLoopDevice();
        device.StartOutcomes.Enqueue(() => Fail(throws));
        using var pool = new SfxLoopPool(1, device);

        var failed = pool.TryStartLoop(Playback);
        var recovered = pool.TryStartLoop(Playback);

        Assert.Equal(SfxLoopStartStatus.BackendFailure, failed.Status);
        Assert.Equal(default, failed.Handle);
        Assert.Equal(SfxLoopStartStatus.Started, recovered.Status);
        Assert.Equal(new SfxLoopBackendHandle(0, 2), recovered.Handle);
        Assert.Equal(new[] { ("start", 0), ("stop", 0), ("start", 0) }, device.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopFailureRetainsOwnership(bool throws)
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device);
        var handle = pool.TryStartLoop(Playback).Handle;
        device.StopOutcomes.Enqueue(() => Fail(throws));

        Assert.False(pool.StopLoop(handle));
        Assert.True(pool.IsLoopActive(handle));
        var calls = device.Calls.ToArray();
        Assert.Equal(SfxLoopStartStatus.Capacity, pool.TryStartLoop(Playback).Status);
        Assert.Equal(calls, device.Calls);
        Assert.True(pool.SetLoopGain(handle, 0.25f));
        Assert.True(pool.StopLoop(handle));
        Assert.False(pool.IsLoopActive(handle));
    }

    [Fact]
    public void UnloadTouchesOnlyMatchingBuffer()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(3, device);
        var matching = pool.TryStartLoop(Playback with { Buffer = 17 }).Handle;
        var other = pool.TryStartLoop(Playback with { Buffer = 23 }).Handle;
        var alsoMatching = pool.TryStartLoop(Playback with { Buffer = 17 }).Handle;
        device.Calls.Clear();

        Assert.True(pool.StopBufferLoops(17));

        Assert.Equal(new[] { ("stop", 0), ("stop", 2) }, device.Calls);
        Assert.False(pool.IsLoopActive(matching));
        Assert.False(pool.IsLoopActive(alsoMatching));
        Assert.True(pool.IsLoopActive(other));
        var calls = device.Calls.ToArray();
        Assert.True(pool.StopBufferLoops(17));
        Assert.True(pool.StopBufferLoops(99));
        Assert.Equal(calls, device.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BufferCleanupKeepsFailedOwnershipAndAttemptsEveryMatch(bool throws)
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(3, device);
        var first = pool.TryStartLoop(Playback with { Buffer = 17 }).Handle;
        var other = pool.TryStartLoop(Playback with { Buffer = 23 }).Handle;
        var last = pool.TryStartLoop(Playback with { Buffer = 17 }).Handle;
        device.StopOutcomes.Enqueue(() => Fail(throws));
        device.Calls.Clear();

        Assert.False(pool.StopBufferLoops(17));

        Assert.Equal(new[] { ("stop", 0), ("stop", 2) }, device.Calls);
        Assert.True(pool.IsLoopActive(first));
        Assert.True(pool.IsLoopActive(other));
        Assert.False(pool.IsLoopActive(last));
        Assert.True(pool.StopBufferLoops(17));
        Assert.Equal(("stop", 0), device.Calls[^1]);
        Assert.False(pool.IsLoopActive(first));
    }

    [Fact]
    public void BufferCleanupFindsQuarantineWithoutPublishedIdentity()
    {
        var device = new ScriptedLoopDevice();
        device.StartOutcomes.Enqueue(() => false);
        device.StopOutcomes.Enqueue(() => false);
        using var pool = new SfxLoopPool(1, device);
        Assert.Equal(SfxLoopStartStatus.BackendFailure,
            pool.TryStartLoop(Playback with { Buffer = 17 }).Status);
        var calls = device.Calls.ToArray();

        Assert.True(pool.StopBufferLoops(23));
        Assert.Equal(calls, device.Calls);
        Assert.Equal(SfxLoopStartStatus.Capacity, pool.TryStartLoop(Playback).Status);
        Assert.True(pool.StopBufferLoops(17));
        Assert.Equal(SfxLoopStartStatus.Started, pool.TryStartLoop(Playback).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GainAndPositionFailurePreserveOwnership(bool throws)
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device);
        var handle = pool.TryStartLoop(Playback with { Positional = true }).Handle;
        device.GainOutcomes.Enqueue(() => Fail(throws));
        device.PositionOutcomes.Enqueue(() => Fail(throws));

        Assert.False(pool.SetLoopGain(handle, 0.25f));
        Assert.True(pool.IsLoopActive(handle));
        Assert.False(pool.SetLoopPosition(handle, Vector3.One));
        Assert.True(pool.IsLoopActive(handle));
        Assert.True(pool.SetLoopGain(handle, 0.5f));
        Assert.True(pool.SetLoopPosition(handle, new(2, 3, 4)));
        Assert.Equal(new[] { (0, 0.25f), (0, 0.5f) }, device.GainWrites);
        Assert.Equal(new[] { (0, Vector3.One), (0, new Vector3(2, 3, 4)) }, device.PositionWrites);
        Assert.Single(device.Starts);
    }

    [Fact]
    public void FlatLoopPositionRefusalHasNoSideEffects()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device);
        var handle = pool.TryStartLoop(Playback).Handle;
        var calls = device.Calls.ToArray();

        Assert.False(pool.SetLoopPosition(handle, Vector3.One));

        Assert.True(pool.IsLoopActive(handle));
        Assert.Equal(calls, device.Calls);
    }

    [Fact]
    public void InvalidHandlesHaveNoSideEffects()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device);
        var active = pool.TryStartLoop(Playback with { Positional = true }).Handle;
        var calls = device.Calls.ToArray();
        SfxLoopBackendHandle[] invalid = { default, new(-1, 1), new(1, 1), new(0, 2) };

        foreach (var handle in invalid)
        {
            Assert.False(pool.IsLoopActive(handle));
            Assert.False(pool.StopLoop(handle));
            Assert.False(pool.SetLoopGain(handle, 0.5f));
            Assert.False(pool.SetLoopPosition(handle, Vector3.One));
        }

        Assert.Equal(calls, device.Calls);
        Assert.True(pool.IsLoopActive(active));
    }

    [Fact]
    public void GenerationNeverWraps()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(1, device, initialGeneration: ulong.MaxValue - 1);
        var final = pool.TryStartLoop(Playback);
        Assert.Equal(SfxLoopStartStatus.Started, final.Status);
        Assert.Equal(ulong.MaxValue, final.Handle.Generation);
        Assert.True(pool.StopLoop(final.Handle));
        var calls = device.Calls.ToArray();

        var refused = pool.TryStartLoop(Playback);

        Assert.Equal(SfxLoopStartStatus.Capacity, refused.Status);
        Assert.Equal(default, refused.Handle);
        Assert.False(pool.IsLoopActive(final.Handle));
        Assert.Equal(calls, device.Calls);
    }

    [Fact]
    public void RetiringOneSlotLeavesOtherFreeSlotsUsable()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(2, device, initialGeneration: ulong.MaxValue - 1);
        var first = pool.TryStartLoop(Playback).Handle;
        Assert.True(pool.StopLoop(first));

        var surviving = pool.TryStartLoop(Playback);

        Assert.Equal(SfxLoopStartStatus.Started, surviving.Status);
        Assert.Equal(new SfxLoopBackendHandle(1, ulong.MaxValue), surviving.Handle);
        Assert.False(pool.IsLoopActive(first));
        Assert.True(pool.IsLoopActive(surviving.Handle));
        Assert.Equal(new[] { ("start", 0), ("stop", 0), ("start", 1) }, device.Calls);
    }

    [Fact]
    public void DisposeInvalidatesIdentitiesAndAttemptsEveryCleanupAfterFailures()
    {
        var device = new ScriptedLoopDevice();
        using var pool = new SfxLoopPool(4, device);
        var first = pool.TryStartLoop(Playback).Handle;
        var second = pool.TryStartLoop(Playback).Handle;
        device.StartOutcomes.Enqueue(() => false);
        device.StopOutcomes.Enqueue(() => false);
        Assert.Equal(SfxLoopStartStatus.BackendFailure, pool.TryStartLoop(Playback).Status);
        var last = pool.TryStartLoop(Playback).Handle;
        device.StopOutcomes.Enqueue(() => false);
        device.StopOutcomes.Enqueue(() => throw new InvalidOperationException("scripted stop"));
        device.Calls.Clear();

        pool.Dispose();

        Assert.True(device.Disposed);
        Assert.Equal(new[] { ("stop", 0), ("stop", 1), ("stop", 2), ("stop", 3), ("dispose", -1) }, device.Calls);
        var calls = device.Calls.ToArray();
        Assert.All(new[] { first, second, last }, handle =>
        {
            Assert.False(pool.IsLoopActive(handle));
            Assert.False(pool.StopLoop(handle));
            Assert.False(pool.SetLoopGain(handle, 0.5f));
            Assert.False(pool.SetLoopPosition(handle, Vector3.One));
        });
        var refused = pool.TryStartLoop(Playback);
        Assert.Equal(SfxLoopStartStatus.Disposed, refused.Status);
        Assert.Equal(default, refused.Handle);
        Assert.False(pool.StopBufferLoops(0));
        Assert.False(pool.StopAllLoops());
        pool.Dispose();
        Assert.Equal(calls, device.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacityIsRejected(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SfxLoopPool(capacity, new ScriptedLoopDevice()));
    }

    [Fact]
    public void NullDeviceIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SfxLoopPool(1, null!));
    }

    static bool Fail(bool throws) => throws ? throw new InvalidOperationException("scripted failure") : false;

    sealed class EqualOwner
    {
        public override bool Equals(object? obj) => obj is EqualOwner;
        public override int GetHashCode() => throw new InvalidOperationException("Owner value hashing is forbidden.");
    }
}
