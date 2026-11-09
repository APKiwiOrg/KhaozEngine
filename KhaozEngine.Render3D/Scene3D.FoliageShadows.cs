using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    // Casting prefixes survive camera colour culling for independent shadow-pass selection.
    readonly List<FoliageDraw> _foliageShadowDraws = new();
    FoliageShadowRenderer? _foliageShadowRenderer;

    /// <summary>Actual displaced foliage cascade draws this frame, separate from rigid and skinned diagnostics.</summary>
    internal int FoliageShadowDrawCalls { get; private set; }

    bool FoliageDrawValid(in FoliageDraw draw) => !draw.Batch.IsDisposed
        && _slots.IsValid(draw.Patch.Mesh.Index, draw.Patch.Mesh.Generation)
        && _meshes[draw.Patch.Mesh.Index] is not null;

    void PrepareFoliageShadowDraws()
    {
        int kept = 0;
        for (int i = 0; i < _foliageShadowDraws.Count; i++)
        {
            FoliageDraw draw = _foliageShadowDraws[i];
            if (FoliageDrawValid(draw)) _foliageShadowDraws[kept++] = draw;
        }
        _foliageShadowDraws.RemoveRange(kept, _foliageShadowDraws.Count - kept);
    }

    void UploadFoliageShadowCascades(IGpuCommandList cl)
    {
        if (_foliageShadowDraws.Count == 0) return;
        _foliageShadowRenderer ??= new FoliageShadowRenderer(_gd, _model.ShadowMap, _model.FoliageLayout, _retired);
        bool ordinary = false, anchored = false;
        foreach (var draw in _foliageShadowDraws)
        {
            if (draw.Batch.AnchorSet is null) ordinary = true;
            else anchored = true;
        }
        _foliageShadowRenderer.UploadCascades(cl, _cascadeDepthVps.AsSpan(0, _cascadeCount),
            _currentFrameView.ViewProjection, _frameOrigin, ordinary, anchored ? _model.PreparedAnchorLayout : null);
    }

    void DrawFoliageShadowCascade(IGpuCommandList cl, int cascade)
    {
        using var pass = ProfilePass(cl, "shadow.cascade.foliage", cascade,
            executed: _foliageShadowDraws.Count > 0);
        FrustumPlanes frustum = _shadowFrustums[cascade];
        foreach (FoliageDraw draw in _foliageShadowDraws)
        {
            if (!FoliageDrawValid(draw) || draw.Batch.Buffer is null ||
                _captureShadowDomain is { } domain && !domain.ContainsCaster(draw.Bounds) ||
                !FoliageShadowSelection.IntersectsCascade(draw.Bounds, frustum, _frameOrigin)) continue;
            Mesh mesh = _meshes[draw.Patch.Mesh.Index]!.Value;
            if (draw.Batch.AnchorSet is { } anchor)
                _foliageShadowRenderer!.DrawPrepared(cl, cascade, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                    draw.Batch.Buffer, (uint)draw.Patch.Start, (uint)draw.Count, _model.FoliageSet, (uint)draw.UniformSlot,
                    mesh.CutsOutShadow ? mesh.ShadowCutoutSet : null, anchor, _model.PreparedAnchorLayout);
            else
                _foliageShadowRenderer!.Draw(cl, cascade, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                    draw.Batch.Buffer, (uint)draw.Patch.Start, (uint)draw.Count, _model.FoliageSet, (uint)draw.UniformSlot,
                    mesh.CutsOutShadow ? mesh.ShadowCutoutSet : null);
            FoliageShadowDrawCalls++;
            CountMeshDraw(mesh.IndexCount, (uint)draw.Count);
        }
        // The next cascade or skin pass starts from its normal layouts, never the foliage set numbering.
        if (_foliageShadowDraws.Count > 0) _model.BeginShadowCascadeRigid(cl, cascade);
    }
}
