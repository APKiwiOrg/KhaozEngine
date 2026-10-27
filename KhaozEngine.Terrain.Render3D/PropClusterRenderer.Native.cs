using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public sealed partial class PropClusterRenderer
{
    readonly Dictionary<PropClusterKey, (NativePropClusterCpuBuild Build, MeshHandle Handle)> _native = new();

    /// <summary>Precondition: the upper scene/routing adapter validated every requested instance and effective
    /// roof role, frozen native inventory, bounded region cell and binary64 vertex precision before entry.
    /// This internal builder receives no building intent and makes no roof promise.</summary>
    internal static NativePropClusterCpuBuild BuildNativeCpu(NativePropClusterRequest request) => BuildNativeCpu(request, false);

    internal static NativePropClusterCpuBuild BuildNativeCpu(NativePropClusterRequest request, bool retainContributions)
        => BuildNativeCpu(request, retainContributions, PropHlod.SelectMergedContribution);

    internal static NativePropClusterCpuBuild BuildNativeCpu(NativePropClusterRequest request, bool retainContributions,
        Func<GltfMesh, int, int, float, GltfMesh> selectContribution)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectContribution);
        var sources = request.BorrowLayer.HlodSourceMeshes ?? throw new ArgumentException("Native cluster requires its source inventory.", nameof(request));
        int vertexCount = 0, indexCount = 0;
        foreach (var instance in request.Instances)
        {
            if (!sources.TryGetValue(instance.AssetId, out var source) || source.Vertices.Length == 0)
                throw new ArgumentException("Every represented instance requires source geometry.", nameof(request));
            vertexCount = checked(vertexCount + source.Vertices.Length); indexCount = checked(indexCount + source.Indices32.Length);
        }
        var vertices = new ModelVertex[vertexCount]; var indices = new uint[indexCount];
        float weldCell = request.BorrowLayer.HlodWeldCell;
        bool weld = weldCell > 0;
        int vertex = 0, index = 0;
        foreach (var instance in request.Instances)
        {
            var source = sources[instance.AssetId]; var pose = instance.LocalTransform;
            // Weld the same absolute binary32 positions as the released route. Storage anchors
            // must change neither cell membership nor the order and precision of centroid accumulation.
            var transform = new NativePropClusterTransform(instance, weld ? default : request.Anchor);
            pose.Translation = Vector3.Zero;
            if (!Matrix4x4.Invert(pose, out var inverse)) throw new ArgumentException("Native cluster requires invertible transforms.", nameof(request));
            var normal = Matrix4x4.Transpose(inverse);
            uint start = (uint)vertex;
            foreach (var v in source.Vertices)
            {
                var output = v;
                // Admission certified anchor-local source coordinates. A non-welded merge retains
                // that single local rounding. A welded merge rebases only its final world centroids.
                var position = transform.Position(v.Position);
                output.Position = new((float)position.X, (float)position.Y, (float)position.Z);
                output.Normal = Vector3.Normalize(Vector3.TransformNormal(v.Normal, normal)); output.Tangent = Vector4.Zero;
                vertices[vertex++] = output;
            }
            foreach (uint corner in source.Indices32)
            {
                if (corner >= source.Vertices.Length) throw new ArgumentException("Native source index exceeds its vertices.", nameof(request));
                indices[index++] = checked(start + corner);
            }
        }
        var mesh = new GltfMesh(vertices, indices);
        GltfMesh[]? contributions = null;
        if (retainContributions)
        {
            contributions = new GltfMesh[request.Instances.Count];
            int firstIndex = 0;
            for (int i = 0; i < contributions.Length; i++)
            {
                int count = sources[request.Instances[i].AssetId].Indices32.Length;
                contributions[i] = selectContribution(mesh, firstIndex, count, weldCell);
                if (weld) NativePropClusterWeld.Store(contributions[i], request.Anchor);
                firstIndex += count;
            }
        }
        if (weld)
        {
            mesh = PropHlod.Weld(mesh, weldCell);
            NativePropClusterWeld.Store(mesh, request.Anchor);
        }
        return new(request, mesh, contributions);
    }

    /// <summary>Retain publication metadata and an externally owned handle. This never uploads or frees it.</summary>
    public void ApplyNative(PropClusterKey key, NativePropClusterCpuBuild build, MeshHandle acceptedMesh)
    {
        ArgumentNullException.ThrowIfNull(build); ThrowIfDisposed();
        if (build.BorrowMesh.TriangleCount == 0) throw new ArgumentException("An empty native proxy cannot replace individual geometry.", nameof(build));
        if (key != build.Request.Key) throw new ArgumentException("Native build and publication keys differ.", nameof(key));
        lock (_sync)
        {
            if (_native.TryGetValue(key, out var current) && current.Build.Request.Generation > build.Request.Generation) return;
            _native[key] = (build, acceptedMesh);
        }
    }

    internal bool TryGetNativeEmission(PropClusterKey key, Vector3 localFocus, out PropClusterDrawState state, out RigidInstanceDraw merged)
    {
        ThrowIfDisposed();
        lock (_sync)
        {
            if (!_native.TryGetValue(key, out var current)) { state = default; merged = default; return false; }
            state = NativeDrawState(current.Build.Request, true, localFocus);
            merged = MergedDraw(current.Handle, current.Build.Request.BorrowLayer, state.MergedDissolve, state.MergedComplement);
            return true;
        }
    }

    internal static RigidInstanceDraw MergedDraw(MeshHandle handle, PropLayer layer, float dissolve, bool complement) =>
        new(handle, Matrix4x4.Identity)
        {
            Dissolve = dissolve,
            DissolveComplement = complement ? 1f : 0f,
            CastsShadows = layer.CastsShadows,
        };

    internal static PropClusterDrawState NativeDrawState(NativePropClusterRequest request, bool accepted, Vector3 localFocus)
    {
        if (!accepted) return new(true, 0, false, 0, false, request.Generation, -1) { HasIndividualGeometry = request.Instances.Count > 0 };
        var layer = request.BorrowLayer;
        float dx = (request.Area.MinX + request.Area.MaxX) * .5f - localFocus.X;
        float dz = (request.Area.MinZ + request.Area.MaxZ) * .5f - localFocus.Z;
        return ResolveCoverage(MathF.Sqrt(dx * dx + dz * dz), layer, request.Generation, request.Generation) with
        { HasIndividualGeometry = request.Instances.Count > 0 };
    }

    // Both retained legacy clusters and native typed recording consume this same coverage decision.
    static PropClusterDrawState ResolveCoverage(float distance, PropLayer layer, long individualGeneration, long mergedGeneration)
    {
        if (distance >= layer.DrawRadius) return new(false, 0, false, 0, false, individualGeneration, mergedGeneration);
        float t = PropHlod.CrossfadeAt(distance, layer.HlodDistance, layer.HlodCrossfadeWidth);
        bool individuals = PropHlod.DrawsHlodProps(t);
        float start = MathF.Max(0, layer.DrawRadius - layer.FadeBandWidth);
        if (layer.FadeBandWidth > 0 && distance >= start)
            return new(individuals, t, true, Math.Clamp((distance - start) / (layer.DrawRadius - start), 0, 1), false, individualGeneration, mergedGeneration);
        bool merged = PropHlod.DrawsHlodMerged(t);
        // Only dissolve while both halves are submitted. A lone representation stays solid.
        bool complement = individuals && merged;
        float handoffDissolve = complement ? t : 0f;
        return new(individuals, handoffDissolve, merged, handoffDissolve, complement, individualGeneration, mergedGeneration);
    }
}
