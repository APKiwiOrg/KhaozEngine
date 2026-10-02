using System;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class Render3DSnapshotSequenceTests
{
    [Theory]
    [InlineData(0, 32, 1, 0, "width")]
    [InlineData(32, -1, 1, 0, "height")]
    [InlineData(32, 32, 0, 0, "frames")]
    [InlineData(32, 32, 1, -1, "warmupFrames")]
    [InlineData(32, 32, 1, 2, "warmupFrames")]
    public void Invalid_sequence_arguments_are_rejected_before_setup(
        int width, int height, int frames, int warmupFrames, string parameter)
    {
        bool setupCalled = false;
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Render3DSnapshot.CaptureSequence(width, height, _ => setupCalled = true, (_, _) => { }, frames,
                (_, _) => { }, warmupFrames));
        Assert.Equal(parameter, error.ParamName);
        Assert.False(setupCalled);
    }

    [Theory]
    [InlineData("setup")]
    [InlineData("drawFrame")]
    [InlineData("onFrame")]
    public void Missing_callbacks_are_rejected_before_setup(string parameter)
    {
        bool setupCalled = false;
        Action<Scene3D>? setup = parameter == "setup" ? null : _ => setupCalled = true;
        Action<Scene3D, int>? draw = parameter == "drawFrame" ? null : (_, _) => { };
        Action<int, Render3DCapture>? consume = parameter == "onFrame" ? null : (_, _) => { };
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(() =>
            Render3DSnapshot.CaptureSequence(32, 32, setup!, draw!, 1, consume!));
        Assert.Equal(parameter, error.ParamName);
        Assert.False(setupCalled);
    }

    [Fact]
    public void An_image_too_large_for_an_RGBA_array_is_rejected_before_setup()
    {
        bool setupCalled = false;
        Assert.Throws<ArgumentOutOfRangeException>(() => Render3DSnapshot.CaptureSequence(
            int.MaxValue, 32, _ => setupCalled = true, (_, _) => { }, 1, (_, _) => { }));
        Assert.False(setupCalled);
    }
}
