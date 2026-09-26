using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary><see cref="TemporalHistory"/> records whether the previous frame can be trusted and why it last could not.
/// The scene decides when to reset and hands over one reason, and this type only keeps it.</summary>
public sealed class TemporalHistoryTests
{
    [Fact]
    public void ANewHistoryIsInvalidAndHasNeverBeenReset()
    {
        var history = new TemporalHistory();
        Assert.False(history.IsValid);
        Assert.Equal(TemporalResetReason.None, history.LastReset);
    }

    [Fact]
    public void AFrameThatCompletesMakesItValid()
    {
        var history = new TemporalHistory();
        history.MarkValidAfterFrame();
        Assert.True(history.IsValid);
    }

    [Theory]
    [InlineData(TemporalResetReason.FirstFrame)]
    [InlineData(TemporalResetReason.Resize)]
    [InlineData(TemporalResetReason.RenderScale)]
    [InlineData(TemporalResetReason.AntiAliasing)]
    [InlineData(TemporalResetReason.CameraCutRequested)]
    [InlineData(TemporalResetReason.CameraCutDetected)]
    [InlineData(TemporalResetReason.DeviceReset)]
    public void AResetRecordsItsReasonAndTheReasonOutlivesTheRecovery(TemporalResetReason reason)
    {
        var history = new TemporalHistory();
        history.MarkValidAfterFrame();
        history.Invalidate(reason);
        Assert.False(history.IsValid);
        Assert.Equal(reason, history.LastReset);
        history.MarkValidAfterFrame();
        Assert.True(history.IsValid);
        Assert.Equal(reason, history.LastReset);
    }

    /// <summary>A reason appended to the public enum lands after the guard's upper bound. Reading the members at run
    /// time makes that a failing test rather than a throw on the first frame that hits the new trigger.</summary>
    [Fact]
    public void EveryDefinedReasonIsAcceptedAndTheValueAfterTheLastIsRefused()
    {
        var max = TemporalResetReason.None;
        foreach (var reason in Enum.GetValues<TemporalResetReason>())
        {
            if (reason > max)
                max = reason;
            if (reason == TemporalResetReason.None)
                continue;
            var accepting = new TemporalHistory();
            accepting.Invalidate(reason);
            Assert.Equal(reason, accepting.LastReset);
        }

        var refusing = new TemporalHistory();
        Assert.Throws<ArgumentOutOfRangeException>(() => refusing.Invalidate((TemporalResetReason)((int)max + 1)));
    }

    [Fact]
    public void TheLatestResetWins()
    {
        var history = new TemporalHistory();
        history.Invalidate(TemporalResetReason.Resize);
        history.Invalidate(TemporalResetReason.CameraCutRequested);
        Assert.Equal(TemporalResetReason.CameraCutRequested, history.LastReset);
    }

    [Theory]
    [InlineData(TemporalResetReason.None)]
    [InlineData((TemporalResetReason)99)]
    [InlineData((TemporalResetReason)(-1))]
    public void ANonReasonIsRefusedAndChangesNothing(TemporalResetReason reason)
    {
        var history = new TemporalHistory();
        history.MarkValidAfterFrame();
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Invalidate(reason));
        Assert.True(history.IsValid);
        Assert.Equal(TemporalResetReason.None, history.LastReset);
    }
}
