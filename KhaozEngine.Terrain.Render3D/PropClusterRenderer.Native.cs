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
    /// roof exclusion before entry. This internal builder receives no building intent and makes no roof promise.</summary>
    internal static NativePropClusterCpuBuild BuildNativeCpu(NativePropClusterRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sources = request.BorrowLayer.HlodSourceMeshes ?? throw new ArgumentException("Native cluster requires its source inventory.", nameof(request));
        int vertexCount = 0, indexCount = 0;
        foreach (var instance in request.Instances)
        {
            if (!sources.TryGetValue(instance.AssetId, out var source) || source.Vertices.Length == 0)
                throw new ArgumentException("Every represented instance requires source geometry.", nameof(request));
            vertexCount = checked(vertexCount + source.Vertices.Length); indexCount = checked(indexCount + source.Indices32.Length);
        }
        var vertices = new ModelVertex[vertexCount]; var indices = new uint[indexCount];
        int vertex = 0, index = 0;
        foreach (var instance in request.Instances)
        {
            var source = sources[instance.AssetId]; var pose = instance.LocalTransform;
            pose.M41 = (float)((double)instance.Anchor.X - request.Anchor.X + pose.M41);
            pose.M42 = (float)((double)instance.Anchor.Y - request.Anchor.Y + pose.M42);
            pose.M43 = (float)((double)instance.Anchor.Z - request.Anchor.Z + pose.M43);
            if (!Matrix4x4.Invert(pose, out var inverse)) throw new ArgumentException("Native cluster requires invertible transforms.", nameof(request));
            var normal = Matrix4x4.Transpose(inverse);
            uint start = (uint)vertex;
            foreach (var v in source.Vertices)
            {
                var output = v; output.Position = Vector3.Transform(v.Position, pose);
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
        if (request.BorrowLayer.HlodWeldCell > 0) mesh = PropHlod.Weld(mesh, request.BorrowLayer.HlodWeldCell);
        return new(request, mesh);
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

    internal static RigidInstanceDraw MergedDraw(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve)
    {
        bool decorated = dissolve > 0 || !layer.CastsShadows;
        return new(handle, Matrix4x4.Identity)
        {
            Dissolve = decorated ? dissolve : 0,
            CastsShadows = layer.CastsShadows,
            InvertShadowDissolve = decorated && invertShadowDissolve,
        };
    }

    internal static PropClusterDrawState NativeDrawState(NativePropClusterRequest request, bool accepted, Vector3 localFocus)
    {
        if (!accepted) return new(true, 0, false, 0, false, request.Generation, -1);
        var layer = request.BorrowLayer;
        float dx = (request.Area.MinX + request.Area.MaxX) * .5f - localFocus.X;
        float dz = (request.Area.MinZ + request.Area.MaxZ) * .5f - localFocus.Z;
        return ResolveCoverage(MathF.Sqrt(dx * dx + dz * dz), layer, request.Generation, request.Generation);
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
        return new(individuals, t, PropHlod.DrawsHlodMerged(t), 1 - t, true, individualGeneration, mergedGeneration);
    }
}
