using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Physics;

/// <summary>Headless tests for the mechanism-only per-chunk dynamic-body lifecycle: an
/// <see cref="IChunkDynamicsSource"/> yields <see cref="DynamicSpawn"/>s per chunk, which the physics world
/// registers on load and removes on unload. Exercised against the real <see cref="BepuPhysicsWorld"/> so the
/// add/remove churn is verified against a live backend (no leak/throw across many chunk cycles).</summary>
public class ChunkDynamicsTests
{
    // A source that spawns N falling boxes for every chunk, positioned by the chunk coordinate so the test can
    // tell which chunk's bodies are live.
    sealed class BoxSpawnSource : IChunkDynamicsSource
    {
        readonly int _perChunk;
        public BoxSpawnSource(int perChunk) => _perChunk = perChunk;

        public IReadOnlyList<DynamicSpawn> SpawnsFor(ChunkCoord coord)
        {
            var list = new List<DynamicSpawn>(_perChunk);
            for (int i = 0; i < _perChunk; i++)
            {
                var pose = Pose.At(new Vector3(coord.X * 10f + i, 5f, coord.Z * 10f));
                list.Add(new DynamicSpawn(new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)), pose, DynamicBodyDescription.WithMass(1f)));
            }
            return list;
        }
    }

    [Fact]
    public void ChunkDynamics_AddAll_RegistersEverySpawn_RemoveAll_RemovesThem()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var source = new BoxSpawnSource(perChunk: 3);
        var handles = new List<DynamicBodyHandle>();

        IReadOnlyList<DynamicSpawn> spawns = source.SpawnsFor(new ChunkCoord(0, 0));
        ChunkDynamics.AddAll(world, spawns, handles);
        Assert.Equal(3, handles.Count);
        // Every handle is a live body: pose queryable, and it falls when stepped.
        var startY = new float[handles.Count];
        for (int i = 0; i < handles.Count; i++) startY[i] = world.GetDynamicPose(handles[i]).Position.Y;
        for (int s = 0; s < 30; s++) world.Step(1f / 60f);
        for (int i = 0; i < handles.Count; i++)
            Assert.True(world.GetDynamicPose(handles[i]).Position.Y < startY[i], "each spawned body must fall");

        var snapshot = new List<DynamicBodyHandle>(handles);
        ChunkDynamics.RemoveAll(world, handles);
        Assert.Empty(handles);
        foreach (var h in snapshot)
            Assert.Throws<ArgumentException>(() => world.GetDynamicPose(h)); // removed => no longer live
    }

    [Fact]
    public void ChunkDynamics_ChurnManyLoadUnloadCycles_NoThrow_HandlesReused()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var source = new BoxSpawnSource(perChunk: 4);

        // Register + step + unregister a chunk's dynamics repeatedly. This mirrors chunk streaming churn and
        // exercises the shape-pool discipline on RemoveDynamic (RecursivelyRemoveAndDispose) across many cycles.
        for (int cycle = 0; cycle < 40; cycle++)
        {
            var coord = new ChunkCoord(cycle % 5, cycle % 3);
            var handles = new List<DynamicBodyHandle>();
            ChunkDynamics.AddAll(world, source.SpawnsFor(coord), handles);
            Assert.Equal(4, handles.Count);
            for (int s = 0; s < 5; s++) world.Step(1f / 60f); // let them move a little
            ChunkDynamics.RemoveAll(world, handles);
            Assert.Empty(handles);
        }

        // The world is empty after the last remove: a ray down through the spawn column hits nothing.
        Assert.False(world.Raycast(new Vector3(0f, 10f, 0f), -Vector3.UnitY, 100f, out _),
            "no dynamic bodies should remain after all chunk cycles unloaded");
    }

    [Fact]
    public void ChunkDynamics_EmptySpawnList_IsNoOp()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var handles = new List<DynamicBodyHandle>();
        ChunkDynamics.AddAll(world, Array.Empty<DynamicSpawn>(), handles);
        Assert.Empty(handles);
        ChunkDynamics.RemoveAll(world, handles); // must not throw
        Assert.Empty(handles);
    }
}
