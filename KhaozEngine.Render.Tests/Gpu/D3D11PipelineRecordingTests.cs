using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11PipelineRecordingTests
{
    [Theory]
    [InlineData((int)D3D11RecordMode.Deferred, false)]
    [InlineData((int)D3D11RecordMode.Deferred, true)]
    [InlineData((int)D3D11RecordMode.Immediate, false)]
    [InlineData((int)D3D11RecordMode.Immediate, true)]
    public void ANullPipelineBindIsRefusedBeforeSpendingAnything(int mode, bool compute)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1808
        var log = new D3D11NativeCallLog();
        var state = new D3D11DeviceState();
        var emitter = new D3D11NativeTraceEmitter(state, log);
        using IGpuCommandList list = D3D11CommandDrivers.Create((D3D11RecordMode)mode, emitter);
        using var target = D3D11BindFixtures.Framebuffer();
        using var layout = D3D11BindFixtures.ShadowLayout();
        using var graphics = D3D11BindFixtures.Pipeline(layout);
        using var kernel = new D3D11BindFlushTests.FakeComputePipeline(layout);

        list.Begin();
        list.SetFramebuffer(target);
        if (compute) list.SetComputePipeline(kernel);
        else list.SetPipeline(graphics);
        log.Reset();
        var stream = (list as D3D11CommandRecorder<D3D11StreamEmitter>)?.Emitter.Stream;
        int recorded = stream?.Count ?? 0;

        Action bindNull = compute ? () => list.SetComputePipeline(null!) : () => list.SetPipeline(null!);
        var refusal = Assert.Throws<ArgumentNullException>(bindNull);
        Assert.Equal("p", refusal.ParamName);
        Assert.Empty(log.Trace);
        if (stream is not null) Assert.Equal(recorded, stream.Count);

        // A refused bind preserves the valid binding that preceded it.
        if (compute) list.Dispatch(1, 1, 1);
        else list.Draw(3);
        list.End();
        D3D11CommandDrivers.Submit(new object(), list, ref emitter);
        Assert.NotEmpty(log.Trace);
    }

    [Theory]
    [InlineData((int)D3D11RecordMode.Deferred, "Draw")]
    [InlineData((int)D3D11RecordMode.Deferred, "DrawSingle")]
    [InlineData((int)D3D11RecordMode.Deferred, "DrawIndexed")]
    [InlineData((int)D3D11RecordMode.Deferred, "Dispatch")]
    [InlineData((int)D3D11RecordMode.Immediate, "Draw")]
    [InlineData((int)D3D11RecordMode.Immediate, "DrawSingle")]
    [InlineData((int)D3D11RecordMode.Immediate, "DrawIndexed")]
    [InlineData((int)D3D11RecordMode.Immediate, "Dispatch")]
    public void ACommandRequiresItsOwnPipelineInThisRecordingBeforeSpendingAnything(int mode, string command)
    {
        // Regression for https://github.com/APKiwiOrg/KhaozEngine/issues/1690
        var log = new D3D11NativeCallLog();
        var state = new D3D11DeviceState();
        var emitter = new D3D11NativeTraceEmitter(state, log);
        using IGpuCommandList list = D3D11CommandDrivers.Create((D3D11RecordMode)mode, emitter);
        using var target = D3D11BindFixtures.Framebuffer();
        using var layout = D3D11BindFixtures.ShadowLayout();
        using var set = D3D11BindFixtures.ShadowSet(layout);
        using var graphics = D3D11BindFixtures.Pipeline(layout);
        using var compute = new D3D11BindFlushTests.FakeComputePipeline(layout);
        using var indices = new TraceBuffer(64);
        bool dispatch = command == "Dispatch";
        Action issue = command switch
        {
            "DrawSingle" => () => list.Draw(3),
            "DrawIndexed" => () => list.DrawIndexed(3, 2, 0, 0, 0),
            "Dispatch" => () => list.Dispatch(1, 1, 1),
            _ => () => list.Draw(3, 2, 0, 0),
        };

        list.Begin();
        list.SetFramebuffer(target);
        list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
        if (dispatch) list.SetPipeline(graphics);
        else list.SetComputePipeline(compute);
        if (dispatch) list.SetComputeResourceSet(0, set);
        else list.SetGraphicsResourceSet(0, set);
        log.Reset();
        var stream = (list as D3D11CommandRecorder<D3D11StreamEmitter>)?.Emitter.Stream;
        int recorded = stream?.Count ?? 0;

        var refusal = Assert.Throws<GpuRecordingStateException>(issue);
        var reason = dispatch ? GpuRecordingStateRefusal.ComputePipelineNotBound : GpuRecordingStateRefusal.GraphicsPipelineNotBound;
        Assert.Equal(reason, refusal.Reason);
        Assert.Equal(GpuBackendKind.Direct3D11Native, refusal.Backend);
        Assert.Equal(command == "DrawSingle" ? "Draw" : command, refusal.Command);
        Assert.Empty(log.Trace);
        if (stream is not null) Assert.Equal(recorded, stream.Count);
        else Assert.Equal(D3D11SlotDirty.Full,
            dispatch ? state.Binds.ComputeDirty(0) : state.Binds.GraphicsDirty(0));

        if (dispatch) list.SetComputePipeline(compute);
        else list.SetPipeline(graphics);
        issue();
        using var nextTarget = D3D11BindFixtures.Framebuffer();
        list.SetFramebuffer(nextTarget);
        list.Dispatch(1, 1, 1);
        issue();
        list.End();
        D3D11CommandDrivers.Submit(new object(), list, ref emitter);
        Assert.Contains(log.Trace, entry => entry.StartsWith(dispatch ? "Dispatch" : "Draw", StringComparison.Ordinal));

        list.Begin();
        list.SetFramebuffer(target);
        list.SetIndexBuffer(indices, GpuIndexFormat.UInt16);
        var next = Assert.Throws<GpuRecordingStateException>(issue);
        Assert.Equal(reason, next.Reason);
        Assert.Equal(refusal.Backend, next.Backend);
        Assert.Equal(refusal.Command, next.Command);
        list.End();
    }
}
