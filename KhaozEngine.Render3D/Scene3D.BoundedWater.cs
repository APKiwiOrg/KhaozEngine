using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly List<WaterSurfaceMesh> _boundedWater = new();
    readonly List<BoundedWaterDraw> _boundedWaterDraws = new();
    readonly List<WaterPlane> _waterDemand = new();
    internal int BoundedWaterCount => _boundedWater.Count;

    /// <summary>Upload a bounded mesh and its original domain once. Residency upload producers call this only
    /// after claiming their aggregate reservation. Frame submissions use the returned handle without uploads.</summary>
    public MeshHandle LoadWaterMesh(GltfMesh mesh, ReadOnlyMemory<byte> clipData)
    {
        WaterSurfaceDomain.Validate(clipData.Span);
        var handle = LoadMesh(mesh);
        try { LoadWaterDomain(handle, clipData); return handle; }
        catch { UnloadMesh(handle); throw; }
    }
    /// <summary>Attach the immutable domain to a mesh already uploaded by a resource producer.</summary>
    public void LoadWaterDomain(MeshHandle mesh, ReadOnlyMemory<byte> clipData)
    {
        if (!_slots.IsValid(mesh.Index, mesh.Generation)) throw new ArgumentException("Water requires a live mesh handle.", nameof(mesh));
        if (_meshes[mesh.Index] is not { } value || value.Bounds.Min.Y != value.Bounds.Max.Y)
            throw new ArgumentException("Water requires a planar mesh.", nameof(mesh));
        _water.RegisterBoundedDomain(mesh, clipData.Span);
    }
    public void DrawWater(in WaterSurfaceMesh surface)
    {
        ArgumentNullException.ThrowIfNull(surface.Look);
        if (!_slots.IsValid(surface.Mesh.Index, surface.Mesh.Generation) || !_water.HasBoundedDomain(surface.Mesh))
            throw new ArgumentException("Water requires a published mesh and its original domain.", nameof(surface));
        ValidateWaterTransform(surface.LocalTransform);
        _boundedWater.Add(surface);
    }
    internal static void ValidateWaterTransform(Matrix4x4 transform)
    {
        // The water plane stays horizontal. Its surface is authored, never inferred from the observer.
        if (!float.IsFinite(transform.M11) || !float.IsFinite(transform.M13) || !float.IsFinite(transform.M22) ||
            !float.IsFinite(transform.M31) || !float.IsFinite(transform.M33) || !float.IsFinite(transform.M41) ||
            !float.IsFinite(transform.M42) || !float.IsFinite(transform.M43) || transform.M12 != 0 || transform.M14 != 0 ||
            transform.M21 != 0 || transform.M23 != 0 || transform.M24 != 0 || transform.M32 != 0 || transform.M34 != 0 ||
            transform.M44 != 1 || !Matrix4x4.Invert(transform, out _))
            throw new ArgumentException("Water requires a finite invertible horizontal affine transform.", nameof(transform));
    }
    void ClearWaterQueues() { _waterPlanes.Clear(); _boundedWater.Clear(); _boundedWaterDraws.Clear(); _waterDemand.Clear(); }
    ReadOnlySpan<WaterPlane> WaterDemandPlanes()
    {
        var legacy = RelativeWaterPlanes();
        if (_boundedWater.Count == 0) return legacy;
        _waterDemand.Clear();
        foreach (var plane in legacy) _waterDemand.Add(plane);
        foreach (var surface in _boundedWater) _waterDemand.Add(new(0, 0, 0, 0, look: surface.Look));
        return CollectionsMarshal.AsSpan(_waterDemand);
    }
    void ForgetWaterAndInteriorRelevance(MeshHandle mesh) { _water.ForgetBoundedDomain(mesh); _interiorRelevance.Forget(mesh); }
    void RenderWater(IGpuCommandList cl, Matrix4x4 vp, Matrix4x4 displayVp, Vector3 eye, ref float waterSyncMs)
    {
        if (_waterPlanes.Count == 0 && _boundedWater.Count == 0) return;
        _boundedWaterDraws.Clear();
        foreach (var surface in _boundedWater)
        {
            if (!_slots.IsValid(surface.Mesh.Index, surface.Mesh.Generation) || !_water.HasBoundedDomain(surface.Mesh))
                throw new InvalidOperationException("A queued water resource was released before recording.");
            var mesh = _meshes[surface.Mesh.Index]!.Value;
            _boundedWaterDraws.Add(new(surface, mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, mesh.Bounds.Min.Y));
        }
        _water.Draw(cl, _res, RelativeWaterPlanes(), vp, displayVp, Post.LightDirection, Post.LightColor, eye,
            Post.Water, Post.Sky, EffectTimeSeconds, _frameOrigin, Post.Atmosphere.Enabled, CollectionsMarshal.AsSpan(_boundedWaterDraws),
            Internal.WaterColumnPacking.NoisePhase(_currentFrameView.FrameIndex, _resolveThisRender));
        _frameStats.DrawCalls += (_waterPlanes.Count > 0 ? 1 : 0) + _boundedWater.Count;
        if (EnableTiming) waterSyncMs = (float)_water.LastOceanCost.StallMs;
    }
}
