using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal enum CoverageGpuPhase
{
    Reset, Skin, ReduceSkin, ValidateSource, ReduceSource, ValidateAliases, ReduceAliases,
    InterpolateGroups, ReduceGroups, ValidateAttributes, ReduceAttributes, FillBefore,
    ConformGroups, ConformBoundary, ConformResume, CopyPointAliases, ReduceConform,
    CorrectFaces, CorrectFacesOverlay, CorrectFacesFinish, ReduceFaces, GatherCorrections,
    CheckFullTargets, ReduceFullTargets, CopyCorrectedAliases, TransitionGroups, StoreTransition,
    ValidateVertices, ReduceVertices, PrepareNormalBasis, FinishNormalBasis, ValidateFaces, ReduceFacesOrientation,
    GatherAttributes, ReduceAttributesFinal, Select
}

/// <summary>Standalone batched coverage phases. The caller owns recording and submission completion.</summary>
internal sealed class CoverageGpuPipeline : IDisposable
{
    readonly IGpuResourceLayout layout;
    readonly IGpuComputeShader[] shaders;
    readonly IGpuComputePipeline[] pipelines;

    internal CoverageGpuPipeline(IGpuDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        CoverageGpuPhase[] phases = Enum.GetValues<CoverageGpuPhase>();
        shaders = new IGpuComputeShader[phases.Length];
        pipelines = new IGpuComputePipeline[phases.Length];
        layout = device.Factory.CreateResourceLayout(new(
            new("CovStaticBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovFrameBuf", GpuResourceKind.StructuredBufferReadOnly, GpuShaderStages.Compute),
            new("CovWorkBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovCurrentBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovPreviousBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute),
            new("CovResultsBuf", GpuResourceKind.StructuredBufferReadWrite, GpuShaderStages.Compute)));
        try
        {
            foreach (CoverageGpuPhase phase in phases)
            {
                shaders[(int)phase] = device.Factory.CreateComputeShaderFromSpirv(CoverageGpuSkinRefineGlsl.Source(phase), new(PreciseMath: true));
                pipelines[(int)phase] = device.Factory.CreateComputePipeline(new(shaders[(int)phase], layout));
            }
        }
        catch { Dispose(); throw; }
    }

    internal IEnumerable<(CoverageGpuPhase Phase, IGpuComputePipeline Pipeline)> Pipelines
    {
        get { foreach (CoverageGpuPhase phase in Enum.GetValues<CoverageGpuPhase>()) yield return (phase, pipelines[(int)phase]); }
    }

    internal void Prepare(CoverageGpuBatch batch) => batch.Prepare(layout);

    internal void Record(IGpuCommandList commands, CoverageGpuBatch batch) =>
        RecordPhases(commands, batch, CoverageGpuPhase.Skin, CoverageGpuPhase.Select);

    internal void RecordPhases(IGpuCommandList commands, CoverageGpuBatch batch, CoverageGpuPhase firstPhase, CoverageGpuPhase lastPhase)
    {
        ArgumentNullException.ThrowIfNull(commands); ArgumentNullException.ThrowIfNull(batch);
        if (firstPhase < CoverageGpuPhase.Skin || lastPhase < firstPhase || lastPhase > CoverageGpuPhase.Select)
            throw new ArgumentOutOfRangeException(nameof(firstPhase));
        if (batch.Count == 0) return;
        IGpuResourceSet set = batch.Bind(layout);
        Dispatch(CoverageGpuPhase.Reset);
        for (CoverageGpuPhase phase = firstPhase; phase <= lastPhase; phase++) Dispatch(phase);
        void Dispatch(CoverageGpuPhase phase)
        {
            uint elements = batch.Elements(phase);
            bool reduce = CoverageGpuSkinRefineGlsl.IsReducer(phase);
            commands.SetComputePipeline(pipelines[(int)phase]);
            commands.SetComputeResourceSet(0, set);
            commands.Dispatch(reduce ? 1u : (elements + 63) / 64, (uint)batch.Count, 1);
        }
    }

    public void Dispose()
    {
        foreach (IGpuComputePipeline? pipeline in pipelines) pipeline?.Dispose();
        foreach (IGpuComputeShader? shader in shaders) shader?.Dispose();
        layout.Dispose();
    }
}
