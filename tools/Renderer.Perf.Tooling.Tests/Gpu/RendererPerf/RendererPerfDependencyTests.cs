using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfDependencyTests
{
    [Theory]
    [InlineData(65)]
    [InlineData(66)]
    public void Ocean_column_reads_the_prior_row_set_in_both_ping_pong_directions(int previousFrame)
    {
        using var rig = new PostTraceRig("outdoors");
        rig.Frame(64);
        if (previousFrame == 66) rig.Frame(65);
        rig.Scene.PassObserver = new PostTraceObserver(rig.Commands);
        rig.Frame(previousFrame);
        object priorRowOutput = ComputeSet(rig.Commands, "ocean.next-row").Resources[2];
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(previousFrame + 1);
        var row = ComputeSet(rig.Commands, "ocean.next-row");
        var column = ComputeSet(rig.Commands, "ocean.column");
        Assert.Same(priorRowOutput, column.Resources[1]);
        Assert.NotSame(row.Resources[2], column.Resources[1]);
        var model = Assert.Single(RendererPerfWorkModels.ForRegistry(observer.Freeze(), "ping-pong fixture"),
            m => m.PassId == "ocean.column");
        Assert.Empty(model.SerialDependencies);
    }

    [Theory]
    [InlineData("outdoors")]
    [InlineData("all")]
    [InlineData("split")]
    public void Every_declared_dependency_has_a_same_frame_resource_path(string mode)
    {
        using var rig = new PostTraceRig(mode);
        rig.Frame(64); rig.Frame(65); rig.Frame(66); rig.Frame(67);
        var observer = new PostTraceObserver(rig.Commands);
        rig.Scene.PassObserver = observer;
        rig.Frame(68);
        var graph = new ResourceDependencies(rig.Commands);
        var definitions = observer.Freeze();
        var duration = RendererPerfDriver.Value(1, "ns", "synthetic execution state from the real trace");
        var passes = definitions.Select(pass => new RendererPerfPassSample(pass.Id, pass.Occurrence,
            pass.Parent < 0 ? null : definitions[pass.Parent].Id,
            pass.Parent < 0 ? null : definitions[pass.Parent].Occurrence, pass.BeginMarker, pass.EndMarker, null,
            observer.Skipped.Any(key => key.Id == pass.Id && key.Occurrence == pass.Occurrence)
                ? new(null, "ns", "real trace", RendererPerfMetricStatus.NotExecuted, "The producer skipped this occurrence.")
                : pass.DeviceOnly ? new(null, "ns", "real trace", RendererPerfMetricStatus.Unsupported, "CPU-only owner.")
                : duration, pass.DeviceOnly)).ToImmutableArray();
        var frame = new RendererPerfFrameSample(0, 0, 0, 1, 1, null, null, duration, passes, [],
            ImmutableDictionary<string, RendererPerfMetric>.Empty, []);
        var models = RendererPerfWorkModels.ForFrame(frame, "resource dependency fixture");
        int checkedEdges = 0;
        foreach (var model in models.Where(m => graph.Executed.Contains(m.PassId)))
            foreach (string producer in model.SerialDependencies)
            {
                Assert.True(graph.HasPath(producer, model.PassId), $"No resource path from {producer} to {model.PassId}.");
                checkedEdges++;
            }
        Assert.True(checkedEdges > 0);
    }

    [Theory]
    [InlineData("temporal.resolve", "temporal.outline-inputs", "copy.opaque-temporal", false)]
    [InlineData("temporal.resolve", "temporal.outline-inputs", "copy.opaque-temporal", true)]
    [InlineData("temporal.accumulate", "temporal.outline-inputs", "copy.opaque-temporal", false)]
    [InlineData("temporal.accumulate", "temporal.outline-inputs", "copy.opaque-temporal", true)]
    [InlineData("water.surface", "ocean.column", "ocean.mips", false)]
    [InlineData("water.surface", "ocean.column", "ocean.mips", true)]
    [InlineData("water.surface", "ocean.mips", "ocean.column", false)]
    [InlineData("water.surface", "ocean.mips", "ocean.column", true)]
    public void Frame_dependencies_follow_optional_producer_execution(string consumer, string producer, string other, bool executed)
    {
        var duration = RendererPerfDriver.Value(1, "ns", "synthetic dependency fixture");
        var frame = new RendererPerfFrameSample(0, 0, 0, 1, 1, null, null, duration,
            [new(consumer, 0, null, null, 1, 2, null, duration, false),
                new(producer, 0, null, null, 3, 4, null, executed ? duration
                    : new(null, "ns", "explicit skipped producer", RendererPerfMetricStatus.NotExecuted, "Disabled in this frame."), false)],
            [], ImmutableDictionary<string, RendererPerfMetric>.Empty, []);
        var model = Assert.Single(RendererPerfWorkModels.ForFrame(frame, "optional dependency fixture"), m => m.PassId == consumer);
        Assert.Equal(executed, model.SerialDependencies.Contains(producer));
        Assert.Contains(other, model.SerialDependencies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Frame_dependency_is_retained_when_any_producer_occurrence_executes(bool secondExecuted)
    {
        var duration = RendererPerfDriver.Value(1, "ns", "synthetic dependency fixture");
        var skipped = new RendererPerfMetric(null, "ns", "explicit skipped occurrence", RendererPerfMetricStatus.NotExecuted, "Disabled in this frame.");
        var frame = new RendererPerfFrameSample(0, 0, 0, 1, 1, null, null, duration,
            [new("ocean.mips", 0, null, null, 1, 2, null, duration, false),
                new("ocean.mip-copy", 0, null, null, 3, 4, null, skipped, false),
                new("ocean.mip-copy", 1, null, null, 5, 6, null, secondExecuted ? duration : skipped, false)],
            [], ImmutableDictionary<string, RendererPerfMetric>.Empty, []);
        var model = Assert.Single(RendererPerfWorkModels.ForFrame(frame, "mixed occurrence fixture"), m => m.PassId == "ocean.mips");
        Assert.Equal(secondExecuted, model.SerialDependencies.Contains("ocean.mip-copy"));
    }

    static FakeResourceSet ComputeSet(GeometryTraceCommands commands, string pass)
        => Assert.IsType<FakeResourceSet>(commands.Resource(
            Assert.Single(commands.Events, e => e.Op == "compute-set" && e.Owner?.Id == pass).A));

    // Resource identities come from real producer bindings over inert handles, not pass-name ordering.
    sealed class ResourceDependencies
    {
        readonly Dictionary<object, string> _writers = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, HashSet<string>> _parents = [];
        internal readonly HashSet<string> Executed = [];

        internal ResourceDependencies(GeometryTraceCommands commands)
        {
            FakeFramebuffer? framebuffer = null;
            FakeResourceSet? graphics = null, compute = null;
            bool recording = false;
            foreach (var entry in commands.Events)
            {
                if (entry.Op == "begin") { recording = true; continue; }
                if (!recording || entry.Owner?.Id is not { } owner) continue;
                switch (entry.Op)
                {
                    case "framebuffer": framebuffer = Assert.IsType<FakeFramebuffer>(commands.Resource(entry.A)); break;
                    case "set":
                    case "set-offset": graphics = Assert.IsType<FakeResourceSet>(commands.Resource(entry.A)); break;
                    case "compute-set":
                    case "compute-set-offset": compute = Assert.IsType<FakeResourceSet>(commands.Resource(entry.A)); break;
                    case "draw":
                    case "draw-instanced":
                    case "draw-indexed":
                        Executed.Add(owner);
                        if (graphics is not null)
                            foreach (var resource in graphics.Resources) Read(owner, resource);
                        if (framebuffer is not null)
                            foreach (var texture in framebuffer.Colour) Write(owner, texture);
                        break;
                    case "dispatch" when owner == "ocean.next-row":
                        Executed.Add(owner);
                        Assert.NotNull(compute);
                        Read(owner, compute.Resources[0]); Read(owner, compute.Resources[1]);
                        Write(owner, compute.Resources[2]);
                        break;
                    case "dispatch" when owner == "ocean.column":
                        Executed.Add(owner);
                        Assert.NotNull(compute);
                        Read(owner, compute.Resources[0]); Read(owner, compute.Resources[1]); Read(owner, compute.Resources[2]);
                        Write(owner, compute.Resources[2]); Write(owner, compute.Resources[3]);
                        break;
                    case "copy-texture":
                    case "copy-subresource":
                    case "copy-subresources":
                    case "resolve":
                        Executed.Add(owner);
                        Read(owner, commands.Resource(entry.A)); Write(owner, commands.Resource(entry.B));
                        break;
                    case "mips":
                        Executed.Add(owner);
                        Read(owner, commands.Resource(entry.A)); Write(owner, commands.Resource(entry.A));
                        break;
                    case "clear-color" when framebuffer is not null:
                        Write(owner, framebuffer.Colour[(int)entry.X]);
                        break;
                }
            }
        }

        void Read(string owner, object resource)
        {
            if (!_writers.TryGetValue(resource, out string? writer) || writer == owner) return;
            if (!_parents.TryGetValue(owner, out var parents)) _parents.Add(owner, parents = []);
            parents.Add(writer);
        }
        void Write(string owner, object resource) => _writers[resource] = owner;

        internal bool HasPath(string producer, string consumer)
        {
            HashSet<string> seen = [];
            Stack<string> pending = new();
            pending.Push(consumer);
            while (pending.TryPop(out string? current))
            {
                if (!seen.Add(current)) continue;
                if (!_parents.TryGetValue(current, out var parents)) continue;
                if (parents.Contains(producer)) return true;
                foreach (string parent in parents) pending.Push(parent);
            }
            return false;
        }
    }
}
