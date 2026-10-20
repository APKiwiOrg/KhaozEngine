using System;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

public sealed partial class MetalTimestampEndTests
{
    [Theory]
    [InlineData("clear-flush")]
    [InlineData("render-end")]
    [InlineData("encoder-close")]
    public void FailedEndWithoutTailAlsoAbandonsTheRecording(string failure)
    {
        using var rig = new MetalTimestampEndFixture();
        rig.List.Begin();
        rig.PendingClear();
        if (failure == "render-end") rig.List.Passes.PrepareDraw();
        if (failure == "encoder-close")
        {
            rig.List.Passes.PrepareDraw();
            rig.List.Encoders.EnsureBlitEncoder();
        }
        var expected = new InvalidOperationException(failure);
        rig.Render.BeforeCreate = () => { if (failure == "clear-flush") throw expected; };
        rig.Calls.AfterEnd = kind => { throw expected; };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(rig.List.End));
        Assert.False(rig.List.IsRecording);
        Assert.False(rig.List.IsSealed);
        Assert.Equal(0, rig.Uncommitted.Outstanding);
        Assert.Equal(0, rig.Buffers.Outstanding);
        rig.Calls.AfterEnd = null;
        rig.Render.BeforeCreate = null;
        rig.List.Begin();
        rig.List.End();
    }

}
