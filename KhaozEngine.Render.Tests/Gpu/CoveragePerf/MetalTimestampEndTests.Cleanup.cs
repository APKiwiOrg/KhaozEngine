using System;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

public sealed partial class MetalTimestampEndTests
{
    [Theory]
    [InlineData(true, "copy", true)]
    [InlineData(false, "copy", true)]
    [InlineData(false, "sample", true)]
    [InlineData(true, "resolve", true)]
    [InlineData(false, "resolve", true)]
    [InlineData(true, "copy", false)]
    [InlineData(false, "copy", false)]
    [InlineData(false, "sample", false)]
    [InlineData(true, "resolve", false)]
    [InlineData(false, "resolve", false)]
    public void CleanupBlitFailurePreservesPrimaryException(bool stage, string failure, bool tail)
    {
        using var rig = new MetalTimestampEndFixture(stage);
        PrepareCleanupFailure(rig, tail, failure == "resolve");
        var primary = new InvalidOperationException("primary timestamp failure");
        var cleanup = new InvalidOperationException("secondary blit close failure");
        bool failed = false;
        int closes = 0;
        rig.Native.OnCall = call =>
        {
            if (call == failure)
            {
                failed = true;
                ThrowPrimary(primary);
            }
            if (call == "end blit" && failed)
            {
                closes++;
                throw cleanup;
            }
        };

        Exception actual = Assert.Throws<InvalidOperationException>(() => InvokeCleanupFailure(rig, tail, failure == "resolve"));
        Assert.Same(primary, actual);
        Assert.Contains(nameof(ThrowPrimary), actual.StackTrace);
        Assert.Equal(1, closes);
        AssertCleanupFailureState(rig, tail);
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, true)]
    public void CleanupLoneBlitFailureStillPropagates(bool stage, bool tail, bool resolve)
    {
        using var rig = new MetalTimestampEndFixture(stage);
        PrepareCleanupFailure(rig, tail, resolve);
        var cleanup = new InvalidOperationException("lone blit close failure");
        bool reachedTarget = false;
        int closes = 0;
        rig.Native.OnCall = call =>
        {
            if (call == (resolve ? "resolve" : "copy")) reachedTarget = true;
            if (call == "end blit" && reachedTarget)
            {
                closes++;
                throw cleanup;
            }
        };

        Assert.Same(cleanup, Assert.Throws<InvalidOperationException>(() => InvokeCleanupFailure(rig, tail, resolve)));
        Assert.Equal(1, closes);
        AssertCleanupFailureState(rig, tail);
    }

    [Theory]
    [InlineData("copy", true)]
    [InlineData("resolve", true)]
    [InlineData("copy", false)]
    [InlineData("resolve", false)]
    public void CleanupBoundaryFailurePreservesPrimaryException(string failure, bool tail)
    {
        using var rig = new MetalTimestampEndFixture();
        PrepareCleanupFailure(rig, tail, failure == "resolve");
        var primary = new InvalidOperationException("primary timestamp failure");
        rig.Native.OnCall = call =>
        {
            if (call != failure) return;
            // Reentrant native work violates the boundary invariant while the primary failure unwinds.
            rig.List.Encoders.EnsureComputeEncoder();
            ThrowPrimary(primary);
        };

        Exception actual = Assert.Throws<InvalidOperationException>(() => InvokeCleanupFailure(rig, tail, failure == "resolve"));
        Assert.Same(primary, actual);
        Assert.Contains(nameof(ThrowPrimary), actual.StackTrace);
        AssertCleanupFailureState(rig, tail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CleanupDescriptorFailurePreservesPrimaryOrPropagatesAlone(bool combined)
    {
        using var rig = new MetalTimestampEndFixture();
        rig.Start();
        rig.Arm();
        rig.PendingClear();
        var primary = new InvalidOperationException("primary pass begin failure");
        var cleanup = new InvalidOperationException("descriptor release failure");
        int releases = 0;
        if (combined) rig.Calls.BeforeBegin = kind => ThrowPrimary(primary);
        rig.Render.AfterRelease = () => { releases++; throw cleanup; };

        Exception actual = Assert.Throws<InvalidOperationException>(rig.List.End);
        Assert.Same(combined ? primary : cleanup, actual);
        if (combined) Assert.Contains(nameof(ThrowPrimary), actual.StackTrace);
        Assert.Equal(1, releases);
        AssertCleanupFailureState(rig, tail: true);
    }

    static void PrepareCleanupFailure(MetalTimestampEndFixture rig, bool tail, bool resolve)
    {
        rig.Start();
        if (tail) rig.Arm();
        else if (resolve) rig.List.WriteTimestamp(rig.Pool, 1);
    }

    static void InvokeCleanupFailure(MetalTimestampEndFixture rig, bool tail, bool resolve)
    {
        if (tail) rig.List.End();
        else if (resolve) rig.List.EndTimestamps(rig.Pool);
        else rig.List.WriteTimestamp(rig.Pool, 1);
    }

    static void AssertCleanupFailureState(MetalTimestampEndFixture rig, bool tail)
    {
        IntPtr abandoned = rig.Buffers.Acquired[0];
        Assert.Equal(0, rig.Native.OpenBlits);
        Assert.False(rig.List.IsSealed);
        if (tail)
        {
            Assert.False(rig.List.IsRecording);
            Assert.Contains("without a sealed recording", Assert.Throws<InvalidOperationException>(rig.Submit).Message);
            Assert.Contains("not recording", Assert.Throws<InvalidOperationException>(rig.List.End).Message);
            Assert.Equal(new[] { abandoned }, rig.Buffers.Released);
            Assert.Equal(0, rig.Buffers.Outstanding);
            Assert.Equal(0, rig.Uncommitted.Outstanding);
            Assert.Equal(0, rig.Calls.OutstandingEncoders);
            Assert.Equal(0, rig.Render.OutstandingDescriptors);
        }
        else
        {
            // Manual timestamp failures keep the existing recording lifecycle. Only End abandons a buffer.
            Assert.True(rig.List.IsRecording);
            Assert.Empty(rig.Buffers.Released);
        }

        rig.Native.OnCall = null;
        rig.Calls.BeforeBegin = null;
        rig.Render.AfterRelease = null;
        int signals = rig.Native.Signals.Count;
        if (tail) rig.List.Begin();
        Assert.Contains("unresolved", Assert.Throws<InvalidOperationException>(() => rig.List.BeginTimestamps(rig.Pool, 2)).Message);
        rig.List.End();
        Assert.Equal(signals, rig.Native.Signals.Count);
        rig.List.Dispose();
        Assert.Equal(1, rig.Buffers.Released.Count(buffer => buffer == abandoned));
        Assert.Equal(0, rig.Buffers.Outstanding);
        Assert.Equal(0, rig.Uncommitted.Outstanding);
        Assert.Equal(0, rig.Calls.OutstandingEncoders);
        Assert.Equal(0, rig.Render.OutstandingDescriptors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ThrowPrimary(InvalidOperationException primary) => throw primary;
}
