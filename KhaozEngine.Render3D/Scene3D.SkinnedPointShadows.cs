using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Current-pose skinned casters for static transient and dynamic base point-shadow rows.</summary>
public sealed partial class Scene3D
{
    [Flags]
    enum PointShadowCasterSet : byte
    {
        Rigid = 1,
        Skinned = 2,
    }

    readonly List<PointShadowTransientCandidate> _pointTransientCandidates = new();
    readonly List<int> _selectedPointTransientRequests = new();
    int[] _pointTransientRowsByRequest = Array.Empty<int>();
    bool[] _pointDynamicSkinnedRequests = Array.Empty<bool>();
    int _pointTransientDemandThisFrame;
    int _pointTransientRowsRenderedThisFrame;
    int _pointDynamicSkinnedDrawCallsThisFrame;
    int _pointStaticTransientSkinnedDrawCallsThisFrame;

    internal int PointShadowTransientSlotForLight(int lightIndex)
    {
        for (int i = 0; i < _pointRequests.Count; i++)
            if (_pointRequests[i].LightIndex == lightIndex)
                return i < _pointTransientRowsByRequest.Length ? _pointTransientRowsByRequest[i] : -1;
        return -1;
    }

    internal int PointShadowBaseSlotForLight(int lightIndex) =>
        (uint)lightIndex < (uint)_lights.Count && (uint)lightIndex < (uint)_pointSlotUniform.Length
            ? _pointSlotUniform[lightIndex] : -1;

    bool PointRequestTouchesSkinned(in PointShadowRequest request)
    {
        if (UseGpuSkinning)
        {
            foreach (GpuSkinnedDraw draw in _gpuSkinnedDraws)
                if (draw.ShadowKind != ShadowCastKind.None
                    && draw.PointSphere.TouchesShadowingShell(request.Position, request.Radius,
                        request.NearRadius, request.ExclusionMin, request.ExclusionMax))
                    return true;
        }
        else
        {
            foreach (CpuSkinnedDraw draw in _cpuSkinnedDraws)
                if (draw.ShadowKind != ShadowCastKind.None
                    && draw.PointSphere.TouchesShadowingShell(request.Position, request.Radius,
                        request.NearRadius, request.ExclusionMin, request.ExclusionMax))
                    return true;
        }
        return false;
    }

    void ScheduleSkinnedPointShadows(PointShadowSlots cache, PointShadowSettings settings, int frame)
    {
        _pointTransientCandidates.Clear();
        _selectedPointTransientRequests.Clear();
        if (_pointTransientRowsByRequest.Length < _pointRequests.Count)
            Array.Resize(ref _pointTransientRowsByRequest, _pointRequests.Count);
        if (_pointDynamicSkinnedRequests.Length < _pointRequests.Count)
            Array.Resize(ref _pointDynamicSkinnedRequests, _pointRequests.Count);
        Array.Fill(_pointTransientRowsByRequest, -1);
        Array.Clear(_pointDynamicSkinnedRequests);
        _pointTransientDemandThisFrame = 0;
        _pointTransientRowsRenderedThisFrame = 0;
        _pointDynamicSkinnedDrawCallsThisFrame = 0;
        _pointStaticTransientSkinnedDrawCallsThisFrame = 0;

        for (int i = 0; i < _pointRequests.Count; i++)
        {
            PointShadowRequest request = _pointRequests[i];
            if (request.Slot < 0 || !PointRequestTouchesSkinned(request)) continue;
            if (request.Mode == LightShadowMode.Static)
            {
                // A newly acquired base row becomes sampleable when its selected rigid rebuild finishes below.
                if (!cache.EverRendered(request.Slot) && !_pointRebuilds.Contains(i)) continue;
                _pointTransientCandidates.Add(new PointShadowTransientCandidate(
                    i, request.LightIndex, request.Key, request.DistanceSq));
            }
            else if (settings.MaxDynamicLightsPerFrame > 0
                && (cache.LastRenderedFrame(request.Slot) == frame || _pointRebuilds.Contains(i)))
            {
                _pointDynamicSkinnedRequests[i] = true;
            }
        }

        _pointTransientDemandThisFrame = _pointTransientCandidates.Count;
        RecordPointShadowTransientDemand(_pointTransientDemandThisFrame);
        Internal.PointShadowTransientRows.Assign(_pointTransientCandidates, PointShadowTransientRows,
            _pointTransientRowsByRequest.AsSpan(0, _pointRequests.Count), _selectedPointTransientRequests);
        _pointTransientRowsRenderedThisFrame = _selectedPointTransientRequests.Count;
    }

    void PrepareGpuSkinnedPointCasters(IGpuCommandList cl)
    {
        if (_pointShadows is not { } renderer || _gpuSkinnedDraws.Count == 0) return;
        renderer.EnsureSkinnedCasterCapacity((uint)_gpuSkinnedDraws.Count);
        foreach (GpuSkinnedDraw draw in _gpuSkinnedDraws)
        {
            if (draw.ShadowKind == ShadowCastKind.None) continue;
            float complement = draw.ShadowKind == ShadowCastKind.DissolvingInverted ? 1f : 0f;
            renderer.PackSkinnedCaster(draw.Slot, draw.World, draw.DissolveParams.X, complement);
        }
        renderer.UploadSkinnedCasters(cl);
    }

    int DrawPointSkinnedCastersForFace(IGpuCommandList cl, PointShadowRenderer renderer,
        in PackedPointShadowSlot packed, int face)
    {
        PointShadowRequest request = _pointRequests[packed.RequestIndex];
        int draws = 0;
        if (UseGpuSkinning)
        {
            foreach (GpuSkinnedDraw draw in _gpuSkinnedDraws)
            {
                if (draw.ShadowKind == ShadowCastKind.None
                    || !draw.PointSphere.TouchesShadowingShell(request.Position, request.Radius,
                        request.NearRadius, request.ExclusionMin, request.ExclusionMax)) continue;
                renderer.BeginGpuSkinnedFace(cl, packed.Atlas,
                    packed.PackedIndex * PointShadowMath.FaceCount + face, face, packed.Row, draw.ShadowKind);
                renderer.DrawGpuSkinnedCaster(cl, draw.RestVb, draw.Ib, draw.IndexCount, draw.IndexFormat,
                    draw.Slot, _model.SkinnedBonePaletteSet, SkinnedBonePalette.OffsetFor(draw.Slot));
                draws++;
            }
        }
        else if (_model.CpuSkinnedVertexBuffer is { } deformed
            && _model.CpuSkinnedInstanceBuffer is { } instances)
        {
            for (int i = 0; i < _cpuSkinnedDraws.Count; i++)
            {
                CpuSkinnedDraw draw = _cpuSkinnedDraws[i];
                if (draw.ShadowKind == ShadowCastKind.None
                    || !draw.PointSphere.TouchesShadowingShell(request.Position, request.Radius,
                        request.NearRadius, request.ExclusionMin, request.ExclusionMax)) continue;
                renderer.BeginFace(cl, packed.Atlas,
                    packed.PackedIndex * PointShadowMath.FaceCount + face, face, packed.Row, draw.ShadowKind);
                renderer.DrawCpuSkinnedCaster(cl, deformed, instances, draw.Ib, draw.IndexCount,
                    draw.IndexFormat, draw.BaseVertex, (uint)i);
                draws++;
            }
        }

        if (request.Mode == LightShadowMode.Dynamic)
            _pointDynamicSkinnedDrawCallsThisFrame += draws;
        else
            _pointStaticTransientSkinnedDrawCallsThisFrame += draws;
        return draws;
    }
}
