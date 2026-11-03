using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class MetalBindingRefusalTests
{
    [Fact]
    public void FlushRefusesAResourceInTheWrongArgumentSpace()
    {
        var records = MetalBindRecords.ForGraphics(MetalBindProgram.DeviceOffsetAlignment);
        records.SetIndexTable(MetalBindProgram.Table());
        var set = MetalBindProgram.Set(new MetalBoundResource(MetalIndexSpace.Texture, new FakeMetalBindable(1), 0, 0, false));
        records.Record(0, set, 0);
        var sink = new FakeMetalEncoderSink(new FakeMetalEncoderCalls());
        var refusal = Assert.Throws<MetalBindRecordsStateException>(() => records.Flush(ref sink, new IntPtr(1), 1, 0));
        Assert.Equal(MetalBindRecordsRefusal.IndexSpaceMismatch, refusal.Reason);
        Assert.Equal(0u, refusal.Set);
        Assert.Equal(0, refusal.Binding);
        Assert.Equal(GpuShaderStages.Vertex, refusal.Stage);
        Assert.Equal("buffer", refusal.ExpectedSpace);
        Assert.Equal("texture", refusal.ActualSpace);
        Assert.Equal(0, refusal.ArgumentIndex);
        Assert.True(records.IsDirty(0));
        records.Record(0, MetalBindProgram.Set(set.Bindings![0] with { Space = MetalIndexSpace.Buffer }), 0);
        records.Flush(ref sink, new IntPtr(1), 1, 0);
    }
}
