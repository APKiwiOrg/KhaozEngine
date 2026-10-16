using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

internal sealed record CoverageTargetReadback(int Width, int Height, int Channels, float[] Values)
{
    internal int SourceRow { get; init; } = -1;
}

/// <summary>Disjoint CPU stages inside coverage staging and render resolution, for optional cost attribution.
/// GeometryEvaluation is the whole evaluator call: construction, interpolation, geometry certification and normals.
/// RenderCertification is the late per-preparation validation only, nested inside the render resolution.</summary>
internal enum CoverageStageSpan { Skinning, GeometryEvaluation, RenderCertification }

public sealed partial class Scene3D
{
    // Groups whose target outlines this frame actually rendered and composed, distinct from retained mask storage.
    int _targetOutlineGroupsRendered;
    internal int TargetOutlineGroupsRenderedForTests => _targetOutlineGroupsRendered;
    /// <summary>Null in ordinary use, so the coverage path pays one null check per bracket and calls nothing.</summary>
    internal Action<CoverageStageSpan, bool>? CoverageStageObserverForTests { get; set; }
    internal (int Preparations, int EquipmentStages) CoverageArenaCapacityForTests =>
        (_coverageArena.Preparations.Capacity, _coverageArena.Equipment.Capacity);
    internal ReadOnlySpan<System.Numerics.Matrix4x4> CoveragePointMatricesForTests =>
        CollectionsMarshal.AsSpan(_coveragePointMatrices);
    internal bool CoverageMainVisibleForTests(SkinnedCoverageFrame frame)
    {
        var actor = RequireCoverage(frame).Actor;
        foreach (var draw in _cpuSkinnedDraws) if (draw.Motion == actor && draw.VisibleMain) return true;
        return false;
    }
    internal CoverageTargetReadback ReadCoverageDepthForTests() => ReadCoverageTarget(_res.DepthColorTex);
    internal CoverageTargetReadback ReadCoverageReconstructionColourForTests() => ReadCoverageTarget(_res.ColorTex);
    internal CoverageTargetReadback ReadCoverageNormalForTests() => ReadCoverageTarget(_res.NormalTex);
    internal CoverageTargetReadback ReadCoverageCascadesForTests() => ReadCoverageTarget(_model.ShadowMap.ShadowTexture);
    internal CoverageTargetReadback? ReadCoveragePointsForTests(bool transient)
    {
        var atlas = transient ? _pointShadowTransientAtlas : _pointShadowAtlas;
        if (atlas is null) return null;
        int row = -1;
        foreach (var packed in _packedPointSlots)
            if (ReferenceEquals(packed.Atlas, atlas))
            {
                if (row >= 0) throw new InvalidOperationException("Coverage diagnostic requires one selected point row.");
                row = packed.Row;
            }
        if (row < 0 && !transient && _pointRequests.Count == 1) row = _pointRequests[0].Slot;
        if (row < 0) return null;
        var full = ReadCoverageTarget(atlas.Texture);
        int count = full.Width * atlas.FaceResolution;
        var values = new float[count];
        Array.Copy(full.Values, row * count, values, 0, count);
        return new(full.Width, atlas.FaceResolution, 1, values) { SourceRow = row };
    }
    internal (CoverageTargetReadback Full, CoverageTargetReadback Visible) ReadCoverageOutlinesForTests()
    {
        var (full, visible) = _targetOutlines.CoverageTargetsForTests;
        return (ReadCoverageTarget(full), ReadCoverageTarget(visible));
    }

    CoverageTargetReadback ReadCoverageTarget(IGpuTexture texture)
    {
        int channels = texture.Format switch
        {
            GpuPixelFormat.R32Float or GpuPixelFormat.R8UNorm => 1,
            GpuPixelFormat.R8G8B8A8UNorm => 4,
            GpuPixelFormat.R16G16Float => 2,
            _ => throw new InvalidOperationException($"Unsupported diagnostic target: {texture.Format}"),
        };
        using var staging = _gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(texture.Width,
            texture.Height, texture.Format, GpuTextureUsage.Staging));
        using var commands = _gd.Factory.CreateCommandList();
        using (GpuRecording.Open(_gd, commands, "Coverage.ReadTarget")) commands.CopyTexture(texture, staging);
        _gd.Submit(commands);
        _gd.WaitForIdle();
        var values = new float[texture.Width * texture.Height * channels];
        var map = _gd.Map(staging, GpuMapMode.Read);
        try
        {
            int rowValues = (int)texture.Width * channels;
            for (int y = 0; y < texture.Height; y++)
            {
                IntPtr row = map.Data + (int)(y * map.RowPitch);
                for (int x = 0; x < rowValues; x++)
                    values[y * rowValues + x] = texture.Format == GpuPixelFormat.R32Float
                        ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(row, x * 4))
                        : texture.Format == GpuPixelFormat.R16G16Float
                            ? (float)BitConverter.Int16BitsToHalf(Marshal.ReadInt16(row, x * 2))
                            : Marshal.ReadByte(row, x) / 255f;
            }
        }
        finally { _gd.Unmap(staging); }
        return new((int)texture.Width, (int)texture.Height, channels, values);
    }
}
