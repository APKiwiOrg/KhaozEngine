using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfConstructionTests
{
    [Fact]
    public void Measured_first_creation_keeps_frame_and_refuses_promotion()
    {
        using var native = new RendererPerfConstructionFixture();
        long tick = 0; var trace = new List<string>(); native.Trace = op => { tick++; trace.Add(op); };
        var capture = new RendererPerfConstructionCapture(OneOperation());
        using (var session = Session(native, capture, new(() => tick, 1000), (d, _) => new Work(d)))
        {
            RendererPerfFrameSample[] samples = new RendererPerfFrameSample[1];
            session.Frame(0, ref samples[0]); session.CompleteBatch(samples);
            Assert.True(capture.HasMeasuredCreation);
            Assert.Contains("growth", session.ReadInfo().Failure!, StringComparison.Ordinal);
            Assert.Contains("measured", capture.Failure!, StringComparison.Ordinal);
            Assert.Contains(samples[0].Errors, e => e.Contains("growth", StringComparison.Ordinal));
            Assert.Equal(2, samples[0].Metrics["logical.total.ResourceCreations"].Value);
            Assert.True(samples[0].Metrics["allocation.currentThread.bytes"].Value > 0);
            Assert.True(samples[0].CpuEndTicks > samples[0].CpuStartTicks);
            Assert.Equal(1, samples[0].Metrics["logical.total.Dispatches"].Value);
            Assert.Equal(1, native.Submissions); Assert.Equal(1, trace.Count(e => e == "begin"));
            Assert.Equal(0, native.CompilerReads);
            _output.WriteLine("measured retained frames=1 creations=2 dispatches=1 submissions=1 allocationBytes={0}", samples[0].Metrics["allocation.currentThread.bytes"].Value);
        }
        var inventory = capture.Freeze(); Assert.False(inventory.Complete);
        var o = Assert.Single(inventory.Occurrences); Assert.Equal(RendererPerfConstructionStage.Measured, o.Stage); Assert.Equal(0, o.FrameIndex);
        Assert.Equal(1, o.BindCount);
    }

    [Fact]
    public void Constructor_and_dispose_cleanup_end_capture_even_when_existing_cleanup_fails()
    {
        using var native = new RendererPerfConstructionFixture();
        var manifest = OneOperation() with { Blocks = [new("S0", RendererPerfLayout.TotalOnly, 0, 0, 1)] };
        var capture = new RendererPerfConstructionCapture(manifest);
        var original = new InvalidOperationException("original constructor failure");
        IGpuComputeShader? shader = null;
        var construction = Assert.Throws<GpuDiagnosticFailureException>(() => Session(native, capture, new(() => 0, 1000), (d, _) =>
        {
            shader = d.Factory.CreateComputeShaderFromSpirv(ComputeSource); throw original;
        }));
        Assert.Same(original, Assert.Single(construction.Failures).Error);
        // This only succeeds if the actual constructor catch ended its operation.
        capture.BeginOperation(1);
        var device = RendererPerfDevice.Wrap(native, Recorder(), capture);
        using var pipeline = device.Factory.CreateComputePipeline(new(shader!));
        Assert.Contains("operation-local", capture.Failure!, StringComparison.Ordinal);
        capture.EndOperation(); Assert.False(capture.Freeze().Complete);

        var disposeCapture = new RendererPerfConstructionCapture(OneOperation());
        var cleanup = new InvalidOperationException("work release failed");
        var session = Session(native, disposeCapture, new(() => 0, 1000), (d, _) => new Work(d) { Cleanup = cleanup });
        var thrown = Assert.Throws<GpuDiagnosticFailureException>(session.Dispose);
        Assert.Same(cleanup, Assert.Single(thrown.Failures).Error);
        // Freeze refuses active operations. It proves EndOperation ran despite the cleanup failure.
        Assert.NotNull(disposeCapture.Freeze()); session.Dispose();
    }

    [Fact]
    public void Native_frame_exception_wins_when_observer_capacity_was_already_refused()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation());
        var original = new InvalidOperationException("original frame factory failure");
        using (var session = Session(native, capture, new(() => 0, 1000), (d, _) => new Work(d)
        {
            BeforePrepare = device =>
            {
                device.Factory.CreateResourceLayout(new([new(new string('x', 1025), GpuResourceKind.Sampler, GpuShaderStages.Fragment)])).Dispose();
                native.FactoryFailure = original;
            }
        }))
        {
            RendererPerfFrameSample sample = default;
            var frameFailure = Assert.Throws<GpuDiagnosticFailureException>(() => session.Frame(0, ref sample));
            Assert.Same(original, Assert.Single(frameFailure.Failures).Error);
            Assert.Equal(original.Message, session.ReadInfo().Failure);
            Assert.Empty(sample.Metrics);
            Assert.Equal(original.Message, Assert.Single(sample.Errors));
            Assert.Contains("capacity", capture.Failure!, StringComparison.Ordinal);
        }
        var i = capture.Freeze(); Assert.False(i.Complete); Assert.Equal(2, native.FactoryAttempts);
        Assert.Equal(2, i.Counts.OmittedFactoryEvents);
    }

    [Fact]
    public void Actual_session_warmup_creation_is_retained_and_measured_bind_adds_no_creation()
    {
        using var native = new RendererPerfConstructionFixture();
        var capture = new RendererPerfConstructionCapture(OneOperation());
        using (var session = Session(native, capture, new(() => 0, 1000), (d, _) => new Work(d), warmup: 1))
        {
            RendererPerfFrameSample[] samples = new RendererPerfFrameSample[2];
            session.Frame(-1, ref samples[0]); session.CompleteBatch(samples.AsSpan(0, 1));
            session.Frame(0, ref samples[1]); session.CompleteBatch(samples.AsSpan(1, 1));
            Assert.Null(session.ReadInfo().Failure); Assert.Empty(samples[1].Errors);
            Assert.Equal(0, samples[1].Metrics["logical.total.ResourceCreations"].Value);
            Assert.False(capture.HasMeasuredCreation); Assert.Equal(2, native.Submissions);
        }
        var i = capture.Freeze(); Assert.True(i.Complete, i.Reason);
        var o = Assert.Single(i.Occurrences); Assert.Equal(RendererPerfConstructionStage.Warmup, o.Stage);
        Assert.Equal(-1, o.FrameIndex); Assert.Equal(2, o.BindCount);
    }

    static RendererPerfNativeSession Session(RendererPerfConstructionFixture native, RendererPerfConstructionCapture capture,
        RendererPerfClock clock, Func<IGpuDevice, IRenderPassObserver, IRendererPerfNativeWork> work, int warmup = 0)
        => new(native, new(RendererPerfProtocolTests.CompleteReport().Identity with { TimestampPeriodNs = 2.5 }, true, true, null,
            ImmutableDictionary<string, RendererPerfMetric>.Empty), new("S0", RendererPerfLayout.TotalOnly, 0, warmup, 1), [], clock,
            work, capture: capture, operationOrdinal: 0);

    sealed class Work : IRendererPerfNativeWork
    {
        readonly IGpuDevice _device;
        readonly IGpuTexture _texture;
        IGpuComputePipeline? _pipeline;
        internal Exception? Cleanup;
        internal Action<IGpuDevice>? BeforePrepare;
        internal Work(IGpuDevice device)
        {
            _device = device; _texture = device.Factory.CreateTexture(new(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
            Target = device.Factory.CreateFramebuffer(null, _texture);
        }
        public IGpuFramebuffer Target { get; }
        public Color ClearColor => Color.Black;
        public RendererPerfRecipeReport? Recipe => null;
        public void PrepareCold(int firstFrameIndex) { }
        public void Prepare(int frameIndex)
        {
            BeforePrepare?.Invoke(_device); _pipeline ??= Compute(_device);
        }
        public void Record(IGpuCommandList commands, int frameIndex) { commands.SetComputePipeline(_pipeline!); commands.Dispatch(1, 1, 1); }
        public void Dispose()
        {
            _pipeline?.Dispose(); Target.Dispose(); _texture.Dispose();
            if (Cleanup is { } error) throw error;
        }
    }
}
