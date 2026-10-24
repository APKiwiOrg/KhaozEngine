using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

// The first frame defers point-atlas installation. The next rebuilds static rows, then
// cached rows admit transient actors. Freeze their union without rendering a native frame.
internal static class RendererPerfFrozenLayout
{
    sealed class Node(string id, int occurrence, bool deviceOnly)
    {
        internal readonly string Id = id;
        internal readonly int Occurrence = occurrence;
        internal readonly bool DeviceOnly = deviceOnly;
        internal readonly List<Node> Children = [];
        internal readonly HashSet<(Node, Node)> Edges = [];
    }

    internal static RendererPerfPassDefinition[] Create(RendererPerfRecipeIdentity identity)
    {
        using var device = new PostTraceDevice();
        using var commands = device.Factory.CreateCommandList();
        var trace = new GeometryTraceCommands(commands);
        var observer = new Collector(trace);
        using var work = new RendererPerfSceneWorkload(device, observer, identity, (_, _) => { });
        work.PrepareCold(-192);
        var root = new Node("root", 0, false);
        var nodes = new Dictionary<RenderPassKey, Node>();
        for (int frame = 0; frame < 3; frame++)
        {
            observer.Current = new(trace);
            work.Prepare(-192 + frame);
            trace.Begin(); work.Record(trace, -192 + frame); trace.End();
            observer.Current.AssertClosed();
            var definitions = observer.Current.Freeze();
            var byParent = new Dictionary<Node, List<Node>>();
            foreach (var definition in definitions)
            {
                var key = new RenderPassKey(definition.Id, definition.Occurrence);
                if (!nodes.TryGetValue(key, out var node))
                    nodes.Add(key, node = new(definition.Id, definition.Occurrence, definition.DeviceOnly));
                RendererPerfManifest.Require(node.DeviceOnly == definition.DeviceOnly, "Layout", "Device-only ownership changed between cold and cached paths.");
                var parent = definition.Parent < 0 ? root : nodes[new(definitions[definition.Parent].Id, definitions[definition.Parent].Occurrence)];
                if (!parent.Children.Contains(node)) parent.Children.Add(node);
                if (!byParent.TryGetValue(parent, out var children)) byParent.Add(parent, children = []);
                // The accepted observer relocates only skipped static/dynamic row notifications.
                if (key.Id is not ("shadow.point.static" or "shadow.point.dynamic") || !observer.Current.Skipped.Contains(key))
                    children.Add(node);
            }
            foreach (var (parent, children) in byParent)
                for (int i = 1; i < children.Count; i++) parent.Edges.Add((children[i - 1], children[i]));
        }
        RendererPerfManifest.Require(nodes.Count <= 255, "Layout", "The frozen union exceeds the approved scope capacity.");
        var result = new List<RendererPerfPassDefinition>();
        uint marker = 1;
        Emit(root, -1);
        return result.ToArray();

        void Emit(Node parent, int parentIndex)
        {
            var pending = new List<Node>(parent.Children);
            while (pending.Count != 0)
            {
                var next = pending.FirstOrDefault(n => !parent.Edges.Any(e => e.Item2 == n && pending.Contains(e.Item1)))
                    ?? throw new RendererPerfProtocolException("Layout", "Cold and cached execution orders cannot share a frozen layout.");
                pending.Remove(next);
                int index = result.Count;
                result.Add(new(next.Id, next.Occurrence, parentIndex, next.DeviceOnly ? 0 : marker++, 0, next.DeviceOnly));
                Emit(next, index);
                if (!next.DeviceOnly) result[index] = result[index] with { EndMarker = marker++ };
            }
        }
    }

    sealed class Collector(GeometryTraceCommands commands) : IRenderPassObserver
    {
        internal RendererPerfGeometryTraceTests.GeometryObserver Current = new(commands);
        public void BeginPass(IGpuCommandList list, RenderPassKey key) => Current.BeginPass(list, key);
        public void EndPass(IGpuCommandList list, RenderPassKey key) => Current.EndPass(list, key);
        public void SkipPass(IGpuCommandList list, RenderPassKey key) => Current.SkipPass(list, key);
        public void BeginCpuPhase(string id) => Current.BeginCpuPhase(id);
        public void EndCpuPhase(string id) => Current.EndCpuPhase(id);
        public void FusedMotion(IGpuCommandList list, RenderPassKey key) => Current.FusedMotion(list, key);
    }
}
