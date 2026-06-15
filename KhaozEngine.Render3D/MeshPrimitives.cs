using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Builds simple <see cref="GltfMesh"/> primitives in code (no asset files), so games don't hand-roll
    /// them. Each face is a quad with its own outward normal (flat shading); vertex color is white
    /// (<see cref="Vector4.One"/>) since color now comes from the per-instance tint passed to
    /// <see cref="Scene3D.Draw(MeshHandle, Matrix4x4, Vector4)"/>.
    /// </summary>
    public static class MeshPrimitives
    {
        /// <summary>Axis-aligned cube centered at the origin: 6 quads = 24 vertices / 36 indices.</summary>
        public static GltfMesh Box(float size = 1f)
        {
            float h = size * 0.5f;
            return BuildBox(new Vector3(-h, -h, -h), new Vector3(h, h, h));
        }

        /// <summary>
        /// Flat box (footprint <paramref name="size"/>×<paramref name="size"/>, height
        /// <paramref name="thickness"/>) resting with its base at y=0, so it sits on the ground plane.
        /// 24 vertices / 36 indices.
        /// </summary>
        public static GltfMesh Tile(float size = 1f, float thickness = 0.1f)
        {
            float h = size * 0.5f;
            return BuildBox(new Vector3(-h, 0f, -h), new Vector3(h, thickness, h));
        }

        /// <summary>Builds an axis-aligned box spanning [min, max] as 6 outward-facing quads.</summary>
        static GltfMesh BuildBox(Vector3 min, Vector3 max)
        {
            var white = Vector4.One;
            var vertices = new ModelVertex[24];
            var indices = new ushort[36];
            int vi = 0, ii = 0;

            void Face(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n)
            {
                ushort baseIdx = (ushort)vi;
                vertices[vi++] = new ModelVertex(a, n, white);
                vertices[vi++] = new ModelVertex(b, n, white);
                vertices[vi++] = new ModelVertex(c, n, white);
                vertices[vi++] = new ModelVertex(d, n, white);
                indices[ii++] = baseIdx;
                indices[ii++] = (ushort)(baseIdx + 1);
                indices[ii++] = (ushort)(baseIdx + 2);
                indices[ii++] = baseIdx;
                indices[ii++] = (ushort)(baseIdx + 2);
                indices[ii++] = (ushort)(baseIdx + 3);
            }

            // +X
            Face(new Vector3(max.X, min.Y, max.Z), new Vector3(max.X, min.Y, min.Z),
                 new Vector3(max.X, max.Y, min.Z), new Vector3(max.X, max.Y, max.Z), Vector3.UnitX);
            // -X
            Face(new Vector3(min.X, min.Y, min.Z), new Vector3(min.X, min.Y, max.Z),
                 new Vector3(min.X, max.Y, max.Z), new Vector3(min.X, max.Y, min.Z), -Vector3.UnitX);
            // +Y
            Face(new Vector3(min.X, max.Y, max.Z), new Vector3(max.X, max.Y, max.Z),
                 new Vector3(max.X, max.Y, min.Z), new Vector3(min.X, max.Y, min.Z), Vector3.UnitY);
            // -Y
            Face(new Vector3(min.X, min.Y, min.Z), new Vector3(max.X, min.Y, min.Z),
                 new Vector3(max.X, min.Y, max.Z), new Vector3(min.X, min.Y, max.Z), -Vector3.UnitY);
            // +Z
            Face(new Vector3(min.X, min.Y, max.Z), new Vector3(max.X, min.Y, max.Z),
                 new Vector3(max.X, max.Y, max.Z), new Vector3(min.X, max.Y, max.Z), Vector3.UnitZ);
            // -Z
            Face(new Vector3(max.X, min.Y, min.Z), new Vector3(min.X, min.Y, min.Z),
                 new Vector3(min.X, max.Y, min.Z), new Vector3(max.X, max.Y, min.Z), -Vector3.UnitZ);

            return new GltfMesh(vertices, indices);
        }
    }
}
