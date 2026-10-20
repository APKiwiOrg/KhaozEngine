using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

public sealed partial class D3D11TimestampEndTests
{
    [Fact]
    public void FailedEmitterEndAbandonsImmediateRecording()
    {
        using var rig = new D3D11TimestampEndFixture(false);
        rig.Start();
        var expected = new InvalidOperationException("emitter-end");
        rig.Api.OnEmitterEnd = () => throw expected;
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(rig.List.End));
        Assert.False(rig.IsRecording);
        Assert.False(rig.IsSealed);
        int calls = rig.Api.Calls.Count;
        Assert.Contains("without", Assert.Throws<InvalidOperationException>(rig.Submit).Message);
        Assert.Contains("not recording", Assert.Throws<InvalidOperationException>(rig.List.End).Message);
        Assert.Equal(calls, rig.Api.Calls.Count);
        rig.Api.OnEnd = null;
        rig.Api.OnEmitterEnd = null;
        rig.List.Begin();
        Assert.Contains("unresolved", Assert.Throws<InvalidOperationException>(() => rig.List.BeginTimestamps(rig.Pool, 2)).Message);
        rig.List.End();
        Assert.Equal(calls, rig.Api.Calls.Count);
    }
}
