using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The props-only partial path (issue #771) on a real <see cref="ViewportWorld"/> over a headless device:
    /// an exclusion drag, its undo and its redo each take
    /// <see cref="ViewportWorld.RefreshLayerProps(MapDocument, RectArea)"/>, which keeps the field and asks the
    /// streamer for no terrain rebuild at all, while a feature edit on the same world still re-meshes through
    /// <see cref="ViewportWorld.PartialRebuild(MapDocument, MapDocRegistry, RectArea, bool)"/>.
    /// The per-chunk equivalence with a full rebuild is proven device-free in <see cref="LayerPropsRefreshTests"/>.
    /// A GpuFact because <see cref="ViewportWorld.Build"/> touches the GPU.
    /// <para>In <c>NativeDeviceLifecycle</c> because it builds a whole device beside the suite's own, see
    /// <see cref="NativeDeviceLifecycleCollection"/>.</para></summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class ViewportWorldPropsRefreshGpuTests
    {
        static MapDocument Doc()
        {
            var doc = new MapDocument
            {
                Id = "props-refresh-gpu",
                Bounds = new MapBounds { MinX = -120f, MinZ = -120f, MaxX = 120f, MaxZ = 120f },
            };
            doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Meadow, BaseHeight = 5f, HillAmplitude = 1f });
            var rule = new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 0.8f, Kinds = { new MapPropKind { Id = "pine_a" } } };
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees", CellSize = 4f, Jitter = 2f, Rules = { rule } });
            doc.CompanionLayers.Add(new MapCompanionLayer
            {
                Name = "understory", HostLayer = "trees", Kinds = { new MapPropKind { Id = "fern" } },
            });
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 10f, CenterZ = 10f, Radius = 12f } });
            return doc;
        }

        [GpuFact]
        public void ExclusionDrag_RefreshesPropsOnly_UndoRedoToo_AndAFeatureEditStillRemeshes()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);

            var editor = new EditorDocument(Doc());
            var world = new ViewportWorld(scene, Array.Empty<string>());
            editor.DocumentChanged += world.InvalidatePlacements;   // the scene's wiring
            world.Build(editor.Doc, editor.Registry);
            var visibility = new EditorVisibility();
            TerrainField field = world.Field!;
            long invalidates = world.Streamer!.BuildReasons.Invalidate;

            void Sync()
            {
                RectArea dirty = Assert.IsType<RectArea>(editor.PendingRebuildRegion);
                Assert.False(editor.PendingFieldChange);
                Assert.True(world.RefreshLayerProps(editor.Doc, dirty));
                editor.AcknowledgeWorldRebuild();
                world.Draw(new Vector3(0f, 20f, 0f), null, default, visibility);
            }

            for (int i = 1; i <= 6; i++)
            {
                MapShapeDoc live = editor.Doc.Exclusions[0].Shape!;
                editor.Execute(new EditExclusionShapeCommand(0,
                    new DiscShapeDoc { CenterX = 10f + 1.5f * i, CenterZ = 10f, Radius = 12f }, live));
                Sync();
            }
            editor.SealGesture();
            Assert.True(editor.Undo());
            Sync();
            Assert.True(editor.Redo());
            Sync();

            Assert.Equal(invalidates, world.Streamer.BuildReasons.Invalidate);
            Assert.Same(field, world.Field);

            // Control: a feature edit changes the field, so it re-meshes the chunks it covers.
            editor.Execute(new AddFeatureCommand(new FlattenFeatureDoc { CenterX = 10f, CenterZ = 10f, Radius = 6f, TargetHeight = 2f }));
            Assert.True(editor.PendingFieldChange);
            Assert.True(world.PartialRebuild(editor.Doc, editor.Registry, Assert.IsType<RectArea>(editor.PendingRebuildRegion),
                editor.PendingLayerConfigRefresh));
            Assert.True(world.Streamer.BuildReasons.Invalidate > invalidates);
            Assert.NotSame(field, world.Field);
            world.Dispose();
        }
    }
}
