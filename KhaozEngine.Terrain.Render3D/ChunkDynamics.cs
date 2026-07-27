using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;

namespace KhaozEngine.Terrain
{
    /// <summary>One dynamic body to spawn for a chunk: a shape, its initial world pose, and its mass/motion
    /// knobs. The engine owns the mechanism (register on chunk load, remove on unload); the GAME decides what
    /// spawns where by supplying an <see cref="IChunkDynamicsSource"/> that emits these specs per chunk.</summary>
    public readonly record struct DynamicSpawn(PhysicsShape Shape, Pose Pose, DynamicBodyDescription Body, PhysicsMaterial? Material = null);

    /// <summary>Game-supplied policy: yields the dynamic bodies to spawn for a given chunk. Kept out of the
    /// engine so "what spawns where" (loot drops, physics props, debris) stays game code; the engine only
    /// registers/removes what this source returns as chunks stream in and out.</summary>
    public interface IChunkDynamicsSource
    {
        /// <summary>The dynamic bodies to spawn for the chunk at <paramref name="coord"/>. Deterministic and
        /// pure (no wall clock) so a re-loaded chunk spawns the same bodies. Return an empty list for a chunk
        /// with no dynamics.</summary>
        IReadOnlyList<DynamicSpawn> SpawnsFor(ChunkCoord coord);
    }

    /// <summary>Render-free helper mirroring <see cref="ChunkStatics"/> for DYNAMIC bodies: adds/removes per-chunk
    /// <see cref="DynamicBodyHandle"/>s in an <see cref="IPhysicsWorld"/> as a chunk loads/unloads. Extracted so
    /// the lifecycle is headless-testable without a GPU context. Mechanism only: it registers exactly the spawns
    /// it is handed and removes exactly the handles it recorded.</summary>
    internal static class ChunkDynamics
    {
        /// <summary>Add every spawn in <paramref name="spawns"/> to <paramref name="physics"/> and append the
        /// resulting handle to <paramref name="handles"/>. A spawn pose is ABSOLUTE world coordinates (the game
        /// authors it), so it is reduced by <see cref="IPhysicsWorld.Origin"/> on the way in - streaming continues
        /// after a rebase, and an unreduced spawn would land one anchor delta from everything else.</summary>
        internal static void AddAll(
            IPhysicsWorld physics,
            IReadOnlyList<DynamicSpawn> spawns,
            List<DynamicBodyHandle> handles)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (spawns is null) throw new ArgumentNullException(nameof(spawns));
            if (handles is null) throw new ArgumentNullException(nameof(handles));

            Vector3 origin = physics.Origin;
            for (int i = 0; i < spawns.Count; i++)
            {
                DynamicSpawn s = spawns[i];
                var pose = new Pose(s.Pose.Position - origin, s.Pose.Orientation);
                handles.Add(physics.AddDynamic(s.Shape, pose, s.Body, s.Material));
            }
        }

        /// <summary>Remove every handle in <paramref name="handles"/> from <paramref name="physics"/> and clear
        /// the list. Safe to call for a chunk that spawned no dynamics (empty list is a no-op).</summary>
        internal static void RemoveAll(IPhysicsWorld physics, List<DynamicBodyHandle> handles)
        {
            if (physics is null) throw new ArgumentNullException(nameof(physics));
            if (handles is null) throw new ArgumentNullException(nameof(handles));

            for (int i = 0; i < handles.Count; i++)
                physics.RemoveDynamic(handles[i]);
            handles.Clear();
        }
    }
}
