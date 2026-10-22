using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Render3D
{
    // Public synthetic geometry only. The tetrahedron is closed with outward-facing triangles.
    internal static class DqFixtureMeshes
    {
        internal sealed record Mesh(SkinnedVertex[] Vertices, int[] Indices);

        internal static Mesh ConstantWeightTube()
        {
            Vector3[] ring = [Vector3.UnitX, Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitY];
            var vertices = new SkinnedVertex[8];
            var indices = new int[24];
            for (int i = 0; i < 4; i++)
            {
                vertices[i] = Vertex(ring[i], new Vector4(.5f, .5f, 0, 0));
                vertices[i + 4] = Vertex(ring[i] + new Vector3(0, 0, 2), new Vector4(.5f, .5f, 0, 0));
                int next = (i + 1) % 4, offset = i * 6;
                indices[offset] = i; indices[offset + 1] = next; indices[offset + 2] = i + 4;
                indices[offset + 3] = next; indices[offset + 4] = next + 4; indices[offset + 5] = i + 4;
            }
            return new Mesh(vertices, indices);
        }

        internal static Mesh ClosedPatch() => new(
            [Vertex(Vector3.Zero, new Vector4(.5f, .5f, 0, 0)),
             Vertex(Vector3.UnitX, new Vector4(.5f, .5f, 0, 0)),
             Vertex(Vector3.UnitY, new Vector4(.5f, .5f, 0, 0)),
             Vertex(Vector3.UnitZ, new Vector4(.5f, .5f, 0, 0))],
            [0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3]);

        internal static Mesh VaryingWeightBend()
        {
            Mesh mesh = ClosedPatch();
            for (int i = 0; i < mesh.Vertices.Length; i++)
                mesh.Vertices[i].BoneWeights = i == 2 ? new Vector4(.5f, .5f, 0, 0) : Vector4.UnitX;
            return mesh;
        }

        internal static Mesh AmbiguousFourInfluenceMesh()
        {
            Mesh mesh = ClosedPatch();
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                mesh.Vertices[i].BoneIndices = new Vector4(0, 1, 2, 3);
                mesh.Vertices[i].BoneWeights = new Vector4(.25f);
            }
            return mesh;
        }

        internal static SkinnedVertex Vertex(Vector3 position, Vector4 weights) => new()
        {
            Position = position, Normal = Vector3.UnitX, Tangent = new Vector4(0, 1, 0, -1),
            Color = new Vector4(.2f, .4f, .6f, .8f), Uv = new Vector2(.25f, .75f),
            BoneIndices = new Vector4(0, 1, 0, 0), BoneWeights = weights,
        };
    }
}
