using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Where <see cref="RenderService"/> streams. The headless rows run the plan's own render distance
    /// through a real synchronous <see cref="TerrainStreamer"/> over the viewport's authored placement layer, so
    /// "resident" means exactly what the render's world would hold, and then check the plan's prop cull reaches the
    /// placement too. The GPU rows render with and without a marker placement and require the pixels to differ,
    /// which proves the marker drew end to end.</summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class RenderStreamPlanTests
    {
        readonly ITestOutputHelper _output;

        public RenderStreamPlanTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void RenderView_StreamsAroundTheEye_SoAPlacementNearAFarEyeIsDrawn()
        {
            MapDocument doc = FlatDoc(1024f);
            doc.Placements.Add(Marker("near-eye", 900f, 900f));
            var eye = new Vector3(880f, 40f, 880f);

            RenderStreamPlan plan = RenderStreamPlan.ForView(eye);

            Assert.Equal(eye, plan.Focus);
            Assert.Contains(Drawn(doc, plan), p => p.X == 900f && p.Z == 900f);
            RenderStreamPlan boundsCentre = RenderStreamPlan.ForView(Vector3.Zero);
            Assert.DoesNotContain(Drawn(doc, boundsCentre), p => p.X == 900f);
        }

        [Fact]
        public void RenderTopDown_OverTheWholeDocument_DrawsItsFarCornerPlacement()
        {
            MapDocument doc = FlatDoc(600f);
            doc.Placements.Add(Marker("corner", 590f, 590f));
            doc.Placements.Add(Marker("opposite", -590f, -590f));

            RenderStreamPlan plan = RenderStreamPlan.ForTopDown(Vector3.Zero, -600f, -600f, 600f, 600f);

            Assert.False(plan.Capped);
            List<PropPlacement> drawn = Drawn(doc, plan);
            Assert.Contains(drawn, p => p.X == 590f && p.Z == 590f);
            Assert.Contains(drawn, p => p.X == -590f && p.Z == -590f);
            Assert.True(plan.CompanionDrawRadius >= Horizontal(Vector3.Zero, 590f, 590f));
            Assert.DoesNotContain(Drawn(doc, RenderStreamPlan.ForView(Vector3.Zero)), p => p.X == 590f);
        }

        [Fact]
        public void RenderTopDown_OverASmallRect_KeepsTheDefaultProfileButWidensCompanions()
        {
            RenderStreamPlan plan = RenderStreamPlan.ForTopDown(Vector3.Zero, -100f, -100f, 100f, 100f);

            Assert.Equal(RenderDistanceProfile.Default, plan.RenderDistance);
            Assert.Equal(MathF.Sqrt(2f) * 100f, plan.CompanionDrawRadius, 3);
            Assert.False(plan.Capped);
        }

        [Fact]
        public void RenderTopDown_OverAHugeRect_CapsTheRing()
        {
            RenderStreamPlan plan = RenderStreamPlan.ForTopDown(Vector3.Zero, -4000f, -4000f, 4000f, 4000f);

            Assert.True(plan.Capped);
            Assert.Equal(RenderStreamPlan.MaxCoverChunks, plan.RenderDistance.GameplayLoadRadiusChunks);
            Assert.Equal(RenderStreamPlan.MaxCoverChunks * RenderDistanceProfile.ChunkMeters,
                plan.RenderDistance.PropDrawRadius);
            plan.RenderDistance.Validate();
        }

        [Fact]
        public void ConfigureWorld_AppliesThePlanBeforeBuild()
        {
            var render = new RenderService(new MapEditSession());
            RenderStreamPlan plan = RenderStreamPlan.ForTopDown(Vector3.Zero, -600f, -600f, 600f, 600f);
            var doc = new MapDocument { Id = "plan" };
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "forest" });
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "ferns", HostLayer = "forest" });

            using ViewportWorld world = render.ConfigureWorld(null!, textured: true, plan);

            Assert.Equal(plan.RenderDistance, world.RenderDistance);
            Assert.Equal(plan.CompanionDrawRadius, world.BuildPropLayers(doc)[1].DrawRadius);
            Assert.Equal(plan.RenderDistance.PropDrawRadius, world.BuildSinkLayers(doc)[2].DrawRadius);
        }

        [GpuFact]
        public void RenderView_FromAFarEye_RendersTheMarkerNearThatEye()
        {
            MapDocument doc = FlatDoc(1024f);
            var eye = new Vector3(880f, Ground + 30f, 880f);
            byte[] without = WithSession(doc, render =>
                render.RenderView(eye.X, eye.Y, eye.Z, 905f, Ground, 905f, width: 128, height: 128));
            doc.Placements.Add(Marker("near-eye", 905f, 905f, scale: 6f));
            byte[] with = WithSession(doc, render =>
                render.RenderView(eye.X, eye.Y, eye.Z, 905f, Ground, 905f, width: 128, height: 128));

            Assert.False(without.AsSpan().SequenceEqual(with), "the marker near the eye did not change the view.");
        }

        [GpuFact]
        public void RenderTopDown_WholeDocument_RendersTheFarCornerMarker()
        {
            MapDocument doc = FlatDoc(600f);
            byte[] without = WithSession(doc, render => render.RenderTopDown(width: 256, height: 256,
                includeOverlays: false));
            doc.Placements.Add(Marker("corner", 560f, 560f, scale: 30f));
            var clock = Stopwatch.StartNew();
            byte[] with = WithSession(doc, render => render.RenderTopDown(width: 256, height: 256,
                includeOverlays: false));
            _output.WriteLine($"render_topdown over 1200 m x 1200 m: {clock.Elapsed.TotalMilliseconds:F0} ms");

            Assert.False(without.AsSpan().SequenceEqual(with), "the far-corner marker did not change the top-down.");
        }

        const float Ground = 5f;
        const float MarkerThickness = 0.1f;

        static float Horizontal(Vector3 focus, float x, float z) =>
            MathF.Sqrt((x - focus.X) * (x - focus.X) + (z - focus.Z) * (z - focus.Z));

        // The placements a render with this plan draws: resident in its streamed ring AND inside its prop cull.
        static List<PropPlacement> Drawn(MapDocument doc, RenderStreamPlan plan)
        {
            TerrainField field = MapRuntime.BuildField(doc, MapDocRegistry.CreateDefault());
            var layer = new AuthoredPlacementLayer(TerrainChunkRegion.DefaultSize);
            layer.Refresh(doc, field, new EditorVisibility(), null, invalidate: null);
            var sink = new ResidentSink(layer);
            using (var streamer = new TerrainStreamer(plan.RenderDistance.ToStreamerConfig().Synchronous(), sink))
                streamer.PrimeAround(plan.Focus);
            return sink.Served
                .Where(p => Horizontal(plan.Focus, p.X, p.Z) <= plan.RenderDistance.PropDrawRadius)
                .ToList();
        }

        static MapDocument FlatDoc(float half)
        {
            var doc = new MapDocument
            {
                Id = "render-plan",
                Bounds = new MapBounds { MinX = -half, MinZ = -half, MaxX = half, MaxZ = half },
            };
            doc.Terrain.GentleAmplitude = 0f;
            doc.Terrain.DetailOctaves = 0;
            doc.Terrain.WaterLevel = -50f;
            doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Meadow, BaseHeight = Ground });
            return doc;
        }

        static MapPlacement Marker(string id, float x, float z, float scale = 1f) =>
            new() { Id = id, Kind = "marker", X = x, Z = z, Y = Ground + 0.5f, Scale = scale };

        static byte[] WithSession(MapDocument doc, Func<RenderService, byte[]> render)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-render-plan-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                WriteFlatQuadGlb(Path.Combine(dir, "marker.glb"));
                string manifest = Path.Combine(dir, "props.manifest.json");
                File.WriteAllText(manifest,
                    "{ \"props\": [ { \"id\": \"marker\", \"file\": \"marker.glb\", \"heightMeters\": 0.1 } ] }");
                string path = Path.Combine(dir, "zone.map.json");
                MapDocumentFile.Save(doc, path);
                var session = new MapEditSession();
                session.Open(path, new[] { manifest });
                return render(new RenderService(session));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // A one-metre square slab a tenth of a metre thick (the prop loader needs a measurable height), its top and
        // bottom faces wound both ways so it faces the camera from any side, in a solid red that reads against the
        // meadow ramp.
        static void WriteFlatQuadGlb(string path)
        {
            var material = new MaterialBuilder("marker").WithBaseColor(new Vector4(1f, 0.05f, 0.05f, 1f));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("marker");
            var prim = mesh.UsePrimitive(material);
            foreach (float y in new[] { 0f, MarkerThickness })
            {
                VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(float x, float z) =>
                    new(new VertexPositionNormal(new Vector3(x, y, z), Vector3.UnitY));
                prim.AddQuadrangle(V(-0.5f, -0.5f), V(0.5f, -0.5f), V(0.5f, 0.5f), V(-0.5f, 0.5f));
                prim.AddQuadrangle(V(-0.5f, -0.5f), V(-0.5f, 0.5f), V(0.5f, 0.5f), V(0.5f, -0.5f));
            }
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            scene.ToGltf2().SaveGLB(path);
        }

        /// <summary>Records every placement a resident chunk's build served, the way the render's sink queries its
        /// authored placement layer.</summary>
        sealed class ResidentSink(IPlacementSource source) : IChunkSink
        {
            readonly Dictionary<ChunkCoord, List<PropPlacement>> _props = new();

            public IEnumerable<PropPlacement> Served => _props.Values.SelectMany(p => p);

            public object Load(ChunkCoord coord, int lod, ChunkRing ring) => Build(coord, ring);
            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring) => Build(coord, ring);
            public void Unload(ChunkCoord coord, object handle) { }

            object Build(ChunkCoord coord, ChunkRing ring)
            {
                var into = new List<PropPlacement>();
                if (ring == ChunkRing.Gameplay)
                    source.PlacementsIn(ChunkGrid.AreaOf(coord, TerrainChunkRegion.DefaultSize), into);
                _props[coord] = into;
                return coord;
            }
        }
    }
}
