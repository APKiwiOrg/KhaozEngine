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
    /// <para>A Bepu <c>Mesh</c> is NOT recentered (unlike a convex hull or cylinder), so the chunk's vertices are
    /// used exactly as built and the STATIC'S POSE carries the placement. Since the chunk-local bake those vertices
    /// are chunk-local in X/Z (absolute in Y), so the pose is the chunk's region origin, not
    /// <see cref="Pose.Identity"/>: see <see cref="ChunkTerrainCollision"/>, which is the registration path that
    /// applies it. That is what makes terrain collision precise at range - Bepu transforms a query into the mesh's
    /// local space using the static's pose, so every triangle test runs at chunk magnitude (60 m by default) however
    /// far out the chunk sits, instead of on the 7.8 mm float32 lattice a 100 km vertex was baked onto. Verify by
    /// raycasting down against the surface in tests, per the gotcha.</para></summary>
    public static class TerrainChunkCollision
    {
        /// <summary>Build a static-collision triangle mesh for a meshed chunk. The vertices are the chunk's
        /// CHUNK-LOCAL surface positions (absolute Y) and the indices are its surface triangles (skirts excluded).
        /// Register it at the chunk's region origin (<c>Pose.At(new Vector3(region.OriginX, 0, region.OriginZ))</c>,
        /// which is what <see cref="ChunkTerrainCollision.Add"/> does), NOT at <see cref="Pose.Identity"/>: the
        /// vertices no longer carry their world position. Returns <c>null</c> when the chunk has no surface
        /// triangles (an empty chunk), so the caller can skip registration.</summary>
        public static TriangleMeshShape? Build(TerrainChunkMesh chunk)
        {
            if (chunk is null) throw new ArgumentNullException(nameof(chunk));
            return Build(chunk.Mesh, chunk.SurfaceVertexCount);
        }

        /// <summary>Build a static-collision triangle mesh from a raw chunk <see cref="GltfMesh"/> and the count of
        /// leading surface vertices. The vertices are taken verbatim, so they are in whatever space the mesh is in
        /// (chunk-local for a <see cref="TerrainChunkBuilder"/> chunk) and the caller's pose supplies the placement.
        /// <para>Skirts are the appended vertices at or beyond <paramref name="surfaceVertexCount"/>. A triangle
        /// whose three indices are all below the surface count is kept, and any triangle touching a skirt vertex
        /// is dropped. Returns <c>null</c> when no surface triangle survives.</para></summary>
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
                    // Reverse the winding (swap b and c). A Bepu Mesh is ONE-SIDED: it generates contacts and
                    // registers ray/sweep hits only from the FRONT of each triangle. Bepu's front face is the
                    // CLOCKWISE-wound side (its per-triangle front normal is cross(C-A, B-A) = -cross(B-A, C-A),
                    // the opposite of the usual right-handed CCW convention). TerrainChunkBuilder winds the surface
                    // CCW-from-above (its header is correct: cross(B-A, C-A) points UP, +Y), so Bepu treats the
                    // opposite side as the front and the terrain's front face points DOWN. A falling body and a
                    // downward ground probe would then pass straight through the top. Swapping b and c makes the
                    // Bepu front face point UP: a body rests ON the terrain and a downward raycast (the
                    // PhysicsGroundProbe path) hits the surface. Empirically verified: unflipped, a down-ray misses
                    // and an up-ray hits; flipped, the reverse. Buildings do NOT flip (PropCollisionBake.BakeTriangleMesh
                    // preserves winding) because a glTF building mesh is already wound CCW-outward, so Bepu's
                    // clockwise-front lands on the OUTWARD faces and it collides correctly with no reversal. The
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
