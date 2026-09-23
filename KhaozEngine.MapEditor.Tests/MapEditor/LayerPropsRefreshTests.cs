using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The props-only partial path for exclusion and scatter-override edits (issue #771). A gizmo drag runs
    /// through the real <see cref="EditorToolController"/>, the real <see cref="EditorDocument"/> accumulation, the
    /// real <see cref="ViewportWorld.BuildSinkLayers"/> and the real sink and streamer, driven device-free: the
    /// streamer's sink wraps a <see cref="Scene3DChunkSink"/> built on a null scene, so any terrain upload would
    /// throw. After every drag frame, after release, after undo and after redo, every loaded chunk's per-layer
    /// placements must equal what a full rebuild computes, which an independent oracle below derives from the
    /// public scatter API rather than from the sink.</summary>
    public class LayerPropsRefreshTests
    {
        const float Chunk = TerrainChunkRegion.DefaultSize;
        static readonly Vector3 Down = new(0f, -1f, 0f);

        // Dense scatter with a jitter larger than its cell on one layer (authored jitter has no clamp), plus a
        // companion layer riding it. The dragged exclusion is a rect whose bounds padded by the bare 2 m margin stay
        // inside chunk (0, 0) for the whole drag while its jittered candidates reach across the x = 60 and z = 60
        // seams, so only the document-aware jitter margin keeps the neighbouring chunks correct.
        static MapDocument Doc()
        {
            var doc = new MapDocument
            {
                Id = "props-refresh",
                Bounds = new MapBounds { MinX = -200f, MinZ = -200f, MaxX = 200f, MaxZ = 200f },
            };
            doc.Terrain.WaterLevel = 0f;
            doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Meadow, BaseHeight = 5f, HillAmplitude = 1f });
            doc.ScatterLayers.Add(Layer("trees", cell: 4f, jitter: 6f, density: 0.9f, "pine_a", "oak_a"));
            doc.ScatterLayers.Add(Layer("grass", cell: 2.5f, jitter: 1f, density: 0.6f, "rock_a"));
            doc.CompanionLayers.Add(new MapCompanionLayer
            {
                Name = "understory", HostLayer = "trees", CountMin = 2, CountMax = 3,
                Kinds = { new MapPropKind { Id = "fern" } },
            });
            doc.Exclusions.Add(new MapExclusion { Shape = new RectShapeDoc { MinX = 20f, MinZ = 5f, MaxX = 57.5f, MaxZ = 45f } });
            doc.Exclusions.Add(new MapExclusion
            {
                Shape = new RectShapeDoc { MinX = -90f, MinZ = 10f, MaxX = -70f, MaxZ = 25f },
                Layers = new List<string> { "grass" },
            });
            doc.ScatterOverrides.Add(new MapScatterOverrideDoc
            {
                Shape = new DiscShapeDoc { CenterX = -20f, CenterZ = -40f, Radius = 10f },
                DensityMultiplier = 0.3f,
                Kinds = new List<MapPropKind> { new() { Id = "rock_b" } },
            });
            return doc;
        }

        static MapScatterLayer Layer(string name, float cell, float jitter, float density, params string[] kinds)
        {
            var rule = new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = density };
            foreach (string id in kinds) rule.Kinds.Add(new MapPropKind { Id = id });
            return new MapScatterLayer { Name = name, CellSize = cell, Jitter = jitter, MaxHeight = null, Rules = { rule } };
        }

        /// <summary>A device-free streamed world: the streamer's chunks are loads the wrapped sink fills through its
        /// own props refresh, so a loaded chunk carries exactly the placements a fresh build would give it.</summary>
        sealed class DeviceFreeWorld
        {
            public readonly EditorDocument Editor;
            public readonly ViewportWorld Viewport = new(null!, Array.Empty<string>());
            public readonly Scene3DChunkSink Sink;
            public readonly Chunks Loaded;
            public readonly TerrainStreamer Streamer;

            public DeviceFreeWorld(MapDocument doc)
            {
                Editor = new EditorDocument(doc);
                TerrainField field = MapRuntime.BuildField(doc, Editor.Registry);
                Sink = new Scene3DChunkSink(null!, field, Viewport.BuildSinkLayers(doc), Chunk);
                Loaded = new Chunks(Sink);
                Streamer = new TerrainStreamer(
                    new StreamerConfig(LoadRadius: 3, UnloadRadius: 4, MaxLoadsPerFrame: 100, ChunkSize: Chunk,
                        Async: false), Loaded);
                Streamer.PrimeAround(Vector3.Zero);
            }

            /// <summary>What <see cref="MapEditorScene"/> does with a pending edit: an exclusion or override batch
            /// takes the props-only path, which must accept it.</summary>
            public void Sync()
            {
                if (!Editor.WorldRebuildPending) return;
                RectArea dirty = Assert.IsType<RectArea>(Editor.PendingRebuildRegion);
                Assert.True(Editor.PendingLayerConfigRefresh);
                Assert.False(Editor.PendingFieldChange);
                Assert.True(ViewportWorld.RefreshLayerProps(Sink, Streamer, Viewport.BuildSinkLayers(Editor.Doc), dirty));
                Editor.AcknowledgeWorldRebuild();
            }

            public Dictionary<ChunkCoord, PropPlacement[][]> Snapshot() => Loaded.Loads.ToDictionary(
                kv => kv.Key, kv => kv.Value.LayerProps.Select(l => l.ToArray()).ToArray());

            /// <summary>Every loaded chunk against the oracle, layer by layer.</summary>
            public void AssertMatchesFullRebuild()
            {
                IReadOnlyList<PropLayer> layers = Viewport.BuildSinkLayers(Editor.Doc);
                TerrainField field = MapRuntime.BuildField(Editor.Doc, Editor.Registry);
                Assert.NotEmpty(Loaded.Loads);
                foreach ((ChunkCoord coord, Scene3DChunkSink.ChunkLoad load) in Loaded.Loads)
                {
                    IReadOnlyList<PropPlacement>[] expected = FullRebuild(layers, field, coord);
                    Assert.Equal(expected.Length, load.LayerProps.Length);
                    for (int i = 0; i < expected.Length; i++)
                        Assert.True(expected[i].SequenceEqual(load.LayerProps[i]),
                            $"chunk {coord} layer {i}: {load.LayerProps[i].Count} props where a full rebuild has {expected[i].Count}");
                }
            }
        }

        sealed class Chunks(Scene3DChunkSink sink) : IChunkPropRefreshSink
        {
            public readonly Dictionary<ChunkCoord, Scene3DChunkSink.ChunkLoad> Loads = new();
            public int TerrainBuilds;
            public readonly List<ChunkCoord> Refreshed = new();

            public object Load(ChunkCoord coord, int lod, ChunkRing ring)
            {
                TerrainBuilds++;
                var load = new Scene3DChunkSink.ChunkLoad { Lod = lod, Ring = ring, Region = ChunkGrid.RegionOf(coord, Chunk) };
                sink.RefreshProps(coord, load);
                return Loads[coord] = load;
            }

            public void ReLod(ChunkCoord coord, object handle, int lod, ChunkRing ring)
            {
                TerrainBuilds++;
                sink.RefreshProps(coord, handle);
            }

            public void Unload(ChunkCoord coord, object handle) => Loads.Remove(coord);

            public void RefreshProps(ChunkCoord coord, object handle)
            {
                Refreshed.Add(coord);
                sink.RefreshProps(coord, handle);
            }
        }

        // The oracle: a chunk's layers the way a fresh build derives them, from the public scatter API.
        static IReadOnlyList<PropPlacement>[] FullRebuild(IReadOnlyList<PropLayer> layers, TerrainField field, ChunkCoord coord)
        {
            RectArea area = ChunkGrid.AreaOf(coord, Chunk);
            var result = new IReadOnlyList<PropPlacement>[layers.Count];
            for (int i = 0; i < layers.Count; i++)
            {
                if (layers[i].PlacementSource is { } source)
                {
                    var into = new List<PropPlacement>();
                    source.PlacementsIn(area, into);
                    result[i] = into;
                }
                else if (layers[i].Scatter is { } scatter)
                {
                    result[i] = PropScatter.Generate(field, scatter, area);
                }
            }
            for (int i = 0; i < layers.Count; i++)
                if (layers[i].Companions is { } companions)
                    result[i] = PropScatter.GenerateCompanions(field, result[layers[i].HostLayerIndex], companions);
            return result;
        }

        static void AssertSame(Dictionary<ChunkCoord, PropPlacement[][]> expected, DeviceFreeWorld world)
        {
            Dictionary<ChunkCoord, PropPlacement[][]> actual = world.Snapshot();
            Assert.Equal(expected.Keys.OrderBy(c => (c.X, c.Z)), actual.Keys.OrderBy(c => (c.X, c.Z)));
            foreach ((ChunkCoord coord, PropPlacement[][] layers) in expected)
                for (int i = 0; i < layers.Length; i++)
                    Assert.True(layers[i].SequenceEqual(actual[coord][i]), $"chunk {coord} layer {i} differs");
        }

        static int TotalProps(Dictionary<ChunkCoord, PropPlacement[][]> snapshot) =>
            snapshot.Values.Sum(layers => layers.Sum(l => l.Length));

        static EditorToolController Controller(DeviceFreeWorld world) => new(world.Editor)
        {
            Field = MapRuntime.BuildField(world.Editor.Doc, world.Editor.Registry),
            HeightOf = static _ => 2f,
            GizmoScale = 1f,
        };

        // One drag frame, then the scene's rebuild check, then the full-rebuild comparison. Chunks the frame's dirty
        // rect does not reach must keep the very arrays they had, which is what makes the comparison bite.
        static void Frame(DeviceFreeWorld world, EditorToolController controller, EditorFrameInput input)
        {
            Dictionary<ChunkCoord, IReadOnlyList<PropPlacement>[]> before =
                world.Loaded.Loads.ToDictionary(kv => kv.Key, kv => kv.Value.LayerProps);
            world.Loaded.Refreshed.Clear();
            controller.Update(input);
            world.Sync();
            foreach ((ChunkCoord coord, IReadOnlyList<PropPlacement>[] layers) in before)
                if (!world.Loaded.Refreshed.Contains(coord))
                    Assert.Same(layers, world.Loaded.Loads[coord].LayerProps);
            world.AssertMatchesFullRebuild();
        }

        [Fact]
        public void ExclusionGizmoDrag_PropsOnlyPartial_MatchesAFullRebuild_EveryFrame_AndThroughUndoRedo()
        {
            var world = new DeviceFreeWorld(Doc());
            world.AssertMatchesFullRebuild();
            Dictionary<ChunkCoord, PropPlacement[][]> preDrag = world.Snapshot();
            int terrainBuilds = world.Loaded.TerrainBuilds;
            EditorToolController controller = Controller(world);
            world.Editor.Selection.Set(SelectionKind.Exclusion, "0");

            // Grab the +Z arrow at the rect centre (38.75, 25) and drag it 12 m along +Z in 1.5 m steps.
            var centre = new Vector3(38.75f, 100f, 25f);
            controller.Update(new EditorFrameInput(centre + new Vector3(0f, 0f, 0.6f), Down, pointerPressed: true,
                pointerDown: true, dt: 0.016f));
            Assert.True(controller.IsDragging);
            var touched = new HashSet<ChunkCoord>();
            for (int i = 1; i <= 8; i++)
            {
                Frame(world, controller, new EditorFrameInput(centre + new Vector3(0f, 0f, 0.6f + 1.5f * i), Down,
                    pointerDown: true, dt: 0.016f));
                touched.UnionWith(world.Loaded.Refreshed);
            }
            Frame(world, controller, new EditorFrameInput(centre + new Vector3(0f, 0f, 12.6f), Down,
                pointerReleased: true, dt: 0.016f));

            var rect = Assert.IsType<RectShapeDoc>(world.Editor.Doc.Exclusions[0].Shape);
            Assert.InRange(rect.MinZ, 16.9f, 17.1f);
            Assert.InRange(rect.MaxZ, 56.9f, 57.1f);
            Assert.Equal(1, world.Editor.History.UndoDepth);
            Assert.Contains(new ChunkCoord(1, 0), touched);
            Assert.True(touched.Count < world.Loaded.Loads.Count, "every chunk was refreshed, so nothing was partial");
            Assert.Equal(terrainBuilds, world.Loaded.TerrainBuilds);   // no chunk re-meshed its terrain
            Dictionary<ChunkCoord, PropPlacement[][]> postDrag = world.Snapshot();
            Assert.NotEqual(TotalProps(preDrag), TotalProps(postDrag));

            // The margin mattered: a chunk no bare-margin rect of the drag reaches still changed its trees, so a
            // refresh without the jitter margin would have left it stale and failed the comparisons above.
            Assert.Contains(postDrag.Keys, c => c != new ChunkCoord(0, 0)
                && !preDrag[c][0].SequenceEqual(postDrag[c][0]));

            Assert.True(world.Editor.Undo());
            world.Sync();
            world.AssertMatchesFullRebuild();
            AssertSame(preDrag, world);

            Assert.True(world.Editor.Redo());
            world.Sync();
            world.AssertMatchesFullRebuild();
            AssertSame(postDrag, world);
            Assert.Equal(terrainBuilds, world.Loaded.TerrainBuilds);
        }

        [Fact]
        public void ScatterOverrideGizmoScale_PropsOnlyPartial_MatchesAFullRebuild_EveryFrame_AndThroughUndoRedo()
        {
            var world = new DeviceFreeWorld(Doc());
            Dictionary<ChunkCoord, PropPlacement[][]> preDrag = world.Snapshot();
            int terrainBuilds = world.Loaded.TerrainBuilds;
            EditorToolController controller = Controller(world);
            world.Editor.Selection.Set(SelectionKind.ScatterOverride, "0");

            // Grab the corner scale cube, 0.85 m out on both axes from the disc centre (-20, -40), and pull it out
            // to 1.9 times that distance in steps, growing the disc until its padded bounds reach past the z = -60
            // chunk seam.
            var centre = new Vector3(-20f, 100f, -40f);
            controller.Update(new EditorFrameInput(centre + new Vector3(0.85f, 0f, 0.85f), Down, pointerPressed: true,
                pointerDown: true, dt: 0.016f));
            Assert.True(controller.IsDragging);
            for (int i = 1; i <= 6; i++)
            {
                float f = 1f + 0.15f * i;
                Frame(world, controller, new EditorFrameInput(centre + new Vector3(0.85f * f, 0f, 0.85f * f), Down,
                    pointerDown: true, dt: 0.016f));
            }
            Frame(world, controller, new EditorFrameInput(centre + new Vector3(0.85f * 1.9f, 0f, 0.85f * 1.9f), Down,
                pointerReleased: true, dt: 0.016f));

            var disc = Assert.IsType<DiscShapeDoc>(world.Editor.Doc.ScatterOverrides[0].Shape);
            Assert.InRange(disc.Radius, 18.9f, 19.1f);
            Assert.Equal(terrainBuilds, world.Loaded.TerrainBuilds);
            Dictionary<ChunkCoord, PropPlacement[][]> postDrag = world.Snapshot();
            Assert.NotEqual(TotalProps(preDrag), TotalProps(postDrag));

            Assert.True(world.Editor.Undo());
            world.Sync();
            world.AssertMatchesFullRebuild();
            AssertSame(preDrag, world);

            Assert.True(world.Editor.Redo());
            world.Sync();
            world.AssertMatchesFullRebuild();
            AssertSame(postDrag, world);
        }

        [Fact]
        public void EveryExclusionAndOverrideCommand_LeavesTheFieldAlone_OtherWorldEditsDoNot()
        {
            var shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 3f };
            var value = new MapScatterOverrideDoc { Shape = shape, DensityMultiplier = 0.5f };
            EditorCommand[] layerOnly =
            {
                new AddExclusionCommand(new MapExclusion { Shape = shape }),
                new RemoveExclusionCommand(0),
                new EditExclusionShapeCommand(0, shape, shape),
                new EditExclusionLayersCommand(0, new List<string> { "trees" }, null),
                new AddScatterOverrideCommand(value),
                new RemoveScatterOverrideCommand(0),
                new EditScatterOverrideShapeCommand(0, shape, shape),
                new EditScatterOverrideValuesCommand(0, value, value),
                new ReorderScatterOverrideCommand(0, 1),
            };
            foreach (EditorCommand command in layerOnly)
            {
                Assert.True(command.AffectsWorld, command.Label);
                Assert.True(command.RefreshesLayerConfig, command.Label);
                Assert.False(command.ChangesField, command.Label);
            }

            EditorCommand[] fieldEdits =
            {
                new AddFeatureCommand(new FlattenFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f }),
                new EditTerrainCommand(newWaterLevel: 2f, oldWaterLevel: 0f),
            };
            foreach (EditorCommand command in fieldEdits) Assert.True(command.ChangesField, command.Label);
        }

        [Fact]
        public void PendingFieldChange_FollowsTheWholeBatch_AndAcknowledgeClearsIt()
        {
            var editor = new EditorDocument(Doc());
            var moved = new DiscShapeDoc { CenterX = 54f, CenterZ = 30f, Radius = 8f };
            editor.Execute(new EditExclusionShapeCommand(0, moved, editor.Doc.Exclusions[0].Shape!));
            Assert.IsType<RectArea>(editor.PendingRebuildRegion);
            Assert.True(editor.PendingLayerConfigRefresh);
            Assert.False(editor.PendingFieldChange);

            // A feature edit landing in the same batch changes the field, so the batch must re-mesh.
            editor.Execute(new AddFeatureCommand(new FlattenFeatureDoc { CenterX = 50f, CenterZ = 30f, Radius = 5f }));
            Assert.IsType<RectArea>(editor.PendingRebuildRegion);
            Assert.True(editor.PendingFieldChange);

            editor.AcknowledgeWorldRebuild();
            Assert.False(editor.PendingFieldChange);

            // Undo replays the undone command's classification: undoing the feature changes the field again, undoing
            // the exclusion does not.
            Assert.True(editor.Undo());
            Assert.True(editor.PendingFieldChange);
            editor.AcknowledgeWorldRebuild();
            Assert.True(editor.Undo());
            Assert.False(editor.PendingFieldChange);
            Assert.True(editor.PendingLayerConfigRefresh);
        }

        [Fact]
        public void AnUnsafeLayerSwap_IsDeclined_WithoutTouchingTheSinkOrAnyChunk()
        {
            var world = new DeviceFreeWorld(Doc());
            Dictionary<ChunkCoord, PropPlacement[][]> before = world.Snapshot();

            // Re-host the companion onto the second scatter layer: a chunk the refresh does not reach would keep
            // companions derived from the old host, so the props-only path must refuse the list outright.
            world.Editor.Doc.CompanionLayers[0].HostLayer = "grass";
            IReadOnlyList<PropLayer> rehosted = world.Viewport.BuildSinkLayers(world.Editor.Doc);
            Assert.False(ViewportWorld.RefreshLayerProps(world.Sink, world.Streamer, rehosted,
                new RectArea(40f, 20f, 70f, 40f)));
            Assert.Empty(world.Loaded.Refreshed);
            world.Editor.Doc.CompanionLayers[0].HostLayer = "trees";
            world.AssertMatchesFullRebuild();   // the sink still serves the original list
            AssertSame(before, world);

            // A layer-count change is refused the same way, and in the editor it never gets this far: the command
            // reports no bounded region, so the batch takes the full rebuild.
            world.Editor.Execute(new EditExclusionShapeCommand(0,
                new DiscShapeDoc { CenterX = 40f, CenterZ = 30f, Radius = 8f }, world.Editor.Doc.Exclusions[0].Shape!));
            world.Editor.Execute(new AddScatterLayerCommand(Layer("rocks", 9f, 1f, 0.3f, "rock_a")));
            Assert.True(world.Editor.PendingFullRebuild);
            Assert.Null(world.Editor.PendingRebuildRegion);
            Assert.False(ViewportWorld.RefreshLayerProps(world.Sink, world.Streamer,
                world.Viewport.BuildSinkLayers(world.Editor.Doc), new RectArea(30f, 20f, 70f, 40f)));
            Assert.Empty(world.Loaded.Refreshed);
        }

        [Fact]
        public void RefreshLayerProps_BeforeBuild_ReturnsFalse_AndAfterDispose_Throws()
        {
            var world = new ViewportWorld(null!, Array.Empty<string>());
            Assert.False(world.RefreshLayerProps(new MapDocument(), new RectArea(0f, 0f, 1f, 1f)));
            world.Dispose();
            Assert.Throws<ObjectDisposedException>(() => world.RefreshLayerProps(new MapDocument(), new RectArea(0f, 0f, 1f, 1f)));
        }
    }
}
