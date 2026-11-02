using System;
using System.Collections.Generic;
using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Diagnostics;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class SceneFramePassObserverTests
{
    [Fact]
    public void Pass_boundaries_reach_the_sink_in_order_and_cpu_phases_do_not()
    {
        using var device = new FakeGpuDevice();
        var commands = device.Factory.CreateCommandList();
        var sink = new Sink();
        var observer = new SceneFramePassObserver(sink);
        observer.BeginCpuPhase("cpu.ignored");
        observer.BeginPass(commands, new("a", 1));
        observer.SkipPass(commands, new("b", 2));
        observer.EndPass(commands, new("a", 1));
        observer.EndCpuPhase("cpu.ignored");
        Assert.Equal(new[]
        {
            (commands, FrameDiagnosticsPassEvent.Begin, "a", 1),
            (commands, FrameDiagnosticsPassEvent.Skip, "b", 2),
            (commands, FrameDiagnosticsPassEvent.End, "a", 1),
        }, sink.Calls);
    }

    [Fact]
    public void A_throwing_sink_cannot_make_the_scene_throw_after_recording()
    {
        using var device = new FakeGpuDevice();
        var commands = device.Factory.CreateCommandList();
        var observer = new SceneFramePassObserver(new Sink { Throw = true });
        observer.BeginPass(commands, new("a"));
        observer.SkipPass(commands, new("a"));
        observer.EndPass(commands, new("a"));
    }

    sealed class Sink : IFrameDiagnosticsPassSink
    {
        internal bool Throw;
        internal readonly List<(IGpuCommandList, FrameDiagnosticsPassEvent, string, int)> Calls = [];
        public void Pass(IGpuCommandList commands, FrameDiagnosticsPassEvent kind, string id, int occurrence)
        {
            if (Throw) throw new InvalidOperationException();
            Calls.Add((commands, kind, id, occurrence));
        }
    }
}
