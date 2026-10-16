using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static long s_coverageScene;
    readonly ulong _coverageIdentity = (ulong)Interlocked.Increment(ref s_coverageScene);
    readonly List<Matrix4x4> _coveragePointMatrices = new();
    ulong _coverageView;
    internal Action<bool>? CoverageAllocationObserverForTests { get; set; }

    void PrepareCoverageRendering(bool shadows)
    {
        CoverageAllocationObserverForTests?.Invoke(true);
        CoverageSphereEvaluationsForTests = 0;
        if (_coverageArena.Preparations.Count > 0 && _coverageArena.Closed)
            throw new InvalidOperationException("Coverage must be staged again for a new render view.");
        BeginCoverageMotion();
        if (shadows) SetShadowReceiverTail();
        _coverageView++;
        // Submissions are complete and the origin is latched, so one conservative sphere serves every consumer.
        foreach (CoveragePreparation p in _coverageArena.Preparations)
        {
            p.Sphere = ComputeCoverageSphere(p);
            p.SphereView = _coverageView;
        }
        PreparePointShadowSchedule();
        CoveredHostPreparationCount = 0;
        foreach (CoveragePreparation p in _coverageArena.Preparations)
        {
            p.View = _coverageView;
            p.Passes.Clear();
            if (p.Owner.Disposed || !CoverageHostLive(p))
            {
                // Late disposal or unload freezes the explicit fallback. Live slices keep their ordinary route.
                p.Outcome = new(CoveragePresentation.UncoveredFallback, new(p.Owner.Disposed
                    ? CoverageFailureCode.StaleFrame : CoverageFailureCode.IdentityMismatch, p.Owner.Topology.HostIdentity, 0, 0, 0, 0));
                continue;
            }
            bool complete = true;
            for (int i = 0; i < p.Owner.Meshes.Length; i++)
            {
                if (p.Draws[i] is { } draw) CaptureHostPasses(p, draw, false, shadows);
                if (p.Outlines[i] is { } outline) CaptureHostPasses(p, outline, true, shadows);
                if (p.Draws[i] is null && p.Outlines[i] is null) complete = false;
            }
            for (int c = 0; c < p.CommandCount; c++)
            {
                CoverageEquipmentStage equipment = p.Equipment[c];
                for (int i = 0; i < equipment.Count; i++)
                {
                    if (!_slots.IsValid(equipment.Draws[i].Mesh.Index, equipment.Draws[i].Mesh.Generation)) { complete = false; continue; }
                    if (!equipment.Submitted[i] && !equipment.Outlined[i]) { complete = false; continue; }
                    CaptureEquipmentPasses(p, equipment, p.Commands[c], i, shadows);
                }
            }
            CoverageFailure failure = new(CoverageFailureCode.IdentityMismatch, p.Owner.Topology.HostIdentity, 0, 0, 0, 0);
            var stamp = new CoverageRenderStamp(_coverageIdentity, p.Generation, p.View, p.Actor, p.Id);
            var context = new CoverageRenderContext(p.Owner.Topology, stamp, CurrentFrameView.RenderOrigin,
                CollectionsMarshal.AsSpan(p.Passes));
            if (complete && CoverageCertification.Validate(p.Owner.Topology, p.Before, p.Candidate, p.Model,
                p.Commands.AsSpan(0, p.CommandCount), context, p.Owner.Scratch, out p.Certificate, out failure))
            {
                p.Outcome = new(CoveragePresentation.Covered, default);
                CoveredHostPreparationCount++;
            }
            else p.Outcome = new(CoveragePresentation.UncoveredFallback, failure);

        }
        _coverageArena.Closed = true;
        CoverageAllocationObserverForTests?.Invoke(false);
    }

    void CaptureHostPasses(CoveragePreparation p, in SkinnedInstanceDraw draw, bool outline, bool shadows)
    {
        MeshBounds bounds = p.CandidateBounds;
        double attributes = p.CandidateAttributes;
        if (outline) AddPass(p, 0, 0, p.Model, bounds,
            GpuClip.Correct(CurrentFrameView.ViewProjection, _gd.Capabilities), Perturbation(p.Model, bounds, p.CandidateOutlineAttributes, 0));
        else
        {
            if (!draw.ShadowOnly) AddPass(p, 0, 0, p.Model, bounds,
                GpuClip.Correct(CurrentFrameView.JitteredViewProjection, _gd.Capabilities), 0);
            if (!draw.ShadowOnly && _res.MotionAllocated)
                AddPass(p, 0, 0, p.Model, bounds, CurrentFrameView.ViewProjection, 0);
            if (draw.CastsShadows)
            {
                double extra = DrawInputs(draw.Tint, draw.Material, draw.DissolveEdgeColor,
                    draw.Dissolve > 0 || draw.DissolveComplement > 0, true);
                double perturbation = Perturbation(p.Model, bounds, attributes, extra);
                if (shadows)
                    for (int c = 0; c < _cascadeCount; c++) AddPass(p, 0, 0, p.Model, bounds, _cascadeDepthVps[c], perturbation);
                foreach (var packed in _packedPointSlots)
                {
                    if ((packed.Casters & PointShadowCasterSet.Skinned) == 0
                        || !CoverageSphere(p).TouchesShadowingShell(packed.LightPosAbsolute, packed.Radius,
                            packed.NearRadius, packed.ExclusionMin, packed.ExclusionMax)) continue;
                    for (int face = 0; face < PointShadowMath.FaceCount; face++)
                        AddPass(p, 0, 0, p.Model, bounds, _coveragePointMatrices[packed.PackedIndex * 6 + face], perturbation);
                }
            }
        }
    }

    bool CoverageHostLive(CoveragePreparation p)
    {
        foreach (var handle in p.Owner.Meshes)
            if (!_skinnedSlots.IsValid(handle.Index, handle.Generation)) return false;
        return true;
    }

    void CaptureEquipmentPasses(CoveragePreparation p, CoverageEquipmentStage stage, in CoverageCommandPose command, int slice, bool shadows)
    {
        RigidInstanceDraw draw = stage.Draws[slice];
        CoverageUploadEvidence upload = _coverageUploads[(draw.Mesh.Index, draw.Mesh.Generation)];
        MeshBounds bounds = command.Support.RenderEvidence.Bounds;
        if (stage.Outlined[slice]) AddPass(p, command.Id, stage.Instance, draw.World, bounds,
            GpuClip.Correct(CurrentFrameView.ViewProjection, _gd.Capabilities), Perturbation(draw.World, bounds, upload.OutlineAttributeSum, 0));
        if (!stage.Submitted[slice]) return;
        if (!draw.ShadowOnly) AddPass(p, command.Id, stage.Instance, draw.World, bounds,
            GpuClip.Correct(CurrentFrameView.JitteredViewProjection, _gd.Capabilities), 0);
        if (!draw.ShadowOnly && _res.MotionAllocated)
            AddPass(p, command.Id, stage.Instance, draw.World, bounds, CurrentFrameView.ViewProjection, 0);
        if (!draw.CastsShadows) return;
        double perturbation = Perturbation(draw.World, bounds, upload.AttributeSum,
            DrawInputs(draw.Tint, draw.Material, draw.DissolveEdgeColor, draw.Dissolve > 0 || draw.DissolveComplement > 0, false));
        if (shadows)
            for (int c = 0; c < _cascadeCount; c++) AddPass(p, command.Id, stage.Instance, draw.World, bounds, _cascadeDepthVps[c], perturbation);
        foreach (var packed in _packedPointSlots)
        {
            if ((packed.Casters & PointShadowCasterSet.Rigid) == 0 || !InstanceTouchesLight(upload.Bounds, draw.World,
                packed.LightPosAbsolute, packed.Radius, packed.NearRadius, packed.ExclusionMin, packed.ExclusionMax)) continue;
            for (int face = 0; face < PointShadowMath.FaceCount; face++)
                AddPass(p, command.Id, stage.Instance, draw.World, bounds, _coveragePointMatrices[packed.PackedIndex * 6 + face], perturbation);
        }
    }

    void AddPass(CoveragePreparation p, uint command, ulong instance, Matrix4x4 world, MeshBounds bounds,
        Matrix4x4 vp, double perturbation) => p.Passes.Add(new(command, instance, ToRender(world), vp,
            bounds.Min, bounds.Max, perturbation));

    static double DrawInputs(KhaozEngine.Primitives.Color tint, Material material, KhaozEngine.Primitives.Color edge,
        bool dissolve, bool dynamic) => CoverageShaderPosition.Sum(Math.Abs((double)tint.R),
            CoverageShaderPosition.Sum(Math.Abs(dissolve ? (double)edge.R : material.Emissive.R),
                CoverageShaderPosition.Sum(Math.Abs((double)material.Specular), dynamic && dissolve ? 1 : 0)));

    double Perturbation(Matrix4x4 world, MeshBounds bounds, double attributes, double instance) =>
        CoverageShaderPosition.Bound(ToRender(world), bounds, attributes, instance);

    void ReserveCoverageFrame()
    {
        if (_coverageArena.Preparations.Count == 0) return;
        int vertices = 0, draws = _skinnedInstances.Items.Count;
        foreach (CoveragePreparation p in _coverageArena.Preparations)
        {
            vertices = checked(vertices + p.Owner.Topology.VertexCount);
            draws = checked(draws + p.Owner.Meshes.Length);
            int slices = p.Owner.Meshes.Length;
            for (int c = 0; c < p.CommandCount; c++) slices += p.Equipment[c].Count;
            p.Passes.EnsureCapacity(checked(slices * (8 + _lights.Count * 12)));
        }
        var queued = _skinnedInstances.Items;
        for (int i = 0; i < queued.Count; i++)
        {
            var it = queued[i];
            if (it.Coverage.Preparation is null && !UseGpuSkinning && _skinnedCpuVerts[it.Mesh.Index] is { } source)
                vertices = checked(vertices + source.Length);
        }
        _cpuSkinnedVerts.EnsureCapacity(vertices); _cpuSkinnedInstances.EnsureCapacity(draws); _cpuSkinnedDraws.EnsureCapacity(draws);
        _coveragePointMatrices.EnsureCapacity(checked(_lights.Count * 12));
        _gpuSkinnedDraws.EnsureCapacity(draws);
        // Public temporal requests may change after preparation. Reserve the bounded previous stream now,
        // without activating motion resources or their bindings before the target requests them.
        _cpuSkinnedPrevious.EnsureCapacity(vertices);
        _model.ReserveCoveredSkinning(vertices, draws, vertices);
        _model.EnsureSkinnedMainCapacity((uint)draws);
        _model.EnsureSkinnedBonePaletteCapacity((uint)draws);
        _model.EnsureSkinnedShadowCapacity((uint)draws * ShadowSettings.MaxCascades);
        _pointShadows?.EnsureSkinnedCasterCapacity((uint)draws);
        _pointShadows?.EnsureFaceCapacity(checked(_lights.Count * 2));
    }
}
