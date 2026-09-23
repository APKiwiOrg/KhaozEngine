using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers the generation-config refresh (issue #771): <see cref="Scene3DChunkSink.UpdateLayers"/> re-runs
    /// the construction rules, <see cref="Scene3DChunkSink.KeepsLayerShape"/> admits only a generation-config swap,
    /// <see cref="Scene3DChunkSink.RefreshProps"/> re-serves every layer of a loaded chunk exactly as a fresh build
    /// computes it without touching its terrain, and <see cref="TerrainStreamer.RefreshProps"/> routes the loaded
    /// chunks of a rect to that seam. The sink rows run with a null <see cref="Scene3D"/>, so any terrain upload or
    /// unload would throw: passing is itself the proof that no terrain work happened.</summary>
    public class ChunkPropRefreshTests
    {
        const float Chunk = 60f;
        static readonly ChunkCoord Origin = new(0, 0);

        // One shared mesh set: layers compare their mesh sets by reference, so a fresh dictionary per layer would make
        // every swap look like a draw-setting change to KeepsLayerShape.
        static readonly IReadOnlyDictionary<string, MeshHandle> NoMeshes = new Dictionary<string, MeshHandle>();

        sealed class FixedSource(params PropPlacement[] placements) : IPlacementSource
        {
            public void PlacementsIn(RectArea area, List<PropPlacement> into)
            {
                foreach (PropPlacement p in placements)
                    if (p.X >= area.MinX && p.X < area.MaxX && p.Z >= area.MinZ && p.Z < area.MaxZ) into.Add(p);
            }
        }

        static TerrainField Flat(float height) => new(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity, End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f,
                },
            },
        });

        // One pine on every 10 m cell, optionally masked by exclusions, so the counts are exact.
        static ScatterConfig Trees(params IArea2D[] exclusions) => new()
        {
            Seed = 3,
            CellSize = 10f,
            Jitter = 0f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind("pine", 1f) } },
            },
            Exclusions = exclusions,
        };

        static CompanionConfig Bushes(int count) => new()
        {
            Seed = 7,
            Kinds = new[] { new PropKind("bush", 1f) },
            CountMin = count,
            CountMax = count,
        };

        static PropPlacement Rock(float x, float z) => new("rock", x, 5f, z, 1f, 0f, 0);

        // A loaded gameplay chunk built without a device: the sink's own per-chunk placements plus a terrain mesh
        // handle that only a terrain rebuild could replace.
        static Scene3DChunkSink.ChunkLoad LoadedGameplay(Scene3DChunkSink sink) => new()
        {
            Mesh = new MeshHandle(7, 3),
            LayerProps = sink.ScatterLayersFor(Origin),
            Lod = 0,
            Region = ChunkGrid.RegionOf(Origin, Chunk),
            Ring = ChunkRing.Gameplay,
        };

        static PropLayer[] Layers(ScatterConfig trees, CompanionConfig bushes, IPlacementSource live,
            IReadOnlyList<PropPlacement> frozen) => new[]
        {
            PropLayer.ScatterLayer(trees, NoMeshes, 90f).WithIdentity("trees"),
            PropLayer.CompanionLayer(0, bushes, NoMeshes, 60f).WithIdentity("trees"),
            PropLayer.PlacementLayer(frozen, NoMeshes, 90f),
            PropLayer.PlacementLayer(live, NoMeshes, 90f, colliders: false),
        };

        [Fact]
        public void RefreshProps_ReservesEveryLayerAsAFreshBuild_AndLeavesTheTerrainAlone()
        {
            var live = new FixedSource(Rock(12f, 12f), Rock(48f, 40f));
            PropPlacement[] frozen = { Rock(30f, 30f) };
            var sink = new Scene3DChunkSink(null!, Flat(5f), Layers(Trees(), Bushes(1), live, frozen), Chunk);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);
            Assert.Equal(36, load.LayerProps[0].Count);   // a 6 x 6 grid of pines, no exclusion yet
            Assert.Equal(36, load.LayerProps[1].Count);

            // The drag frame's config swap: an exclusion appears over part of the chunk and the understory doubles.
            PropLayer[] next = Layers(Trees(new DiscArea2D(25f, 25f, 12f)), Bushes(2), live, frozen);
            Assert.True(sink.KeepsLayerShape(next));
            sink.UpdateLayers(next);
            sink.RefreshProps(Origin, load);

            IReadOnlyList<PropPlacement>[] fresh = sink.ScatterLayersFor(Origin);
            Assert.Equal(fresh.Length, load.LayerProps.Length);
            for (int i = 0; i < fresh.Length; i++) Assert.Equal(fresh[i], load.LayerProps[i]);
            Assert.True(load.LayerProps[0].Count < 36, "the exclusion removed no pine, so the test proves nothing");
            Assert.Equal(2 * load.LayerProps[0].Count, load.LayerProps[1].Count);
            Assert.Equal(frozen, load.LayerProps[2]);
            Assert.Equal(2, load.LayerProps[3].Count);
            Assert.Equal((7, 3), (load.Mesh.Index, load.Mesh.Generation));
            Assert.False(load.HasTerrainCollider);
        }

        [Fact]
        public void RefreshProps_RebuildsPropStaticsFromTheRefreshedScatter()
        {
            var physics = new FakePhysicsWorld();
            var shapes = new Dictionary<string, PhysicsShape> { ["pine"] = new BoxShape(new Vector3(0.4f)) };
            var sink = new Scene3DChunkSink(null!, Flat(5f),
                new[] { PropLayer.ScatterLayer(Trees(), NoMeshes, 90f) }, Chunk, physics: physics,
                collisionShapes: shapes);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);
            sink.RefreshProps(Origin, load);
            Assert.Equal(36, load.Statics.Count);

            sink.UpdateLayers(new[] { PropLayer.ScatterLayer(Trees(new BoxArea2D(0f, 0f, 60f, 25f)), NoMeshes, 90f) });
            sink.RefreshProps(Origin, load);

            Assert.Equal(load.LayerProps[0].Count, load.Statics.Count);
            Assert.Equal(18, load.Statics.Count);      // the three rows at z 0, 10 and 20 are excluded
            Assert.Equal(36, physics.Removed.Count);
            Assert.Equal(36 + 18, physics.Added.Count);
        }

        [Fact]
        public void RefreshProps_OnADecorChunkWithoutHlod_IsANoOp()
        {
            var sink = new Scene3DChunkSink(null!, Flat(5f),
                new[] { PropLayer.ScatterLayer(Trees(), NoMeshes, 90f) }, Chunk);
            IReadOnlyList<PropPlacement>[] empty = { Array.Empty<PropPlacement>() };
            var load = new Scene3DChunkSink.ChunkLoad
            {
                Mesh = new MeshHandle(4, 1), LayerProps = empty, Ring = ChunkRing.Decor,
                Region = ChunkGrid.RegionOf(Origin, Chunk),
            };

            sink.RefreshProps(Origin, load);

            Assert.Same(empty, load.LayerProps);
            Assert.Equal((4, 1), (load.Mesh.Index, load.Mesh.Generation));
        }

        [Fact]
        public void KeepsLayerShape_AdmitsOnlyAGenerationConfigSwap()
        {
            var live = new FixedSource(Rock(12f, 12f));
            PropPlacement[] frozen = { Rock(30f, 30f) };
            var sink = new Scene3DChunkSink(null!, Flat(5f), Layers(Trees(), Bushes(1), live, frozen), Chunk);
            PropLayer[] Swap() => Layers(Trees(new DiscArea2D(0f, 0f, 5f)), Bushes(3), live, frozen);

            Assert.True(sink.KeepsLayerShape(Swap()));

            PropLayer[] fewer = Swap()[..3];
            Assert.False(sink.KeepsLayerShape(fewer));

            PropLayer[] radius = Swap();
            radius[0] = PropLayer.ScatterLayer(Trees(), NoMeshes, 120f).WithIdentity("trees");
            Assert.False(sink.KeepsLayerShape(radius));

            PropLayer[] identity = Swap();
            identity[0] = PropLayer.ScatterLayer(Trees(), NoMeshes, 90f).WithIdentity("rocks");
            Assert.False(sink.KeepsLayerShape(identity));

            PropLayer[] hlod = Swap();
            hlod[0] = hlod[0].WithHlod(new Dictionary<string, GltfMesh> { ["pine"] = MeshPrimitives.Box(1f) },
                hlodDistance: 50f, weldCell: 0f);
            Assert.False(sink.KeepsLayerShape(hlod));

            PropLayer[] otherSource = Swap();
            otherSource[3] = PropLayer.PlacementLayer(new FixedSource(Rock(12f, 12f)), NoMeshes, 90f, colliders: false);
            Assert.False(sink.KeepsLayerShape(otherSource));

            PropLayer[] kind = Swap();
            kind[0] = PropLayer.PlacementLayer(frozen, NoMeshes, 90f).WithIdentity("trees");
            Assert.False(sink.KeepsLayerShape(kind));
        }

        [Fact]
        public void UpdateLayers_AcceptsACompanionHostChange_ThatKeepsLayerShapeRefuses()
        {
            PropLayer[] Hosted(int host) => new[]
            {
                PropLayer.ScatterLayer(Trees(), NoMeshes, 90f),
                PropLayer.ScatterLayer(Trees(new BoxArea2D(0f, 0f, 60f, 25f)), NoMeshes, 90f),
                PropLayer.CompanionLayer(host, Bushes(1), NoMeshes, 60f),
            };
            var sink = new Scene3DChunkSink(null!, Flat(5f), Hosted(0), Chunk);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);
            Assert.Equal(36, load.LayerProps[2].Count);

            // A host swap is safe only when every loaded chunk rebuilds, so the partial-refresh test refuses it
            // while the all-chunks setter accepts it.
            Assert.False(sink.KeepsLayerShape(Hosted(1)));
            sink.UpdateLayers(Hosted(1));
            sink.RefreshProps(Origin, load);
            Assert.Equal(18, load.LayerProps[2].Count);
        }

        [Fact]
        public void UpdateLayers_RerunsTheConstructionRules()
        {
            var sink = new Scene3DChunkSink(null!, Flat(5f), new[]
            {
                PropLayer.ScatterLayer(Trees(), NoMeshes, 90f),
                PropLayer.CompanionLayer(0, Bushes(1), NoMeshes, 60f),
                PropLayer.CompanionLayer(0, Bushes(1), NoMeshes, 60f),
            }, Chunk);

            var outOfRange = Assert.Throws<ArgumentException>(() => sink.UpdateLayers(new[]
            {
                PropLayer.ScatterLayer(Trees(), NoMeshes, 90f),
                PropLayer.CompanionLayer(0, Bushes(1), NoMeshes, 60f),
                PropLayer.CompanionLayer(5, Bushes(1), NoMeshes, 60f),
            }));
            Assert.Contains("out of range", outOfRange.Message);

            var companionHost = Assert.Throws<ArgumentException>(() => sink.UpdateLayers(new[]
            {
                PropLayer.ScatterLayer(Trees(), NoMeshes, 90f),
                PropLayer.CompanionLayer(0, Bushes(1), NoMeshes, 60f),
                PropLayer.CompanionLayer(1, Bushes(1), NoMeshes, 60f),
            }));
            Assert.Contains("must be a scatter or placement layer", companionHost.Message);

            // A refused list leaves the sink serving the one it had.
            Assert.Equal(36, sink.ScatterLayersFor(Origin)[2].Count);
        }

        [Fact]
        public void Streamer_RefreshProps_RoutesTheRectsLoadedChunks_FallsBackToInvalidate_AndSkipsUnloaded()
        {
            var config = new StreamerConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerFrame: 16, ChunkSize: Chunk,
                Async: false);
            var props = new RecordingSink(propsOnly: true);
            using (var streamer = new TerrainStreamer(config, props))
            {
                streamer.PrimeAround(new Vector3(30f, 0f, 30f));
                // x -1..1, z 0..0: every coord in the rect is inside the primed ring.
                Assert.Equal(3, streamer.RefreshProps(new RectArea(-10f, 5f, 70f, 55f)));
                Assert.Equal(0, streamer.RefreshProps(new RectArea(3000f, 3000f, 3010f, 3010f)));
            }
            Assert.Equal(new[] { new ChunkCoord(-1, 0), new ChunkCoord(0, 0), new ChunkCoord(1, 0) },
                props.Refreshed.OrderBy(c => c.X).ToArray());
            Assert.Empty(props.ReLods);

            var plain = new RecordingSink(propsOnly: false);
            using (var streamer = new TerrainStreamer(config, plain.AsPlain()))
            {
                streamer.PrimeAround(new Vector3(30f, 0f, 30f));
                Assert.Equal(1, streamer.RefreshProps(new RectArea(5f, 5f, 55f, 55f)));
            }
            Assert.Equal(new[] { Origin }, plain.ReLods.ToArray());
        }

        sealed class RecordingSink : IChunkPropRefreshSink
        {
            readonly bool _propsOnly;
            public readonly Dictionary<ChunkCoord, object> Handles = new();
            public readonly List<ChunkCoord> Refreshed = new();
            public readonly List<ChunkCoord> ReLods = new();

            public RecordingSink(bool propsOnly) => _propsOnly = propsOnly;

            public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Handles[coord] = new object();
            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => ReLods.Add(coord);
            public void Unload(ChunkCoord coord, object handle) => Handles.Remove(coord);

            public void RefreshProps(ChunkCoord coord, object handle)
            {
                if (!_propsOnly) throw new InvalidOperationException("A plain sink has no props-only seam.");
                Assert.Same(Handles[coord], handle);
                Refreshed.Add(coord);
            }

            public IChunkSink AsPlain() => new Plain(this);

            sealed class Plain(RecordingSink inner) : IChunkSink
            {
                public object Load(ChunkCoord coord, int lod, ChunkRing ring) => inner.Load(coord, lod, ring);
                public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) =>
                    inner.ReLod(coord, handle, lod, ring);
                public void Unload(ChunkCoord coord, object handle) => inner.Unload(coord, handle);
            }
        }

        sealed class FakePhysicsWorld : IPhysicsWorld
        {
            int _next = 1;
            public readonly List<(PhysicsShape Shape, Pose Pose)> Added = new();
            public readonly List<StaticHandle> Removed = new();

            public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
            {
                Added.Add((shape, pose));
                return new StaticHandle(_next++);
            }

            public void RemoveStatic(StaticHandle handle) => Removed.Add(handle);

            public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body,
                PhysicsMaterial? material = null) => throw new NotSupportedException();
            public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
            public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
            public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
                => throw new NotSupportedException();
            public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
                => throw new NotSupportedException();
            public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
            public ConstraintHandle AddConstraint(in ConstraintDescription description) =>
                throw new NotSupportedException();
            public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
            public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
            public void Step(float dt) { }
            public bool Raycast(Vector3 o, Vector3 d, float max, out RayHit hit, QueryFilter f = default)
                => throw new NotSupportedException();
            public bool SweepCapsule(CapsuleShape c, Pose p, Vector3 d, float max, out SweepHit hit,
                QueryFilter f = default) => throw new NotSupportedException();
            public bool ComputePenetration(CapsuleShape c, Pose p, out Vector3 mtv)
                => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
