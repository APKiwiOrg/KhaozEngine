using System;
using System.Collections.Generic;
using System.IO;
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
            Assert.Throws<InvalidOperationException>(() => vw.Draw(Vector3.Zero, null, default));
        }

        [Fact]
        public void Rebuild_BeforeBuild_Throws()
        {
            ViewportWorld vw = Construct(TwoPropManifest);
            Assert.Throws<InvalidOperationException>(() => vw.Rebuild(new MapDocument(), MapDocRegistry.CreateDefault()));
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
            Assert.Throws<ObjectDisposedException>(() => vw.Update(Vector3.Zero, 0.016f));
            Assert.Throws<ObjectDisposedException>(() => vw.Draw(Vector3.Zero, null, default));
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
        public void WaterPlane_DerivesFromDocumentBoundsAndLevel()
        {
            // Asymmetric bounds so the centre and each half-extent are independently checkable (a square footprint
            // would hide an X/Z swap). The plane centres on the bounds midpoint at the water level and spans the
            // full XZ footprint, so the editor draws one plane covering the whole document.
            var bounds = new MapBounds { MinX = -64f, MinZ = -32f, MaxX = 64f, MaxZ = 96f };

            WaterPlane plane = ViewportWorld.BuildWaterPlane(bounds, -1.2f);

            Assert.Equal(0f, plane.CenterX);       // (-64 + 64) / 2
            Assert.Equal(32f, plane.CenterZ);      // (-32 + 96) / 2
            Assert.Equal(-1.2f, plane.SurfaceY);   // the water level maps straight to the surface height
            Assert.Equal(64f, plane.HalfExtentX);  // (64 - -64) / 2
            Assert.Equal(64f, plane.HalfExtentZ);  // (96 - -32) / 2
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
    }
}
