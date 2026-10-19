using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Counts actual recorded commands, without a device or a clock.</summary>
internal sealed class InteriorCutCommandCounters
{
    internal sealed class Counts
    {
        internal int Evaluations, Lookups, Draws, Switches, Binds;
        public override string ToString() => $"{Evaluations}/{Lookups}/{Draws}/{Switches}/{Binds}";
    }

    internal Dictionary<string, Counts> Passes { get; } = new();
    internal Dictionary<string, int> MeshRuns { get; } = new();
    internal Counts For(string pass)
    {
        if (!Passes.TryGetValue(pass, out Counts? counts)) Passes.Add(pass, counts = new());
        return counts;
    }

    internal void Read(IReadOnlyList<RecordingGpuCommandList.Command> commands)
    {
        IGpuPipeline? previous = null;
        string cascade = "cascade0";
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.Pipeline is not FakePipeline { Request: { } request }) continue;
            string? pass = Pass(request);
            if (pass == "cascade")
            {
                // BeginCascade binds its pipeline before the dynamic cascade slot. Attribute both commands
                // to that upcoming slot, including the initial depth-pass bind before cascade zero.
                for (int j = i; j < commands.Count; j++)
                {
                    var next = commands[j];
                    if (next.Kind == "draw") break;
                    if (next.Kind == "bind" && next.Slot == 0)
                    {
                        cascade = $"cascade{next.Value / 256u}";
                        break;
                    }
                }
                pass = cascade;
            }
            if (pass is not null)
            {
                Counts counts = For(pass);
                if (command.Kind == "draw") counts.Draws++;
                if (command.Kind == "bind") counts.Binds++;
                if (command.Kind == "pipeline" && !ReferenceEquals(previous, command.Pipeline)) counts.Switches++;
            }
            if (command.Kind == "pipeline") previous = command.Pipeline;
        }
    }

    static string? Pass(FakeGraphicsPipelineRequest request)
    {
        string vertex = request.VertexGlsl, fragment = request.FragmentGlsl;
        if (vertex == ShaderSources.ModelVert) return "colour";
        if (vertex == ShaderSources.ModelMotionVert) return "temporal";
        if (vertex == ShaderSources.ShadowDepthVert || vertex == ShaderSources.ShadowDepthDissolveVert
            || vertex == ShaderSources.ShadowDepthCutoutVert) return "cascade";
        if (vertex == ShaderSources.PointShadowRigidVert || vertex == ShaderSources.PointShadowRigidDissolveVert
            || vertex == ShaderSources.PointShadowClearVert)
            return "point";
        if (vertex == ShaderSources.TargetOutlineMaskVert || fragment == ShaderSources.TargetOutlineCompositeFrag) return "outline";
        if (vertex == ShaderSources.SilhouetteVert) return "silhouette";
        return null;
    }
}
