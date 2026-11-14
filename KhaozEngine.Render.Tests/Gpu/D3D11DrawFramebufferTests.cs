using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11DrawFramebufferTests
{
    [Theory]
    [InlineData((int)D3D11RecordMode.Immediate, "Draw")]
    [InlineData((int)D3D11RecordMode.Immediate, "DrawSingle")]
    [InlineData((int)D3D11RecordMode.Immediate, "DrawIndexed")]
    [InlineData((int)D3D11RecordMode.Deferred, "Draw")]
    [InlineData((int)D3D11RecordMode.Deferred, "DrawSingle")]
    [InlineData((int)D3D11RecordMode.Deferred, "DrawIndexed")]
    public void ADrawRequiresThisRecordingsFramebufferBeforeFlushingOrAppending(int mode, string draw)
    {
        // #1688: refuse at the draw call on both drivers, leaving pending binds and the stream untouched.
        var log = new D3D11NativeCallLog();
        var state = new D3D11DeviceState();
        var emitter = new D3D11NativeTraceEmitter(state, log);
        using IGpuCommandList list = D3D11CommandDrivers.Create((D3D11RecordMode)mode, emitter);
        using D3D11ResourceLayout layout = D3D11BindFixtures.ShadowLayout();
        using D3D11ResourceSet set = D3D11BindFixtures.ShadowSet(layout);
        using var target = D3D11BindFixtures.Framebuffer();

        Action issue = draw switch
        {
            "DrawIndexed" => () => list.DrawIndexed(3, 1, 0, 0, 0),
            "DrawSingle" => () => list.Draw(3),
            _ => () => list.Draw(3, 1, 0, 0),
        };

        list.Begin();
        list.SetPipeline(D3D11BindFixtures.Pipeline(layout));
        list.SetGraphicsResourceSet(0, set, 0);
        list.SetVertexBuffer(0, new TraceBuffer(256));
        log.Reset();
        var stream = (list as D3D11CommandRecorder<D3D11StreamEmitter>)?.Emitter.Stream;
        int recorded = stream?.Count ?? 0;

        var refusal = Assert.Throws<GpuBoundFramebufferException>(issue);

        Assert.Equal(GpuBoundFramebufferRefusal.FramebufferNotBound, refusal.Reason);
        Assert.Equal(draw == "DrawIndexed" ? "DrawIndexed" : "Draw", refusal.Command);
        Assert.Empty(log.Trace);
        if (stream is not null) Assert.Equal(recorded, stream.Count);
        else Assert.Equal(D3D11SlotDirty.Full, state.Binds.GraphicsDirty(0));

        list.SetFramebufferWithStoreActions(target, default);
        issue();
        list.End();
        D3D11CommandDrivers.Submit(new object(), list, ref emitter);
        Assert.Equal(1, log.Count(draw == "DrawIndexed"
            ? D3D11NativeCall.DrawIndexedInstanced : D3D11NativeCall.DrawInstanced));

        list.Begin();
        var next = Assert.Throws<GpuBoundFramebufferException>(issue);
        Assert.Equal(GpuBoundFramebufferRefusal.FramebufferNotBound, next.Reason);
        Assert.Equal(refusal.Command, next.Command);
        list.End();
    }
}
