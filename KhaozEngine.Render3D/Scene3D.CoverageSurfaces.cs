using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly List<CoveragePulledDraw> _coverageDraws = [];
    readonly List<ModelRenderer.InstanceData> _coverageInstances = [];
    readonly List<IGpuResourceSet?> _coverageMaterials = [];

    void ValidateCoverageSurfaces()
    {
        foreach (var host in _coverageHosts)
        {
            var p = host.Request;
            if (!p.Published || CoverageHostsLive(host) && (p.Raw || CompleteCoverageEquipment(p))) continue;
            _coverageBatch!.CancelOutput(host); p.Published = false;
            p.Failure = new(CoverageFailureCode.IdentityMismatch, host.Topology.HostIdentity, 0, 0, 0, 0);
        }
    }

    void RecordCoverageSurfaces(IGpuCommandList commands, FrustumPlanes camera, bool shadows, int cascades, bool points)
    {
        if (!_coverageRegistered) return;
        foreach (var host in _coverageHosts)
        {
            var p = host.Request;
            if (!p.Published) continue;
            p.View = ActiveCamera;
            p.PreviousSlot = p.History.Corresponds(p, p.View, _coverageSerial) ? p.History.Committed.Slot : -1;
            p.Surface = _coverageBatch!.Surface(host, p.OutputSlot, p.PreviousSlot);
        }
        _coverageBatch!.Record(commands);
        foreach (var host in _coverageHosts)
        {
            var p = host.Request;
            if (!p.Published) continue;
            var sphere = host.Bounds.Sphere(p);
            bool visible = !FrustumCulling || camera.IntersectsSphere(sphere.Center, sphere.Radius);
            for (int slice = 0; slice < p.Draws.Length; slice++)
            {
                var submitted = p.Draws[slice];
                var outlined = p.Outlines[slice];
                if (submitted is null && outlined is null) continue;
                var draw = submitted ?? outlined!.Value;
                var entry = _skinnedMeshes[draw.Mesh.Index]!;
                var range = host.Topology.MaterialRanges[slice];
                bool main = submitted is not null && !draw.ShadowOnly && visible;
                bool shadow = false;
                if (shadows && submitted is not null && draw.CastsShadows)
                    for (int c = 0; c < cascades && !shadow; c++)
                        shadow = _shadowFrustums[c].IntersectsSphereExceptPlane(sphere.Center - _frameOrigin, sphere.Radius, 4);
                bool point = points && submitted is not null && draw.CastsShadows
                    && PointRequestCanRetainSkinned(sphere, Post.Quality.Shadows.PointShadows);
                if (main) _drawnSkinnedInstances++;
                else if (submitted is not null && !draw.ShadowOnly) _culledSkinnedInstances++;
                bool dissolve = draw.Dissolve > 0 || draw.DissolveComplement > 0;
                _coverageInstances.Add(new()
                {
                    Model = ToRender(draw.Model),
                    Tint = draw.Tint,
                    Emissive = dissolve ? draw.DissolveEdgeColor : draw.Material.Emissive,
                    SpecParams = new(draw.Material.Specular, draw.Material.Shininess, entry.AlphaCutoff, 0),
                    IsDynamic = 1,
                    Dissolve = dissolve ? new(draw.Dissolve, draw.DissolveEdgeWidth) : Vector2.Zero,
                    DissolveComplement = draw.DissolveComplement,
                });
                var kind = submitted is null || !draw.CastsShadows || !shadow && !point && !main ? ShadowCastKind.None
                    : ShadowDepthSelection.ClassifySkinnedCaster(new SkinnedSceneInstances.Instance(draw));
                _coverageDraws.Add(new(p.Surface, range.FirstIndex, range.IndexCount, (uint)_coverageDraws.Count,
                    host.Actor, sphere, main, kind));
                _coverageMaterials.Add(entry.SkinnedMaterialSet);
            }
        }
        PrepareCoverageDraws(commands, shadows);
        _coverageEncoded = true;
    }

    void DrawSkinnedGeometry(IGpuCommandList commands)
    {
        if (UseGpuSkinning) DrawGpuSkinnedMain(commands);
        else DrawCpuSkinnedMain(commands);
        DrawCoverageMain(commands);
    }

    void DrawCoverageMain(IGpuCommandList commands)
    {
        if (_coverageDraws.Count == 0) return;
        // Ordinary and coverage casters share the header buffer, but never a window in one recording.
        _model.SetCoverageMainSlotOffset(UseGpuSkinning ? (uint)_gpuSkinnedDraws.Count : 0);
        foreach (var draw in _coverageDraws) _model.PackCoverage(draw, _coverageInstances[(int)draw.InstanceSlot]);
        _model.UploadCoverage(commands);
        foreach (var draw in _coverageDraws)
        {
            if (!draw.VisibleMain) continue;
            if (_res.MotionAllocated) _model.DrawCoverageMotion(commands, draw,
                CoveragePreviousModel(draw.Actor, _coverageInstances[(int)draw.InstanceSlot].Model), _coverageMaterials[(int)draw.InstanceSlot]);
            else _model.DrawCoverage(commands, draw, _coverageMaterials[(int)draw.InstanceSlot]);
            CountSkinnedDraw(draw.CornerCount);
        }
    }

    // Immutable pass evidence is emitted only after the real draw command has been recorded.
    Action<string, CoverageDrawEvidence>? _coverageObservePass;
    internal Action<CoveragePassEvidence>? CoveragePassObserverForTests
    {
        set
        {
            _coverageObservePass = value is null ? null : (pass, draw) => ObserveCoveragePass(value, pass, draw);
            _model.CoveragePullObserverForTests = _coverageObservePass;
            _targetOutlines.CoveragePullObserverForTests = _coverageObservePass;
            if (_pointShadows is not null) _pointShadows.CoveragePullObserverForTests = _coverageObservePass;
        }
    }
    void ObserveCoveragePass(Action<CoveragePassEvidence> observe, string pass, CoverageDrawEvidence draw)
    {
        foreach (var host in _coverageHosts)
        {
            if (host.Actor != draw.Actor) continue;
            var p = host.Request;
            observe(new(pass, host.Actor, p.History.Token(p.OutputSlot),
                p.History.Token(p.PreviousSlot < 0 ? p.OutputSlot : p.PreviousSlot), draw.FirstCorner,
                draw.CornerCount, draw.VisibleMain));
            return;
        }
    }
}

internal readonly record struct CoveragePassEvidence(string Pass, MotionKey Actor, object Surface, object Previous,
    int FirstIndex, int IndexCount, bool VisibleMain);
