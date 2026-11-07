using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu.Performance;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFrameBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outer_pair_contains_host_clear_ui_and_end_tail(bool detailed)
    {
        using var rig = new BoundaryRig(detailed);
        RendererPerfFrameSample[] samples = new RendererPerfFrameSample[3];
        rig.Session.Frame(0, ref samples[0]);
        string[] expected = detailed
            ? ["begin", "marker:0", "bind", "clear", "marker:1", "scene", "draw", "marker:2", "marker:3", "ui", "draw", "marker:4", "native.finalize", "marker:5", "submit", "present"]
            : ["begin", "marker:0", "bind", "clear", "scene", "draw", "ui", "draw", "native.finalize", "marker:1", "submit", "present"];
        Assert.Equal(expected, rig.Trace);
        rig.Session.CompleteBatch(samples.AsSpan(0, 1));
        Assert.Empty(samples[0].Errors);
        Assert.Equal(detailed ? 6U : 2U, rig.Device.Pools[0].Count);
        int first = rig.Trace.IndexOf("marker:0");
        int last = rig.Trace.IndexOf(detailed ? "marker:5" : "marker:1");
        Assert.True(first < rig.Trace.IndexOf("bind"));
        Assert.True(first < rig.Trace.IndexOf("clear"));
        Assert.True(last > rig.Trace.IndexOf("ui"));
        Assert.True(last > rig.Trace.IndexOf("native.finalize"));
        Assert.Equal(1, rig.Device.Submissions);
        Assert.Equal(1, rig.Device.Pools.Sum(p => p.Resolves));
        Assert.Equal(detailed ? 2 : 0, samples[0].Passes.Length);
        Assert.NotNull(samples[0].GpuElapsedTicks);
    }

    [Fact]
    public void Cpu_wall_contains_host_return_and_separates_batch_drain()
    {
        using var rig = new BoundaryRig(false);
        RendererPerfFrameSample[] samples = new RendererPerfFrameSample[1];
        rig.Session.Frame(0, ref samples[0]);
        long returned = rig.Tick;
        rig.Session.CompleteBatch(samples);
        RendererPerfFrameSample sample = samples[0];
        Assert.Equal(returned, sample.CpuEndTicks);
        Assert.Equal(rig.PresentEnd, sample.CpuEndTicks);
        RendererPerfSessionInfo info = rig.Session.ReadInfo();
        Assert.NotNull(info.Drain);
        Assert.True(info.Drain.StartTicks >= sample.CpuEndTicks);
        Assert.Equal(50, info.Drain.EndTicks - info.Drain.StartTicks);
        Assert.True(info.Harvest!.StartTicks >= info.Drain.EndTicks);
        Assert.Contains(sample.CpuPhases, p => p.Id == "present.end-frame" && p.EndTicks == sample.CpuEndTicks);
        Assert.All(sample.CpuPhases, p => Assert.True(p.EndTicks <= sample.CpuEndTicks));
    }

    [Fact]
    public void Unexpected_prepare_submission_invalidates_steady_frame()
    {
        using var rig = new BoundaryRig(false, extraPrepare: true);
        RendererPerfFrameSample[] samples = new RendererPerfFrameSample[1];
        rig.Session.Frame(0, ref samples[0]);
        rig.Session.CompleteBatch(samples);
        Assert.NotEmpty(samples[0].Errors);
        Assert.Contains(samples[0].Errors, e => e.Contains("submission", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, rig.Device.Submissions);
        Assert.NotNull(samples[0].GpuElapsedTicks);
    }

    [Fact]
    public void Native_short_batch_is_consumed_once_and_pools_are_not_dropped()
    {
        using var rig = new BoundaryRig(false, count: 4);
        RendererPerfFrameSample[] samples = new RendererPerfFrameSample[4];
        rig.Session.Frame(0, ref samples[0]);
        rig.Session.Frame(1, ref samples[1]);
        rig.Session.Frame(2, ref samples[2]);
        Assert.Throws<InvalidOperationException>(() => rig.Session.Frame(3, ref samples[3]));
        Assert.Equal(0, rig.Device.Waits);
        rig.Session.CompleteBatch(samples.AsSpan(0, 3));
        rig.Session.Frame(3, ref samples[3]);
        rig.Session.CompleteBatch(samples.AsSpan(3, 1));
        Assert.Equal(4, rig.Device.Submissions);
        Assert.Equal(2, rig.Device.Waits);
        Assert.Equal(4, rig.Device.Pools.Sum(p => p.Resolves));
        Assert.All(samples, s => Assert.Empty(s.Errors));
        Assert.Throws<InvalidOperationException>(() => rig.Session.CompleteBatch(samples.AsSpan(3, 1)));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("no-timestamp")]
    [InlineData("software")]
    [InlineData("wrong-device")]
    public void Native_admission_happens_before_clock_or_workload(string fault)
    {
        using var device = new GpuPerfTimestampTestDevice();
        device.Features = new(false, true, fault == "wrong-device" ? "other GPU" : "NVIDIA T4", supportsCompletionFences: true)
        { SupportsTimestamps = fault != "no-timestamp" };
        device.Software = fault == "software";
        var identity = RendererPerfProtocolTests.CompleteReport().Identity;
        int calls = 0;
        Assert.Throws<RendererPerfProtocolException>(() => new RendererPerfNativeSession(device,
            new(identity, fault != "disabled", true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty),
            new("S0", RendererPerfLayout.TotalOnly, 0, 0, 1), [],
            new(() => { calls++; return 0; }, 1000), (d, o) => throw new InvalidOperationException("work opened")));
        Assert.Equal(0, calls);
        Assert.Empty(device.Pools);
    }

    [Fact]
    public void Rejected_frozen_layout_releases_its_timestamp_pools()
    {
        using var device = new GpuPerfTimestampTestDevice();
        device.Features = new(false, true, "NVIDIA T4", supportsCompletionFences: true) { SupportsTimestamps = true };
        var identity = RendererPerfProtocolTests.CompleteReport().Identity;
        var failure = Assert.Throws<GpuDiagnosticFailureException>(() => new RendererPerfNativeSession(device,
            new(identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty),
            new("S0", RendererPerfLayout.PerPass, 0, 0, 1), [new("bad", 0, -1, 2, 1)], new(() => 0, 1000),
            (d, o) => throw new InvalidOperationException("work must not open")));
        Assert.IsType<ArgumentException>(Assert.Single(failure.Failures).Error);
        Assert.Equal(3, device.Pools.Count);
        Assert.All(device.Pools, pool => Assert.Equal(1, pool.Disposals));
    }

    internal sealed class BoundaryRig : IDisposable
    {
        internal readonly GpuPerfTimestampTestDevice Device = new();
        internal readonly List<string> Trace = [];
        internal long Tick, PresentEnd;
        internal readonly RendererPerfNativeSession Session;
        internal BoundaryRig(bool detailed, bool extraPrepare = false, int count = 1, int? notReadyPolls = null,
            long completionDeadline = long.MaxValue)
        {
            Device.Features = new(false, true, "NVIDIA T4", supportsCompletionFences: true) { SupportsTimestamps = true };
            if (notReadyPolls is { } polls)
                Device.PoolFactory = capacity =>
                {
                    var pool = new GpuPerfTimestampAwaitTestPool(capacity, polls) { Owners = Device.Fences, OnPoll = () => Tick += 7 };
                    Device.Pools.Add(pool);
                    return pool;
                };
            Device.Trace = op =>
            {
                Trace.Add(op);
                Tick += op == "drain" ? 50 : 1;
                if (op == "present") PresentEnd = Tick;
            };
            RendererPerfPassDefinition[] definitions = detailed
                ? [new("scene", 0, -1, 1, 2), new("ui.sprites", 0, -1, 3, 4)] : [];
            RendererPerfRunIdentity identity = RendererPerfProtocolTests.CompleteReport().Identity with { TimestampPeriodNs = 2.5 };
            Session = new(Device, new(identity, true, true, null, ImmutableDictionary<string, RendererPerfMetric>.Empty),
                new("S0", detailed ? RendererPerfLayout.PerPass : RendererPerfLayout.TotalOnly, 0, 0, count), definitions,
                new(() => Tick, 1000), (device, observer) => new Work(this, device, observer, extraPrepare),
                completionDeadline: completionDeadline);
            Trace.Clear();
        }
        public void Dispose() { Session.Dispose(); Device.Dispose(); }
        sealed class Work : IRendererPerfNativeWork
        {
            readonly BoundaryRig _rig;
            readonly IGpuDevice _device;
            readonly IRenderPassObserver _observer;
            readonly bool _extraPrepare;
            readonly IGpuTexture _texture;
            internal Work(BoundaryRig rig, IGpuDevice device, IRenderPassObserver observer, bool extraPrepare)
            {
                _rig = rig; _device = device; _observer = observer; _extraPrepare = extraPrepare;
                _texture = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget));
                Target = device.Factory.CreateFramebuffer(null, _texture);
            }
            public IGpuFramebuffer Target { get; }
            public Color ClearColor => Color.Black;
            public RendererPerfRecipeReport? Recipe => null;
            public void PrepareCold(int firstFrameIndex) => _rig.Tick += 100;
            public void Prepare(int frameIndex)
            {
                if (_extraPrepare)
                {
                    using var list = _device.Factory.CreateCommandList();
                    list.Begin(); list.End(); _device.Submit(list);
                }
            }
            public void Record(IGpuCommandList commands, int frameIndex)
            {
                _observer.BeginPass(commands, new("scene"));
                _rig.Trace.Add("scene"); commands.Draw(3);
                _observer.EndPass(commands, new("scene"));
                _observer.BeginPass(commands, new("ui.sprites"));
                _rig.Trace.Add("ui"); commands.Draw(6);
                _observer.EndPass(commands, new("ui.sprites"));
            }
            public void Dispose() { Target.Dispose(); _texture.Dispose(); }
        }
    }
}
