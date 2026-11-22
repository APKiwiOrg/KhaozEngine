using System;
using System.Numerics;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>Create a skinned mesh view with its own rest positions and indices. The view copies the source's
    /// other vertex attributes and shares its material sets, alpha cutoff, inverse binds, rest pose and skeleton.
    /// Its bounds grow with the source's and keep the largest vertex displacement as a margin on every side.
    /// <para>After validating the source and indices as for a plain view, refuses with
    /// <see cref="SkinnedMeshViewArgumentException"/> if <paramref name="restPositions"/> does not supply one finite
    /// position per source vertex. A refused call creates no GPU resources.</para>
    /// <para>The source cannot unload while the view lives. <see cref="UnloadSkinnedMesh"/> keeps the view drawable
    /// until the frame ends, then retires its vertex and index buffers.</para></summary>
    public SkinnedMeshHandle CreateSkinnedMeshView(
        SkinnedMeshHandle source, ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> restPositions)
    {
        SkinnedMeshEntry entry = ValidateSkinnedViewSource(source, indices);
        SkinnedGltfMesh mesh = entry.Source;
        int vertexCount = mesh.Vertices.Length;
        if (restPositions.Length != vertexCount)
            throw new SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal.PositionCountMismatch,
                "A displaced skinned mesh view needs one rest position per source vertex.", nameof(restPositions))
            {
                SourceIndex = source.Index, SourceGeneration = source.Generation,
                VertexCount = vertexCount, PositionCount = restPositions.Length,
            };

        var vertices = new SkinnedVertex[vertexCount];
        double maxDistanceSquared = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 position = restPositions[i];
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                throw new SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal.PositionNonFinite,
                    "A displaced skinned mesh view needs finite rest positions.", nameof(restPositions))
                { SourceIndex = source.Index, SourceGeneration = source.Generation, VertexIndex = i };

            SkinnedVertex vertex = mesh.Vertices[i];
            // Squaring a finite float displacement can overflow in float arithmetic.
            double dx = (double)position.X - vertex.Position.X;
            double dy = (double)position.Y - vertex.Position.Y;
            double dz = (double)position.Z - vertex.Position.Z;
            maxDistanceSquared = Math.Max(maxDistanceSquared, dx * dx + dy * dy + dz * dz);
            vertex.Position = position;
            vertices[i] = vertex;
        }

        double displacement = Math.Sqrt(maxDistanceSquared);
        MeshBounds bounds = BoundsWithSkinnedViewDisplacement(in entry.Bounds, displacement);
        var viewMesh = new SkinnedGltfMesh(vertices, indices.ToArray(), mesh.InverseBind, mesh.RestPose, mesh.Skeleton);
        IGpuBuffer vertexBuffer = UploadDisplacedViewBuffer(vertices, SkinnedVertex.SizeInBytes, GpuBufferUsage.VertexBuffer);
        IGpuBuffer? indexBuffer = null;
        try
        {
            indexBuffer = viewMesh.IndexFormat == GpuIndexFormat.UInt32
                ? UploadDisplacedViewBuffer(viewMesh.Indices32, sizeof(uint), GpuBufferUsage.IndexBuffer)
                : UploadDisplacedViewBuffer(MeshIndices.Narrow(viewMesh.Indices32), sizeof(ushort), GpuBufferUsage.IndexBuffer);
            return StoreSkinnedView(source, entry, viewMesh, vertexBuffer, indexBuffer, in bounds, displacement);
        }
        catch
        {
            _retired.Retire(vertexBuffer, indexBuffer, null);
            throw;
        }
    }

    IGpuBuffer UploadDisplacedViewBuffer<T>(T[] data, uint stride, GpuBufferUsage usage) where T : unmanaged
    {
        IGpuBuffer buffer = _gd.Factory.CreateBuffer(new GpuBufferDescription(checked((uint)data.Length * stride), usage));
        try
        {
            _gd.UpdateBuffer(buffer, 0, data);
            return buffer;
        }
        catch
        {
            // An upload can already be queued on the device, even though the view was never published.
            _retired.Retire(buffer);
            throw;
        }
    }

    static MeshBounds BoundsWithSkinnedViewDisplacement(in MeshBounds source, double? displacement)
    {
        if (displacement is not > 0) return source;
        double margin = displacement.Value;
        return new MeshBounds(
            new Vector3((float)(source.Min.X - margin), (float)(source.Min.Y - margin), (float)(source.Min.Z - margin)),
            new Vector3((float)(source.Max.X + margin), (float)(source.Max.Y + margin), (float)(source.Max.Z + margin)));
    }
}
