using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Turns a meshed terrain chunk into a static-collision <see cref="TriangleMeshShape"/> so the
    /// streamed terrain surface becomes physics geometry alongside props and buildings (one unified query path
    /// through <see cref="IPhysicsWorld"/>), replacing the analytic <see cref="TerrainCollision"/> ground-follow
    /// delegate for games that opt in. Render-free (works off the CPU-built <see cref="TerrainChunkMesh"/>, no GPU
    /// device), so it is headless-testable.
    /// <para>Only the SURFACE triangles are used, not the downward edge skirts: the skirts are a rendering
    /// crack-hider (a thin vertical curtain that faces inward/down), so including them would add spurious inward
    /// collision faces at chunk seams. <see cref="TerrainChunkMesh.SurfaceVertexCount"/> is the count of leading
    /// grid vertices before the appended skirt vertices, so a triangle is a surface triangle exactly when all
    /// three of its indices are below that count.</para>
    /// <para>A Bepu <c>Mesh</c> is NOT recentered (unlike a convex hull or cylinder), so the world-space chunk
    /// vertices are used directly with an identity pose: the collision surface lines up with the visual surface
    /// with no offset. Verify by raycasting down against the surface in tests, per the gotcha.</para></summary>
    public static class TerrainChunkCollision
    {
        /// <summary>Build a static-collision triangle mesh for a meshed chunk. The vertices are the chunk's
        /// world-space surface positions and the indices are its surface triangles (skirts excluded). The mesh is
        /// placed at the world origin with identity orientation, so pass <see cref="Pose.Identity"/> (or the
        /// default) when registering it: the vertices already carry their world position. Returns <c>null</c> when
        /// the chunk has no surface triangles (an empty chunk), so the caller can skip registration.</summary>
        public static TriangleMeshShape? Build(TerrainChunkMesh chunk)
        {
            if (chunk is null) throw new ArgumentNullException(nameof(chunk));
            return Build(chunk.Mesh, chunk.SurfaceVertexCount);
        }

        /// <summary>Build a static-collision triangle mesh from a raw chunk <see cref="GltfMesh"/> and the count of
        /// leading surface vertices (skirts are the appended vertices at or beyond
        /// <paramref name="surfaceVertexCount"/>). A triangle whose three indices are all below the surface count
        /// is kept; any triangle touching a skirt vertex is dropped. Returns <c>null</c> when no surface triangle
        /// survives.</summary>
        public static TriangleMeshShape? Build(GltfMesh mesh, int surfaceVertexCount)
        {
            if (mesh is null) throw new ArgumentNullException(nameof(mesh));
            if (surfaceVertexCount < 0) throw new ArgumentOutOfRangeException(nameof(surfaceVertexCount));

            ModelVertex[] verts = mesh.Vertices;
            uint[] indices = mesh.Indices32;

            // Surface vertices only: a Bepu mesh copies each triangle's three vertex positions into its own
            // Triangle buffer, so we hand it the exact surface positions and a compacted index list. We keep the
            // full surface-vertex slice (0..surfaceVertexCount) as the vertex array and filter the index list to
            // surface-only triangles; unreferenced vertices are harmless (Bepu reads by index).
            int surfaceCount = Math.Min(surfaceVertexCount, verts.Length);
            var surfaceVerts = new Vector3[surfaceCount];
            for (int i = 0; i < surfaceCount; i++) surfaceVerts[i] = verts[i].Position;

            // Count surviving surface triangles first so we can size the index array exactly (no List growth).
            int surviving = 0;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                if (indices[t] < surfaceCount && indices[t + 1] < surfaceCount && indices[t + 2] < surfaceCount)
                    surviving++;
            }
            if (surviving == 0) return null;

            var surfaceIndices = new int[surviving * 3];
            int w = 0;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a < surfaceCount && b < surfaceCount && c < surfaceCount)
                {
                    // Reverse the winding (swap b and c). A Bepu Mesh is ONE-SIDED: it generates contacts (and
                    // registers ray/sweep hits) only from the FRONT of each triangle, the side its winding-derived
                    // normal points to. The render mesh (TerrainChunkBuilder) winds the surface so its normal, as
                    // Bepu reads it, points DOWN, so a falling body and a downward ground probe would pass straight
                    // through the top. Flipping the winding here makes the collidable face point UP: a body rests
                    // ON the terrain and a downward raycast (the PhysicsGroundProbe path) hits the surface. The
                    // render mesh is untouched (it lights off its own per-vertex normals, not the face winding).
                    surfaceIndices[w++] = (int)a;
                    surfaceIndices[w++] = (int)c;
                    surfaceIndices[w++] = (int)b;
                }
            }

            return new TriangleMeshShape(surfaceVerts, surfaceIndices);
        }
    }
}
