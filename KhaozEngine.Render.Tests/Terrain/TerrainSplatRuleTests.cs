using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>The consumer-supplied splat rule (issue #373). Headless: the chunk builder is CPU only, and the sink
    /// tests go through the internal CpuBuild seam with no GPU device.
    /// <para>The load-bearing test here is the NULL one. The rule is opt-in and every existing consumer passes
    /// nothing, so "null bakes what the engine baked before" is the whole compatibility story, and it is asserted by
    /// recomputing <see cref="TerrainSplatWeights.From"/> independently over a sampled grid and comparing the five
    /// floats EXACTLY, not by leaning on a golden image that would also pass if both sides drifted together.</para></summary>
    public class TerrainSplatRuleTests
    {
        static TerrainField Field() => new TerrainField(TerrainPresets.Clearing());
        static TerrainChunkRegion Region() => new TerrainChunkRegion { OriginX = -30f, OriginZ = -30f, Size = 60f };

        /// <summary>The builder's own default snow line. The rule seam must not disturb it.</summary>
        const float SnowLine = 60f;

        static void AssertSameWeights(in TerrainSplatWeights expected, in TerrainSplatWeights actual, string where)
        {
            Assert.True(expected.Grass == actual.Grass, $"{where}: grass {expected.Grass} != {actual.Grass}");
            Assert.True(expected.Dirt == actual.Dirt, $"{where}: dirt {expected.Dirt} != {actual.Dirt}");
            Assert.True(expected.Rock == actual.Rock, $"{where}: rock {expected.Rock} != {actual.Rock}");
            Assert.True(expected.Sand == actual.Sand, $"{where}: sand {expected.Sand} != {actual.Sand}");
            Assert.True(expected.Snow == actual.Snow, $"{where}: snow {expected.Snow} != {actual.Snow}");
        }

        [Fact]
        public void Null_rule_bakes_exactly_the_engine_weights_over_the_whole_grid()
        {
            // The compatibility assertion, vertex by vertex rather than in aggregate: with no rule the builder must
            // store precisely what TerrainSplatWeights.From produces for that vertex's own inputs. Vertices are
            // chunk-local in X/Z, so the field is re-sampled at position + region origin (float addition commutes,
            // so this reproduces the builder's absolute coordinate bit-for-bit).
            var field = Field();
            TerrainChunkRegion region = Region();
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 1);

            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                Vector3 p = chunk.Mesh.Vertices[i].Position;
                float x = p.X + region.OriginX, z = p.Z + region.OriginZ;
                float slope01 = 1f - field.SampleNormal(x, z).Y;
                var expected = TerrainSplatWeights.From(
                    field.SampleHeight(x, z), slope01, field.SampleBiome(x, z), field.WaterLevel, SnowLine);
                AssertSameWeights(expected, chunk.Splat[i], $"vertex {i}");
            }
        }

        [Fact]
        public void Passing_null_is_identical_to_not_passing_a_rule_at_all()
        {
            // Both overloads, both call shapes. Weights AND the ramp vertex colour, since the colour is derived from
            // the weights and is what the untextured path actually renders.
            var field = Field();
            TerrainChunkRegion region = Region();
            TerrainChunkMesh omitted = TerrainChunkBuilder.Build(field, region, lod: 1);
            TerrainChunkMesh explicitNull = TerrainChunkBuilder.Build(field, region, lod: 1, splatRule: null);
            TerrainChunkMesh viaLodConfig = TerrainChunkBuilder.Build(field, region, lod: 1, TerrainLodConfig.Default, splatRule: null);

            Assert.Equal(omitted.Mesh.Vertices.Length, explicitNull.Mesh.Vertices.Length);
            Assert.Equal(omitted.Mesh.Vertices.Length, viaLodConfig.Mesh.Vertices.Length);
            for (int i = 0; i < omitted.Mesh.Vertices.Length; i++)
            {
                AssertSameWeights(omitted.Splat[i], explicitNull.Splat[i], $"explicit null, vertex {i}");
                AssertSameWeights(omitted.Splat[i], viaLodConfig.Splat[i], $"lodConfig overload, vertex {i}");
                Assert.Equal(omitted.Mesh.Vertices[i].Color, explicitNull.Mesh.Vertices[i].Color);
                Assert.Equal(omitted.Mesh.Vertices[i].Color, viaLodConfig.Mesh.Vertices[i].Color);
            }
        }

        [Fact]
        public void Rule_receives_the_engine_result_as_Default_alongside_the_vertex_inputs()
        {
            // Default is the reason the seam hands over a context instead of raw inputs: a consumer adjusting one
            // channel must not have to reimplement (and then drift from) the engine's own mix.
            var field = Field();
            TerrainChunkRegion region = Region();
            var seen = new List<TerrainSplatContext>();
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(field, region, lod: 1,
                splatRule: ctx => { seen.Add(ctx); return ctx.Default; });

            Assert.Equal(chunk.SurfaceVertexCount, seen.Count);
            for (int i = 0; i < seen.Count; i++)
            {
                TerrainSplatContext ctx = seen[i];
                Vector3 p = chunk.Mesh.Vertices[i].Position;
                float x = p.X + region.OriginX, z = p.Z + region.OriginZ;

                Assert.Equal(x, ctx.WorldX);                       // ABSOLUTE world coords, not the chunk-local ones
                Assert.Equal(z, ctx.WorldZ);
                Assert.Equal(p.Y, ctx.Height);                     // absolute world height, same as the vertex
                Assert.Equal(1f - field.SampleNormal(x, z).Y, ctx.Slope01);
                Assert.Equal(field.SampleBiome(x, z), ctx.Biome);

                var engine = TerrainSplatWeights.From(
                    field.SampleHeight(x, z), ctx.Slope01, ctx.Biome, field.WaterLevel, SnowLine);
                AssertSameWeights(engine, ctx.Default, $"Default at vertex {i}");
            }
        }

        [Fact]
        public void Returning_Default_unchanged_bakes_the_engine_weights()
        {
            // The "defer to the engine" escape hatch has to be free, or a rule that only cares about one region of
            // the map quietly changes the rest of the world.
            var field = Field();
            TerrainChunkRegion region = Region();
            TerrainChunkMesh plain = TerrainChunkBuilder.Build(field, region, lod: 1);
            TerrainChunkMesh passthrough = TerrainChunkBuilder.Build(field, region, lod: 1, splatRule: ctx => ctx.Default);

            for (int i = 0; i < plain.Mesh.Vertices.Length; i++)
            {
                AssertSameWeights(plain.Splat[i], passthrough.Splat[i], $"vertex {i}");
                Assert.Equal(plain.Mesh.Vertices[i].Color, passthrough.Mesh.Vertices[i].Color);
            }
        }

        [Fact]
        public void Rule_output_is_what_gets_baked_into_the_weights_and_the_ramp_colour()
        {
            var allSand = new TerrainSplatWeights { Sand = 1f };
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1, splatRule: _ => allSand);

            Vector4 expected = TerrainRamp.Of(allSand);
            for (int i = 0; i < chunk.Mesh.Vertices.Length; i++)
            {
                AssertSameWeights(allSand, chunk.Splat[i], $"vertex {i}");
                Assert.Equal(expected, chunk.Mesh.Vertices[i].Color);   // skirt copies included
            }
        }

        [Fact]
        public void Rule_runs_once_per_surface_vertex_and_skirts_copy_their_edge()
        {
            // The hot-path claim in the doc contract, made concrete: a skirt vertex is a copy of the edge vertex it
            // hangs under, so the rule is never called for it. Counting also pins the per-chunk cost a consumer is
            // told to budget for.
            int calls = 0;
            TerrainChunkMesh chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1,
                splatRule: ctx => { calls++; return ctx.Default; });

            int res = TerrainLod.ResolutionFor(1);
            Assert.Equal((res + 1) * (res + 1), chunk.SurfaceVertexCount);
            Assert.Equal(chunk.SurfaceVertexCount, calls);
            Assert.True(chunk.Mesh.Vertices.Length > chunk.SurfaceVertexCount);   // skirts exist and were not re-ruled
        }

        [Fact]
        public void A_lake_shoreline_rule_paints_sand_only_where_it_is_asked_to()
        {
            // The motivating case (issue #373): a second body of water the engine's single WaterLevel cannot see. The
            // rule pushes sand up inside a disc and defers everywhere else, which is why the assertion is that the
            // OUTSIDE is untouched, not merely that the inside changed.
            var field = Field();
            TerrainChunkRegion region = Region();
            const float lakeX = -10f, lakeZ = -10f, radius = 12f;

            TerrainSplatWeights Shore(TerrainSplatContext ctx)
            {
                float dx = ctx.WorldX - lakeX, dz = ctx.WorldZ - lakeZ;
                if (dx * dx + dz * dz > radius * radius) return ctx.Default;
                TerrainSplatWeights w = ctx.Default;
                w.Sand += 4f;
                return w.Normalized();
            }

            TerrainChunkMesh plain = TerrainChunkBuilder.Build(field, region, lod: 1);
            TerrainChunkMesh shored = TerrainChunkBuilder.Build(field, region, lod: 1, splatRule: Shore);

            int inside = 0, sandier = 0;
            for (int i = 0; i < shored.SurfaceVertexCount; i++)
            {
                Vector3 p = shored.Mesh.Vertices[i].Position;
                float dx = p.X + region.OriginX - lakeX, dz = p.Z + region.OriginZ - lakeZ;
                if (dx * dx + dz * dz > radius * radius)
                {
                    AssertSameWeights(plain.Splat[i], shored.Splat[i], $"outside the disc, vertex {i}");
                }
                else
                {
                    inside++;
                    // Never LESS sandy (a vertex already at full sand simply stays there), normalized either way.
                    Assert.True(shored.Splat[i].Sand >= plain.Splat[i].Sand, $"inside the disc, vertex {i} lost sand");
                    Assert.Equal(1f, Sum(shored.Splat[i]), 5);
                    if (shored.Splat[i].Sand > plain.Splat[i].Sand) sandier++;
                }
            }
            Assert.True(inside > 0, "the disc has to cover some vertices or the test asserts nothing");
            Assert.True(sandier > 0, "the rule has to actually change something inside the disc");
        }

        [Fact]
        public void Sink_threads_its_rule_into_every_chunk_it_builds()
        {
            // The seam a game actually configures: games drive the streamer through the sink, they do not call
            // TerrainChunkBuilder.Build. Both ctors carry the rule.
            var field = Field();
            var allSnow = new TerrainSplatWeights { Snow = 1f };
            var coord = new ChunkCoord(0, 0);

            var ruled = new Scene3DChunkSink(scene: null!, field, new ScatterConfig(),
                propMeshes: new Dictionary<string, MeshHandle>(), chunkSize: 60f, propDrawRadius: 90f,
                splatRule: _ => allSnow);
            var plain = new Scene3DChunkSink(scene: null!, field, new ScatterConfig(),
                propMeshes: new Dictionary<string, MeshHandle>(), chunkSize: 60f, propDrawRadius: 90f);

            var ruledCpu = (Scene3DChunkSink.CpuBuild)ruled.BuildCpu(coord, lod: 1);
            var plainCpu = (Scene3DChunkSink.CpuBuild)plain.BuildCpu(coord, lod: 1);

            for (int i = 0; i < ruledCpu.Mesh.Splat.Length; i++)
                AssertSameWeights(allSnow, ruledCpu.Mesh.Splat[i], $"ruled sink, vertex {i}");
            // And the default sink is untouched by the seam existing.
            TerrainChunkMesh direct = TerrainChunkBuilder.Build(field, ChunkGrid.RegionOf(coord, 60f), lod: 1, TerrainLodConfig.Default);
            for (int i = 0; i < plainCpu.Mesh.Splat.Length; i++)
                AssertSameWeights(direct.Splat[i], plainCpu.Mesh.Splat[i], $"plain sink, vertex {i}");
        }

        [Fact]
        public void Multi_layer_sink_ctor_carries_the_rule_too()
        {
            var field = Field();
            var allRock = new TerrainSplatWeights { Rock = 1f };
            var layers = new[] { PropLayer.ScatterLayer(new ScatterConfig(), new Dictionary<string, MeshHandle>(), 90f) };
            var sink = new Scene3DChunkSink(scene: null!, field, layers, chunkSize: 60f, splatRule: _ => allRock);

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(1, -2), lod: 2);
            for (int i = 0; i < cpu.Mesh.Splat.Length; i++)
                AssertSameWeights(allRock, cpu.Mesh.Splat[i], $"vertex {i}");
        }

        [Fact]
        public void Normalized_restores_the_sum_the_shader_relies_on()
        {
            // The splat pipeline packs four weights and reconstructs snow as 1 - sum, so an unnormalized rule result
            // renders as snow bleeding in. Normalized is the one-call fix a rule is told to reach for.
            var w = new TerrainSplatWeights { Grass = 2f, Dirt = 1f, Rock = 1f };
            TerrainSplatWeights n = w.Normalized();

            Assert.Equal(1f, Sum(n), 5);
            Assert.Equal(0.5f, n.Grass, 5);
            Assert.Equal(0.25f, n.Dirt, 5);
            Assert.Equal(0.25f, n.Rock, 5);
        }

        [Fact]
        public void Normalized_falls_back_to_grass_when_there_is_no_mix_to_preserve()
        {
            TerrainSplatWeights n = default(TerrainSplatWeights).Normalized();
            Assert.Equal(1f, n.Grass);
            Assert.Equal(1f, Sum(n), 5);
        }

        [Fact]
        public void From_already_returns_a_normalized_set()
        {
            // Normalized() was factored out of From, so this guards that the extraction kept From's contract.
            var w = TerrainSplatWeights.From(height: 12f, slope01: 0.4f, biome: BiomeId.Forest, waterLevel: 0f, snowLine: SnowLine);
            AssertSameWeights(w, w.Normalized(), "From output");
        }

        static float Sum(in TerrainSplatWeights w) => w.Grass + w.Dirt + w.Rock + w.Sand + w.Snow;
    }
}
