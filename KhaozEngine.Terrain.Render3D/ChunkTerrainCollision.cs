using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Terrain
{
    /// <summary>Render-free helper mirroring <see cref="ChunkStatics"/>/<see cref="ChunkDynamics"/> for the TERRAIN
    /// SURFACE of a chunk: registers the chunk's collision mesh as one static <see cref="StaticHandle"/> in an
    /// <see cref="IPhysicsWorld"/> when the chunk streams in and removes it on stream-out. Extracted so the
    /// lifecycle is headless-testable without a GPU context.
    /// <para>Memory discipline: the Bepu backend's mesh takes ownership of a <c>BufferPool</c> triangle buffer;
    /// <see cref="IPhysicsWorld.RemoveStatic"/> disposes it (<c>RecursivelyRemoveAndDispose</c>), so a register on
    /// load paired with a remove on unload leaves the pool flat across thousands of streaming cycles. The churn
    /// test pins this against the real backend.</para></summary>
    internal static class ChunkTerrainCollision
    {
        /// <summary>Build the chunk's surface collision mesh and, if it has any surface triangles, add it as a
        /// static body at <see cref="Pose.Identity"/> (the mesh vertices already carry their world position - a
        /// Bepu mesh is not recentered). The handle is returned so the caller can record it for removal.
        /// <paramref name="handle"/> is set and the method returns true when a body was added; it returns false
        /// (and does not set a handle) for an empty chunk with no surface triangles.</summary>
        internal static bool Add(IPhysicsWorld physics, TerrainChunkMesh chunk, out StaticHandle handle)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (chunk is null) throw new ArgumentNullException(nameof(chunk));

            TriangleMeshShape? mesh = TerrainChunkCollision.Build(chunk);
            if (mesh is null) { handle = default; return false; }

            handle = physics.AddStatic(mesh, Pose.Identity);
            return true;
        }

        /// <summary>Remove a chunk's terrain collision body if one was added (a valid handle). Safe to call for a
        /// chunk that registered no terrain body (<paramref name="hasHandle"/> false).</summary>
        internal static void Remove(IPhysicsWorld physics, bool hasHandle, StaticHandle handle)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (hasHandle) physics.RemoveStatic(handle);
        }
    }
}
