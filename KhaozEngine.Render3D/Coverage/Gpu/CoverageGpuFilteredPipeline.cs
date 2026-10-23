using System;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Producer, GPU compaction, count-gated exact consumer and ordered replay.</summary>
internal sealed class CoverageGpuFilteredPipeline : IDisposable
{
    readonly IGpuComputeShader[] shaders = new IGpuComputeShader[7];
    readonly IGpuComputePipeline[] pipelines = new IGpuComputePipeline[7];

    internal CoverageGpuFilteredPipeline(IGpuDevice device, IGpuResourceLayout layout, CoverageGpuFilterPath path)
    {
        try
        {
            foreach (var stage in Enum.GetValues<CoverageGpuFilteredStage>())
            {
                shaders[(int)stage] = device.Factory.CreateComputeShaderFromSpirv(CoverageGpuFilteredSources.Source(stage),
                    CoverageGpuFilteredSources.Options(stage, path));
                pipelines[(int)stage] = device.Factory.CreateComputePipeline(new(shaders[(int)stage], layout));
            }
        }
        catch { Dispose(); throw; }
    }

    internal void Record(IGpuCommandList commands, CoverageGpuBatch batch, IGpuResourceSet set, CoverageGpuPhase phase)
    {
        uint faces = batch.Elements(CoverageGpuPhase.CorrectFaces);
        uint pairs = checked(faces * batch.MaximumFilterSupportCount);
        if (phase == CoverageGpuPhase.CorrectFaces)
        {
            Dispatch(CoverageGpuFilteredStage.Faces, faces);
            Dispatch(CoverageGpuFilteredStage.CompactFaces, 1);
            Dispatch(CoverageGpuFilteredStage.ExactFaces, faces);
        }
        else
        {
            Dispatch(CoverageGpuFilteredStage.Pairs, pairs);
            Dispatch(CoverageGpuFilteredStage.CompactPairs, 1);
            Dispatch(CoverageGpuFilteredStage.ExactPairs, pairs);
            Dispatch(CoverageGpuFilteredStage.Replay, faces);
        }
        void Dispatch(CoverageGpuFilteredStage stage, uint count)
        {
            commands.SetComputePipeline(pipelines[(int)stage]);
            commands.SetComputeResourceSet(0, set);
            uint groups = Math.Max(1u, (count + 63) / 64);
            commands.Dispatch(Math.Min(65535u, groups), (uint)batch.Count, (groups + 65534u) / 65535u);
        }
    }

    public void Dispose()
    {
        foreach (var pipeline in pipelines) pipeline?.Dispose();
        foreach (var shader in shaders) shader?.Dispose();
    }
}
