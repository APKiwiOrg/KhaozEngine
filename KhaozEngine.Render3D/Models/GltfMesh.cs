using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Interleaved vertex: position, normal, base color (RGBA), texture UV. 48 bytes.</summary>
    public struct ModelVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Color;
        public Vector2 Uv;
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv) { Position = p; Normal = n; Color = c; Uv = uv; }
        public ModelVertex(Vector3 p, Vector3 n, Vector4 c) : this(p, n, c, Vector2.Zero) { } // back-compat
        public const uint SizeInBytes = 48; // 3*4 + 3*4 + 4*4 + 2*4
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
