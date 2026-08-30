using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the GPU-free surface of <see cref="ViewportWorld"/>: manifest parsing into
    /// <see cref="ViewportWorld.KindHeights"/>, the Build/Rebuild/Dispose state guards (which throw BEFORE any GPU
    /// call, so they are reachable without a device), placement-cache invalidation on
    /// <see cref="EditorDocument.DocumentChanged"/>, and the selected/unselected draw partition. The GPU path
    /// (mesh upload, sink construction, streaming, draws) is deliberately untested here (no GpuFact in v1); the
    /// Showcase room in Task 8 is the manual verification. <see cref="ViewportWorld"/> never touches its
    /// Scene3D before <see cref="ViewportWorld.Build"/>, so a null scene is a valid headless fixture for
    /// everything below.</summary>
    public class ViewportWorldTests
    {
        // A flat field at y = 0 everywhere (single default meadow band, gentle roll zeroed), matching
        // EditorPickingTests, so ground-snap arithmetic stays exact.
        static TerrainField FlatField() => new TerrainField(new TerrainConfig { GentleAmplitude = 0f });

        // Writes a manifest to a throwaway temp file and returns its path. ViewportWorld parses manifests
        // eagerly in its ctor, so callers delete the file straight after construction.
        static string WriteManifest(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"ke-viewport-{Guid.NewGuid():N}.manifest.json");
            File.WriteAllText(path, json);
            return path;
        }

        // Writes a manifest under an EXPLICIT file name (its own fresh temp directory, so two calls with the same
        // name never collide) so a test can assert the KindCategories manifest-stem fallback, e.g.
        // "props.manifest.json" -> "props". The caller deletes the whole directory when done.
        static string WriteManifestNamed(string fileName, string json)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-viewport-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, fileName);
            File.WriteAllText(path, json);
            return path;
        }

        static ViewportWorld Construct(params string[] jsons)
        {
            var paths = new List<string>(jsons.Length);
            foreach (string j in jsons) paths.Add(WriteManifest(j));
            try { return new ViewportWorld(null!, paths); }
            finally { foreach (string p in paths) File.Delete(p); }
        }

        const string TwoPropManifest =
            "{ \"props\": [ " +
            "{ \"id\": \"hut\", \"file\": \"hut.glb\", \"heightMeters\": 3.0 }, " +
            "{ \"id\": \"rock\", \"file\": \"rock.glb\", \"heightMeters\": 1.0 } ] }";

        // ---- KindHeights parsing -----------------------------------------------------------------------

        [Fact]
        public void KindHeights_ParsesHeightMetersById()
        {
            ViewportWorld vw = Construct(TwoPropManifest);

            Assert.Equal(2, vw.KindHeights.Count);
            Assert.Equal(3f, vw.KindHeights["hut"]);
            Assert.Equal(1f, vw.KindHeights["rock"]);
        }

        [Fact]
        public void KindHeights_FirstManifestWins()
        {
            const string second =
                "{ \"props\": [ { \"id\": \"hut\", \"file\": \"hut2.glb\", \"heightMeters\": 9.0 }, " +
                "{ \"id\": \"tree\", \"file\": \"tree.glb\", \"heightMeters\": 12.0 } ] }";
            ViewportWorld vw = Construct(TwoPropManifest, second);

            Assert.Equal(3, vw.KindHeights.Count);
            Assert.Equal(3f, vw.KindHeights["hut"]);   // the FIRST manifest wins, matching the mesh tiebreak
            Assert.Equal(1f, vw.KindHeights["rock"]);
            Assert.Equal(12f, vw.KindHeights["tree"]);
        }

        [Fact]
        public void KindHeights_EmptyManifestList_IsEmpty()
        {
            var vw = new ViewportWorld(null!, Array.Empty<string>());
            Assert.Empty(vw.KindHeights);
        }

        // ---- KindCategories parsing ---------------------------------------------------------------------

        [Fact]
        public void KindCategories_FallbackToManifestStem_AndFirstWins()
        {
            string first = WriteManifestNamed("props.manifest.json",
                "{ \"props\": [ { \"id\": \"pine_a\", \"file\": \"pine_a.glb\", \"heightMeters\": 12.0 }, " +
                "{ \"id\": \"hut\", \"file\": \"hut.glb\", \"heightMeters\": 3.0, \"category\": \"buildings\" } ] }");
            string second = WriteManifestNamed("groundcover.manifest.json",
                "{ \"props\": [ { \"id\": \"hut\", \"file\": \"hut2.glb\", \"heightMeters\": 9.0, \"category\": \"structures\" }, " +
                "{ \"id\": \"grass_a\", \"file\": \"grass_a.glb\", \"heightMeters\": 0.3 } ] }");
            try
            {
                var vw = new ViewportWorld(null!, new[] { first, second });

                Assert.Equal(3, vw.KindCategories.Count);
                Assert.Equal("props", vw.KindCategories["pine_a"]);        // no "category" -> falls back to the manifest file stem
                Assert.Equal("buildings", vw.KindCategories["hut"]);       // explicit category, and the FIRST manifest wins over "structures"
                Assert.Equal("groundcover", vw.KindCategories["grass_a"]); // "groundcover.manifest.json" -> "groundcover"
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(first)!, true);
                Directory.Delete(Path.GetDirectoryName(second)!, true);
            }
        }

        [Fact]
        public void KindCategories_EmptyManifestList_IsEmpty()
        {
            var vw = new ViewportWorld(null!, Array.Empty<string>());
            Assert.Empty(vw.KindCategories);
        }

        [Fact]
        public void Ctor_NullManifestPaths_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new ViewportWorld(null!, null!));
        }

        // ---- initial (unbuilt) state -------------------------------------------------------------------

        [Fact]
        public void BeforeBuild_IsNotBuiltAndFieldIsNull()
        {
            ViewportWorld vw = Construct(TwoPropManifest);

            Assert.False(vw.IsBuilt);
            Assert.Null(vw.Field);
        }

        [Fact]
        public void Update_BeforeBuild_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.Throws<InvalidOperationException>(() => vw.Update(Vector3.Zero, 0.016f));
        }

        [Fact]
        public void Draw_BeforeBuild_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.Throws<InvalidOperationException>(() => vw.Draw(Vector3.Zero, null, default, new EditorVisibility()));
        }

        [Fact]
        public void Rebuild_BeforeBuild_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.Throws<InvalidOperationException>(() => vw.Rebuild(new MapDocument(), MapDocRegistry.CreateDefault()));
        }

        [Fact]
        public void PartialRebuild_BeforeBuild_ReturnsFalse()
        {
            // Unlike Rebuild (which throws), the partial path reports "not built" so the scene falls back to a full
            // rebuild rather than crashing.
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.False(vw.PartialRebuild(new MapDocument(), MapDocRegistry.CreateDefault(), new RectArea(0f, 0f, 1f, 1f)));
        }

        // ---- partial rebuild vs. the prop layers' captured scatter configs ------------------------------

        // A document whose one scatter layer covers everything: a flat meadow field (GentleAmplitude zeroed, the
        // FlatField() recipe expressed as a document), density 1 and no jitter, so PropScatter.Generate places one
        // prop on every 4 m cell of the area below and the counts are exact rather than statistical.
        static MapDocument ScatterDoc()
        {
            var doc = new MapDocument
            {
                Id = "scatter-partial",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
                Terrain = new MapTerrain { GentleAmplitude = 0f },
            };
            doc.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                CellSize = 4f,
                Jitter = 0f,
                Rules =
                {
                    new MapBiomeScatterRule
                    {
                        Biome = BiomeId.Meadow,
                        Density = 1f,
                        Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } },
                    },
                },
            });
            return doc;
        }

        [Fact]
        public void ExclusionEdit_TakesTheFullRebuild_BecausePartialKeepsTheCapturedScatterConfigs()
        {
            // Issue #765. PartialRebuild swaps the field and re-meshes the dirty chunks, and the sink re-scatters
            // each of them from the ScatterConfig captured inside its PropLayer at Build time
            // (Scene3DChunkSink.ScatterLayersFor reads _layers[i].Scatter, and the sink has no layer setter). An
            // exclusion lives in that captured config (MapRuntime.BuildScatterConfig fills Exclusions), and it
            // changes no terrain at all, so a partial rebuild after drawing one regenerates BYTE-IDENTICAL props
            // and every tree under the new exclusion keeps standing. Only a full Rebuild, which reruns
            // BuildPropLayers, clears them. So the command must not report a bounded region.
            MapDocument doc = ScatterDoc();
            MapDocRegistry registry = MapDocRegistry.CreateDefault();
            var area = new RectArea(-20f, -20f, 20f, 20f);

            // What ViewportWorld.Build hands the sink: the layer configs as the document reads right now.
            ScatterConfig captured = MapRuntime.BuildScatterConfigs(doc)["trees"];
            Assert.NotEmpty(PropScatter.Generate(MapRuntime.BuildField(doc, registry), captured, area));

            // Draw an exclusion over the whole area through the real command path.
            var editor = new EditorDocument(doc, registry);
            editor.Execute(new AddExclusionCommand(new MapExclusion
            {
                Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 40f },
            }));

            // The field PartialRebuild would swap in is unchanged by an exclusion (it is a scatter input, not a
            // terrain one), so the re-meshed chunks re-scatter the same props off the stale captured config...
            TerrainField after = MapRuntime.BuildField(doc, registry);
            Assert.NotEmpty(PropScatter.Generate(after, captured, area));
            // ...while the configs a full rebuild would construct place nothing inside the exclusion.
            Assert.Empty(PropScatter.Generate(after, MapRuntime.BuildScatterConfigs(doc)["trees"], area));

            // Which is why the edit has to route to the full rebuild: pending with a NULL region.
            Assert.True(editor.WorldRebuildPending);
            Assert.Null(editor.PendingRebuildRegion);
        }

        // ---- after dispose (never built) ---------------------------------------------------------------

        [Fact]
        public void AfterDispose_MethodsThrowObjectDisposed()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            vw.Dispose();

            MapDocument doc = new();
            MapDocRegistry registry = MapDocRegistry.CreateDefault();
            Assert.Throws<ObjectDisposedException>(() => vw.Build(doc, registry));
            Assert.Throws<ObjectDisposedException>(() => vw.Rebuild(doc, registry));
            Assert.Throws<ObjectDisposedException>(() => vw.PartialRebuild(doc, registry, new RectArea(0f, 0f, 1f, 1f)));
            Assert.Throws<ObjectDisposedException>(() => vw.Update(Vector3.Zero, 0.016f));
            Assert.Throws<ObjectDisposedException>(() => vw.Draw(Vector3.Zero, null, default, new EditorVisibility()));
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            vw.Dispose();
            vw.Dispose();   // a second dispose must not throw
        }

        [Fact]
        public void InvalidatePlacements_AfterDispose_IsSafeNoOp()
        {
            // The scene wires DocumentChanged -> InvalidatePlacements; a late event after teardown must not throw.
            ViewportWorld vw = Construct(TwoPropManifest);
            vw.Dispose();
            vw.InvalidatePlacements();
        }

        [Fact]
        public void InvalidateKitMeshes_BeforeBuild_DoesNotThrow()
        {
            // Callable any time except after Dispose, including before the first Build: the cache is empty and the
            // splat material was never loaded, so this must not touch the (null) scene.
            ViewportWorld vw = Construct(TwoPropManifest);
            vw.InvalidateKitMeshes();
        }

        [Fact]
        public void InvalidateKitMeshes_AfterDispose_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            vw.Dispose();
            Assert.Throws<ObjectDisposedException>(() => vw.InvalidateKitMeshes());
        }

        // ---- selected/unselected partition -------------------------------------------------------------

        static EditorPlacement Ep(string id, string kind) =>
            new EditorPlacement(id, new PropPlacement(kind, 0f, 0f, 0f, 1f, 0f, 0));

        [Fact]
        public void Partition_NullSelectedId_AllUnselectedNoneSelected()
        {
            var placements = new List<EditorPlacement> { Ep("a", "hut"), Ep("b", "rock") };

            (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) =
                ViewportWorld.Partition(placements, null);

            Assert.Null(selected);
            Assert.Equal(2, unselected.Count);
            Assert.Equal("a", unselected[0].Id);
            Assert.Equal("b", unselected[1].Id);
        }

        [Fact]
        public void Partition_MatchingId_PullsThatOneOutAndKeepsOrder()
        {
            var placements = new List<EditorPlacement> { Ep("a", "hut"), Ep("b", "rock"), Ep("c", "tree") };

            (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) =
                ViewportWorld.Partition(placements, "b");

            Assert.NotNull(selected);
            Assert.Equal("b", selected!.Value.Id);
            Assert.Equal(2, unselected.Count);
            Assert.Equal("a", unselected[0].Id);   // order of the remaining is preserved
            Assert.Equal("c", unselected[1].Id);
        }

        [Fact]
        public void Partition_UnknownId_AllUnselectedNoneSelected()
        {
            var placements = new List<EditorPlacement> { Ep("a", "hut"), Ep("b", "rock") };

            (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) =
                ViewportWorld.Partition(placements, "zzz");

            Assert.Null(selected);
            Assert.Equal(2, unselected.Count);
        }

        [Fact]
        public void Partition_DuplicateId_OnlyFirstIsSelected()
        {
            // Ids are unique in a real document; the guard keeps the partition total (every input lands in exactly
            // one output) even if a caller passes a duplicate.
            var placements = new List<EditorPlacement> { Ep("a", "hut"), Ep("a", "rock") };

            (IReadOnlyList<EditorPlacement> unselected, EditorPlacement? selected) =
                ViewportWorld.Partition(placements, "a");

            Assert.NotNull(selected);
            Assert.Equal("hut", selected!.Value.Prop.Id);   // the first "a" is the selected one
            Assert.Single(unselected);
            Assert.Equal("rock", unselected[0].Prop.Id);
        }

        // ---- water plane derivation --------------------------------------------------------------------

        [Fact]
        public void WaterPlane_CentresOnTheCameraAtTheProfileExtent()
        {
            // Asymmetric view position so an X/Z swap cannot hide. The plane follows the CAMERA (not the document
            // bounds) at the profile's ocean half-extent, which is what keeps its rim outside the frustum on a
            // document smaller than the far clip.
            var viewPos = new Vector3(37f, 12f, -215f);

            WaterPlane plane = ViewportWorld.BuildWaterPlane(viewPos, -1.2f, RenderDistanceProfile.Default.OceanHalfExtent);

            Assert.Equal(37f, plane.CenterX);       // camera X, ignoring the document footprint
            Assert.Equal(-215f, plane.CenterZ);     // camera Z
            Assert.Equal(-1.2f, plane.SurfaceY);    // the water level maps straight to the surface height
            Assert.Equal(600f, plane.HalfExtentX);  // the Far tier's OceanHalfExtent, square footprint
            Assert.Equal(600f, plane.HalfExtentZ);
        }

        [Fact]
        public void WaterPlane_RimSitsPastTheFarClipButInsideTheStreamedTerrain()
        {
            // The coherence the profile exists to guarantee, checked through the editor's own plane rather than on
            // the profile alone: the nearest point of the rim is past the camera's far clip (so it never reads as a
            // wall of water) and still inside the streamed far field (so the sea is never drawn over a void).
            RenderDistanceProfile p = RenderDistanceProfile.Default;

            WaterPlane plane = ViewportWorld.BuildWaterPlane(Vector3.Zero, 0f, p.OceanHalfExtent);

            Assert.True(plane.HalfExtentX > p.FarClip, "the ocean rim must clip out rather than be visible");
            Assert.True(plane.HalfExtentX <= p.DecorRadiusMeters, "the ocean must sit over resident terrain");
        }

        [Fact]
        public void RenderDistance_RejectsAnIncoherentProfile()
        {
            // A hand-rolled profile whose ocean rim falls inside the frustum must fail at assignment (editor start),
            // not by rendering a slab of water with a visible lip. ViewportWorld touches no GPU before Build, so the
            // guard is reachable with a null scene.
            var vw = new ViewportWorld(null!, Array.Empty<string>());
            var incoherent = RenderDistanceProfile.Default with { OceanHalfExtent = 100f };

            Assert.Throws<ArgumentOutOfRangeException>(() => vw.RenderDistance = incoherent);
            Assert.Equal(RenderDistanceProfile.Default, vw.RenderDistance);   // the bad set never took effect
        }

        [Fact]
        public void RenderDistance_DefaultsToTheFarTier()
        {
            var vw = new ViewportWorld(null!, Array.Empty<string>());
            Assert.Equal(RenderDistanceProfile.For(RenderDistanceTier.Far), vw.RenderDistance);
        }

        // ---- placement cache ---------------------------------------------------------------------------

        static MapDocument DocWith(params MapPlacement[] placements)
        {
            var doc = new MapDocument { Id = "cache-zone" };
            foreach (MapPlacement p in placements) doc.Placements.Add(p);
            return doc;
        }

        [Fact]
        public void PlacementCache_StartsDirty()
        {
            var cache = new PlacementCache();
            Assert.True(cache.IsDirty);
        }

        [Fact]
        public void PlacementCache_Get_PairsStableIdWithBuiltProp_AndGroundSnaps()
        {
            var cache = new PlacementCache();
            TerrainField field = FlatField();
            MapDocument doc = DocWith(
                new MapPlacement { Id = "hut-1", Kind = "hut", X = 4f, Z = -2f },              // null Y -> ground-snap
                new MapPlacement { Id = "rock-1", Kind = "rock", X = 1f, Z = 1f, Y = 5f });    // explicit Y kept

            IReadOnlyList<EditorPlacement> built = cache.Get(doc, field);

            Assert.False(cache.IsDirty);
            Assert.Equal(2, built.Count);
            // Stable document id is preserved separately from the kit id the renderer instances.
            Assert.Equal("hut-1", built[0].Id);
            Assert.Equal("hut", built[0].Prop.Id);
            Assert.Equal(0f, built[0].Prop.Y);   // flat field ground-snap
            Assert.Equal("rock-1", built[1].Id);
            Assert.Equal(5f, built[1].Prop.Y);   // explicit Y wins over the field
        }

        [Fact]
        public void PlacementCache_ServesStaleUntilInvalidated()
        {
            var cache = new PlacementCache();
            TerrainField field = FlatField();
            MapDocument doc = DocWith(new MapPlacement { Id = "hut-1", Kind = "hut" });

            Assert.Single(cache.Get(doc, field));

            // Mutating the document behind the cache's back does NOT change what Get returns until invalidation.
            doc.Placements.Add(new MapPlacement { Id = "hut-2", Kind = "hut" });
            Assert.Single(cache.Get(doc, field));

            cache.Invalidate();
            Assert.True(cache.IsDirty);
            Assert.Equal(2, cache.Get(doc, field).Count);   // rebuilt from the mutated document
        }

        // ---- visibility: scatter-layer rebuild filter --------------------------------------------------

        [Fact]
        public void HiddenScatterLayer_ExcludedFromRebuild()
        {
            // VisibleScatterLayerNames is the seam BuildPropLayers uses to decide which scatter prop layers a
            // Build / Rebuild constructs, so pinning it pins that a hidden layer drops out of the rebuilt world.
            var doc = new MapDocument { Id = "layers" };
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "rocks" });
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "flowers" });

            var vis = new EditorVisibility();
            // All visible: every layer's props are built, in document order.
            Assert.Equal(new[] { "trees", "rocks", "flowers" },
                ViewportWorld.VisibleScatterLayerNames(doc, vis.GetLayer).ToArray());

            // Hide one: it drops out of the rebuilt prop layers, and the order of the rest is preserved.
            vis.SetLayer("rocks", false);
            Assert.Equal(new[] { "trees", "flowers" },
                ViewportWorld.VisibleScatterLayerNames(doc, vis.GetLayer).ToArray());

            // Hiding every layer yields none (the sink still gets its one empty fallback layer, built separately).
            vis.SetLayer("trees", false);
            vis.SetLayer("flowers", false);
            Assert.Empty(ViewportWorld.VisibleScatterLayerNames(doc, vis.GetLayer));
        }

        [Fact]
        public void FilterVisiblePlacements_DropsHiddenAndRespectsGroup()
        {
            var placements = new List<EditorPlacement> { Ep("a", "hut"), Ep("b", "rock"), Ep("c", "tree") };
            var vis = new EditorVisibility();

            // Nothing hidden: the SAME list instance comes back (the fast path, no needless copy).
            Assert.Same(placements, ViewportWorld.FilterVisiblePlacements(placements, vis));

            // Hide the middle one: order preserved, only "b" dropped.
            vis.SetElementHidden(SelectionKind.Placement, "b", true);
            IReadOnlyList<EditorPlacement> kept = ViewportWorld.FilterVisiblePlacements(placements, vis);
            Assert.Equal(new[] { "a", "c" }, kept.Select(k => k.Id).ToArray());

            // Hide the whole Placements group: nothing draws.
            vis.SetGroup(VisibilityGroup.Placements, false);
            Assert.Empty(ViewportWorld.FilterVisiblePlacements(placements, vis));
        }

        [Fact]
        public void PlacementCache_InvalidatedByDocumentChanged()
        {
            // Mirrors the Task 8 scene wiring: EditorDocument.DocumentChanged -> cache.Invalidate. An edit through
            // the command choke point invalidates the cache, so the next Get reflects the new placement.
            var cache = new PlacementCache();
            TerrainField field = FlatField();
            var doc = new MapDocument { Id = "wired" };
            doc.Placements.Add(new MapPlacement { Id = "hut-1", Kind = "hut" });
            var edoc = new EditorDocument(doc);
            edoc.DocumentChanged += cache.Invalidate;

            Assert.Single(cache.Get(doc, field));   // warm the cache
            Assert.False(cache.IsDirty);

            edoc.Execute(new AddPlacementCommand(new MapPlacement { Id = "hut-2", Kind = "hut" }));

            Assert.True(cache.IsDirty);
            Assert.Equal(2, cache.Get(doc, field).Count);
        }

        // ---- TexturedProps toggle: ResolvePropParts is the GPU-free decision LoadKitMeshes makes each rebuild -----

        // A single-material textured glb (red albedo + flat normal + packed metal-rough), the
        // GltfMaterialAutoReadTests fixture idiom reused here rather than a new PNG-embedding helper. One material,
        // so LoadPropAuto's textured branch and its flat branch both yield exactly one GltfMeshPart, differing only
        // in whether Maps is populated: the decisive, GPU-free signal for "loaded textured" vs "loaded flat".
        // The name mirrors the plan's binding test: with the editor's TexturedProps option off, a rebuild's
        // LoadKitMeshes loads the flat variant for a textured-flagged entry (asserted here via the internal
        // ResolvePropParts seam it calls). The "RebuildFires" half of the scenario, that flipping the Layers-panel
        // toggle actually triggers a ViewportWorld.Rebuild, is covered at the MapEditorScene level by
        // TexturedToggle_Flip_TriggersRebuild (this seam has no Scene3D, so it cannot build/rebuild a real world).
        [Fact]
        public void TexturedToggle_Off_LoadsFlat_RebuildFires()
        {
            string path = GltfTriangleFixtures.WriteTexturedTriangleGlb();
            try
            {
                var entry = new AssetEntry("hut", path, heightMeters: 2f, source: "", license: "", textured: true);

                // The option is off, so even though the entry itself declares Textured, ResolvePropParts must
                // still fall back to the flattened single-part form (the same one an untextured entry produces).
                IReadOnlyList<GltfMeshPart> parts = ViewportWorld.ResolvePropParts(entry, texturedProps: false);

                Assert.Single(parts);
                Assert.True(parts[0].Maps.IsEmpty);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TexturedToggle_DefaultTrue()
        {
            string path = GltfTriangleFixtures.WriteTexturedTriangleGlb();
            try
            {
                var entry = new AssetEntry("hut", path, heightMeters: 2f, source: "", license: "", textured: true);

                // MapEditorOptions.TexturedProps defaults true (matching gameplay): a textured entry loads its
                // textured parts, with maps present, when the option is left on.
                IReadOnlyList<GltfMeshPart> parts = ViewportWorld.ResolvePropParts(entry, texturedProps: true);

                Assert.Single(parts);
                Assert.False(parts[0].Maps.IsEmpty);
                Assert.NotNull(parts[0].Maps.Albedo);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TexturedToggle_UntexturedEntry_AlwaysFlat_RegardlessOfOption()
        {
            // An entry that never declared Textured must stay flat even when the option is on: the option can only
            // ever turn a textured entry's parts OFF, never turn an untextured entry's parts on.
            string path = GltfTriangleFixtures.WriteUntexturedTriangleGlb();
            try
            {
                var entry = new AssetEntry("rock", path, heightMeters: 1f, source: "", license: "", textured: false);

                IReadOnlyList<GltfMeshPart> parts = ViewportWorld.ResolvePropParts(entry, texturedProps: true);

                Assert.Single(parts);
                Assert.True(parts[0].Maps.IsEmpty);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TexturedPropsEnabled_DefaultsToAlwaysOn()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.True(vw.TexturedPropsEnabled());
        }

        [Fact]
        public void TexturedPropsEnabled_NullSetter_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.Throws<ArgumentNullException>(() => vw.TexturedPropsEnabled = null!);
        }
    }
}
