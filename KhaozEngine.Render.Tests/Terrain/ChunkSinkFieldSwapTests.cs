using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Issue #105: <c>Scene3DChunkSink.UpdateField</c> stated its flush-before-swap precondition in a doc
    /// comment only, with nothing but a null check in the method itself. An async caller (a game doing runtime
    /// terrain edits) could therefore swap the field out from under a build that was already reading it, and mesh
    /// one chunk from two different fields with nothing anywhere to say so. The swap now refuses while a build is
    /// executing.
    /// <para>Headless throughout: <c>BuildCpu</c> never touches the GPU, so the sink is built over a null scene the
    /// way the other CPU-build tests do. The in-flight window is reached by REENTRY (a live placement source that
    /// calls back into the sink from inside the build) rather than by racing a worker thread, so the test pins the
    /// rule deterministically instead of hoping for an interleaving.</para></summary>
    public class ChunkSinkFieldSwapTests
    {
        static TerrainField Flat(float height) => new(new TerrainConfig
        {
            Seed = 1,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
            },
        });

        static Scene3DChunkSink SinkOver(TerrainField field, IPlacementSource source) =>
            new(null!, field,
                new[] { PropLayer.PlacementLayer(source, new Dictionary<string, MeshHandle>(), drawRadius: 90f) },
                chunkSize: 60f);

        // Queried on the build thread, which is exactly where the sink is mid-build, so calling UpdateField from
        // here is the same window an async streamer opens by swapping on the frame thread while a worker builds.
        sealed class SwappingSource : IPlacementSource
        {
            public Scene3DChunkSink? Sink;
            public TerrainField? Swap;
            public Exception? Caught;
            public int Queries;
            public Action<Scene3DChunkSink>? Change;

            public void PlacementsIn(RectArea area, List<PropPlacement> into)
            {
                Queries++;
                if (Sink is null) return;
                try
                {
                    if (Change is null) Sink.UpdateField(Swap!);
                    else Change(Sink);
                }
                catch (Exception ex) { Caught = ex; }
            }
        }

        sealed class ThrowingSource : IPlacementSource
        {
            public void PlacementsIn(RectArea area, List<PropPlacement> into) =>
                throw new NotSupportedException("this build fails");
        }

        // Which field a built chunk came from, read off the mesh bounds. The two fields under test sit 45 m apart,
        // so a 2 m band identifies one without pinning the field's own micro relief.
        static void AssertBuiltFrom(float baseHeight, Scene3DChunkSink.CpuBuild cpu)
        {
            float floor = cpu.Mesh.Bounds.Min.Y, ceiling = cpu.Mesh.Bounds.Max.Y;
            Assert.True(MathF.Abs(floor - baseHeight) < 2f, $"mesh floor {floor} did not come from the field at {baseHeight}");
            Assert.True(MathF.Abs(ceiling - baseHeight) < 2f, $"mesh ceiling {ceiling} did not come from the field at {baseHeight}");
        }

        [Fact]
        public void UpdateField_DuringABuild_IsRefused_AndTheBuildKeepsItsOwnField()
        {
            TerrainField oldField = Flat(5f), newField = Flat(50f);
            var source = new SwappingSource { Swap = newField };
            Scene3DChunkSink sink = SinkOver(oldField, source);
            source.Sink = sink;

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), lod: 0);

            Assert.Equal(1, source.Queries);   // not vacuous: the source really was queried from inside the build
            var ex = Assert.IsType<ChunkSinkConfigurationStateException>(source.Caught);
            Assert.IsAssignableFrom<InvalidOperationException>(ex);
            Assert.Equal(ChunkSinkConfigurationRefusal.FieldChangeDuringBuild, ex.Reason);
            Assert.Equal(1, ex.BuildCount);

            // The refused swap did not land, so this build's mesh is the old field's all the way through. The two
            // fields are 45 m apart in base height, so a couple of metres of band is plenty to tell them apart (a
            // flat band still carries the field's own micro relief, so this is not an equality check).
            AssertBuiltFrom(5f, cpu);
        }

        [Theory]
        [InlineData(false, ChunkSinkConfigurationRefusal.LayersChangeDuringBuild)]
        [InlineData(true, ChunkSinkConfigurationRefusal.LodChangeDuringBuild)]
        public void ConfigurationChangesDuringABuildAreRefused(bool changeLod, ChunkSinkConfigurationRefusal reason)
        {
            var source = new SwappingSource();
            PropLayer[] layers = [PropLayer.PlacementLayer(source, new Dictionary<string, MeshHandle>(), 90f)];
            using var sink = new Scene3DChunkSink(null!, Flat(5f), layers, 60f);
            source.Sink = sink;
            source.Change = current =>
            {
                if (changeLod) current.ReconfigureLod(TerrainLodConfig.Default);
                else current.UpdateLayers(layers);
            };
            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), 0);
            var error = Assert.IsType<ChunkSinkConfigurationStateException>(source.Caught);
            Assert.Equal(reason, error.Reason);
            Assert.Equal(1, error.BuildCount);
            AssertBuiltFrom(5f, cpu);
            source.Change(sink);
        }

        sealed class NoDynamics : IChunkDynamicsSource
        {
            public IReadOnlyList<DynamicSpawn> SpawnsFor(ChunkCoord coord) => Array.Empty<DynamicSpawn>();
        }

        [Theory]
        [InlineData(false, ChunkSinkConfigurationRefusal.DynamicsRequiresPhysics, "dynamicsSource")]
        [InlineData(true, ChunkSinkConfigurationRefusal.CollisionRequiresPhysics, "collideTerrain")]
        public void PhysicsFeaturesRequireAPhysicsWorld(bool collision, ChunkSinkConfigurationRefusal reason, string paramName)
        {
            TerrainField field = Flat(5f);
            PropLayer[] layers = [PropLayer.PlacementLayer(Array.Empty<PropPlacement>(), new Dictionary<string, MeshHandle>(), 90f)];
            IChunkDynamicsSource? dynamics = collision ? null : new NoDynamics();
            var error = Assert.Throws<ChunkSinkConfigurationArgumentException>(() =>
                new Scene3DChunkSink(null!, field, layers, 60f, dynamicsSource: dynamics, collideTerrain: collision));
            Assert.IsAssignableFrom<ArgumentException>(error);
            Assert.Equal(reason, error.Reason);
            Assert.Equal(paramName, error.ParamName);
            using var physics = new BepuPhysicsWorld();
            using var sink = new Scene3DChunkSink(null!, field, layers, 60f,
                physics: physics, dynamicsSource: dynamics, collideTerrain: collision);
        }

        [Fact]
        public void UpdateField_OutsideABuild_Swaps_AndStillRejectsNull()
        {
            // The guard is scoped to the in-flight window: the ordinary editor swap (nothing building) is untouched.
            var source = new SwappingSource();   // inert: no sink wired, so it never reenters
            Scene3DChunkSink sink = SinkOver(Flat(5f), source);

            sink.UpdateField(Flat(50f));

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), lod: 0);
            AssertBuiltFrom(50f, cpu);
            Assert.Throws<ArgumentNullException>(() => sink.UpdateField(null!));
        }

        [Fact]
        public void AFaultedBuild_DoesNotWedge_TheSwap()
        {
            // The in-flight count unwinds through a throwing build too, or one poisoned chunk would refuse every
            // field swap for the rest of the session (the streamer contains build faults and keeps running).
            Scene3DChunkSink sink = SinkOver(Flat(5f), new ThrowingSource());
            Assert.Throws<NotSupportedException>(() => sink.BuildCpu(new ChunkCoord(0, 0), lod: 0));

            sink.UpdateField(Flat(50f));

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), lod: 0, ChunkRing.Decor);
            AssertBuiltFrom(50f, cpu);
        }
    }
}
