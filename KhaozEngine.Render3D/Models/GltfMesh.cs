using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved vertex: position, flat normal, base color (RGBA). 40 bytes.</summary>
    public struct ModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c) { Position = p; Normal = n; Color = c; }
        public const uint SizeInBytes = 40; // 3*4 + 3*4 + 4*4
    }

    /// <summary>CPU-side loaded mesh. GPU buffers are created internally by the renderer.</summary>
    public sealed class GltfMesh
    {
        public ModelVertex[] Vertices { get; }
        public ushort[] Indices { get; }
        public GltfMesh(ModelVertex[] v, ushort[] i) { Vertices = v; Indices = i; }
        public int TriangleCount => Indices.Length / 3;
    }
}
