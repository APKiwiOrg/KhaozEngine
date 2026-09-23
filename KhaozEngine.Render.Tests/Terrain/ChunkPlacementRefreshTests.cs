using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers the props-only chunk refresh: <see cref="TerrainStreamer.RefreshPlacements"/> routes to
    /// <see cref="IChunkPlacementRefreshSink"/>, and <see cref="Scene3DChunkSink"/> re-serves only its live
    /// placement-source layers (plus companions they host) without touching the chunk's terrain mesh, terrain
    /// collider, scatter or frozen layers. The sink rows run with a null <see cref="Scene3D"/>, so any terrain upload
    /// or unload would throw: passing is itself the proof that no terrain work happened. One GPU-gated row repeats it
    /// against a chunk loaded through a real device.</summary>
    public class ChunkPlacementRefreshTests
    {
        const float Chunk = 60f;
        static readonly ChunkCoord Origin = new(0, 0);

        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        sealed class MutableSource : IPlacementSource
        {
            volatile PropPlacement[] _published = Array.Empty<PropPlacement>();

            public void Publish(params PropPlacement[] placements) => _published = placements;

            public void PlacementsIn(RectArea area, List<PropPlacement> into)
            {
                foreach (PropPlacement p in _published)
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

        static ScatterConfig Trees() => new()
        {
            Seed = 3,
            CellSize = 10f,
            Jitter = 0f,
            ClearingRadius = 0f,
            Biomes = new[]
            {
                new BiomeScatterRule
                {
                    Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind("pine", 1f) },
                },
            },
        };

        static CompanionConfig Bushes() => new()
        {
            Seed = 7,
            HostKinds = new[] { "tree" },
            Kinds = new[] { new PropKind("bush", 1f) },
            CountMin = 1,
            CountMax = 1,
        };

        static PropPlacement Tree(float x, float z) => new("tree", x, 5f, z, 1f, 0f, 0);
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

        [Fact]
        public void Refresh_ReservesLiveLayerAndItsCompanions_AndLeavesTerrainScatterAndFrozenLayersAlone()
        {
            var source = new MutableSource();
            source.Publish(Tree(10f, 10f));
            var sink = new Scene3DChunkSink(null!, Flat(5f), new[]
            {
                PropLayer.ScatterLayer(Trees(), NoMeshes(), 90f),
                PropLayer.PlacementLayer(new[] { Rock(30f, 30f) }, NoMeshes(), 90f),
                PropLayer.PlacementLayer(source, NoMeshes(), 90f),
                PropLayer.CompanionLayer(2, Bushes(), NoMeshes(), 60f),
            }, Chunk);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);
            IReadOnlyList<PropPlacement> scatterBefore = load.LayerProps[0];
            IReadOnlyList<PropPlacement> frozenBefore = load.LayerProps[1];
            Assert.Single(load.LayerProps[2]);
            Assert.Single(load.LayerProps[3]);

            source.Publish(Tree(10f, 10f), Tree(40f, 20f), Tree(500f, 500f));
            sink.RefreshPlacements(Origin, load, ChunkRing.Gameplay);

            Assert.Equal((7, 3), (load.Mesh.Index, load.Mesh.Generation));
            Assert.Same(scatterBefore, load.LayerProps[0]);
            Assert.Same(frozenBefore, load.LayerProps[1]);
            Assert.Equal(2, load.LayerProps[2].Count);
            Assert.Equal(2, load.LayerProps[3].Count);
            Assert.False(load.HasTerrainCollider);
        }

        [Fact]
        public void Refresh_RebuildsPropStaticsFromTheAdoptedPlacements()
        {
            var source = new MutableSource();
            var physics = new FakePhysicsWorld();
            var shapes = new Dictionary<string, PhysicsShape> { ["rock"] = new BoxShape(new Vector3(0.4f)) };
            var sink = new Scene3DChunkSink(null!, Flat(5f),
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f) }, Chunk, physics: physics,
                collisionShapes: shapes);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);

            source.Publish(Rock(5f, 5f), Rock(20f, 20f));
            sink.RefreshPlacements(Origin, load, ChunkRing.Gameplay);
            Assert.Equal(2, load.Statics.Count);
            Assert.Equal(2, physics.Added.Count);

            source.Publish(Rock(5f, 5f));
            sink.RefreshPlacements(Origin, load, ChunkRing.Gameplay);
            Assert.Single(load.Statics);
            Assert.Equal(2, physics.Removed.Count);
            Assert.Equal(3, physics.Added.Count);
            Assert.False(load.HasTerrainCollider);
        }

        [Fact]
        public void Refresh_OnADecorChunkWithoutHlod_KeepsItsEmptyProps()
        {
            var source = new MutableSource();
            var sink = new Scene3DChunkSink(null!, Flat(5f),
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f) }, Chunk);
            IReadOnlyList<PropPlacement>[] empty = { Array.Empty<PropPlacement>() };
            var load = new Scene3DChunkSink.ChunkLoad
            {
                Mesh = new MeshHandle(4, 1), LayerProps = empty, Ring = ChunkRing.Decor,
                Region = ChunkGrid.RegionOf(Origin, Chunk),
            };

            source.Publish(Rock(5f, 5f));
            sink.RefreshPlacements(Origin, load, ChunkRing.Decor);

            Assert.Same(empty, load.LayerProps);
            Assert.Equal((4, 1), (load.Mesh.Index, load.Mesh.Generation));
        }

        [Fact]
        public void Refresh_WithNoLiveSource_IsANoOp()
        {
            var sink = new Scene3DChunkSink(null!, Flat(5f),
                new[] { PropLayer.ScatterLayer(Trees(), NoMeshes(), 90f) }, Chunk);
            Scene3DChunkSink.ChunkLoad load = LoadedGameplay(sink);
            IReadOnlyList<PropPlacement>[] before = load.LayerProps;

            sink.RefreshPlacements(Origin, load, ChunkRing.Gameplay);

            Assert.Same(before, load.LayerProps);
        }

        [Fact]
        public void Streamer_RefreshPlacements_UsesThePropsOnlySeam_FallsBackToReLod_AndSkipsUnloaded()
        {
            var config = new StreamerConfig(LoadRadius: 1, UnloadRadius: 2, MaxLoadsPerFrame: 16, ChunkSize: Chunk,
                Async: false);
            var refreshing = new RecordingSink(propsOnly: true);
            object loadedHandle;
            using (var streamer = new TerrainStreamer(config, refreshing))
            {
                streamer.PrimeAround(Vector3.Zero);
                loadedHandle = refreshing.Handles[Origin];
                Assert.True(streamer.RefreshPlacements(Origin));
                Assert.False(streamer.RefreshPlacements(new ChunkCoord(50, 50)));
            }
            Assert.Equal(new[] { Origin }, refreshing.Refreshed.ToArray());
            Assert.Same(loadedHandle, refreshing.RefreshedHandles[0]);
            Assert.Empty(refreshing.ReLods);

            var plain = new RecordingSink(propsOnly: false);
            using (var streamer = new TerrainStreamer(config, plain.AsPlain()))
            {
                streamer.PrimeAround(Vector3.Zero);
                Assert.True(streamer.RefreshPlacements(Origin));
            }
            Assert.Equal(new[] { Origin }, plain.ReLods.ToArray());
        }

        [GpuFact]
        public void Refresh_OnARealLoadedChunk_KeepsItsTerrainMeshHandle()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);

            var source = new MutableSource();
            source.Publish(Rock(5f, 5f));
            using var sink = new Scene3DChunkSink(scene, Flat(5f),
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f, colliders: false) }, Chunk);
            var load = (Scene3DChunkSink.ChunkLoad)sink.Load(Origin, lod: 0);
            MeshHandle terrain = load.Mesh;

            source.Publish(Rock(5f, 5f), Rock(25f, 25f));
            sink.RefreshPlacements(Origin, load, ChunkRing.Gameplay);

            Assert.Equal((terrain.Index, terrain.Generation), (load.Mesh.Index, load.Mesh.Generation));
            Assert.Equal(2, load.LayerProps[0].Count);
        }

        sealed class RecordingSink : IChunkPlacementRefreshSink
        {
            readonly bool _propsOnly;
            public readonly Dictionary<ChunkCoord, object> Handles = new();
            public readonly List<ChunkCoord> Refreshed = new();
            public readonly List<object> RefreshedHandles = new();
            public readonly List<ChunkCoord> ReLods = new();

            public RecordingSink(bool propsOnly) => _propsOnly = propsOnly;

            public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Handles[coord] = new object();
            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => ReLods.Add(coord);
            public void Unload(ChunkCoord coord, object handle) => Handles.Remove(coord);

            public void RefreshPlacements(ChunkCoord coord, object handle, ChunkRing ring)
            {
                if (!_propsOnly) throw new InvalidOperationException("A plain sink has no props-only seam.");
                Refreshed.Add(coord);
                RefreshedHandles.Add(handle);
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
