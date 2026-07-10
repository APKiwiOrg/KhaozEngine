using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for GPU-free document picking: a camera ray against placement AABBs, spawn
    /// marker boxes, and the analytic terrain. Nearest hit wins, placements and spawns beat terrain at equal
    /// footing, and a miss beyond maxDistance returns false. Direction normalization is the CALLER's job (the
    /// B1 review lesson): every ray here is passed pre-normalized, so a returned T reads directly as the world
    /// distance to the hit.</summary>
    public class EditorPickingTests
    {
        // A flat field at y = 0 everywhere: single default meadow band (BaseHeight 0, HillAmplitude 0) with the
        // gentle roll zeroed, so SampleHeight is a constant 0 and the arithmetic in each assertion stays exact.
        static TerrainField FlatField() => new TerrainField(new TerrainConfig { GentleAmplitude = 0f });

        // Maps a placement kind id to its world-space box height. Spawns do not consult this (fixed box).
        static float HeightOf(string kind) => kind switch
        {
            "hut" => 3f,
            "rock" => 1f,
            "tall" => 4f,
            "short" => 1f,
            "flat" => 0f,
            _ => 2f,
        };

        // The shared fixture: two placements at distinct spots and one spawn over a flat field.
        static MapDocument Fixture()
        {
            var doc = new MapDocument
            {
                Id = "pick-zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Placements.Add(new MapPlacement { Id = "hut", Kind = "hut", X = 10f, Z = 0f });
            doc.Placements.Add(new MapPlacement { Id = "rock", Kind = "rock", X = -10f, Z = 0f });
            doc.Spawns.Add(new MapSpawn { Id = "wolf", ArchetypeId = "wolf", X = 0f, Z = 10f });
            return doc;
        }

        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        // ---- placement beats terrain -------------------------------------------------------------------

        [Fact]
        public void Pick_StraightDownOverPlacement_ReturnsPlacementNotTerrain()
        {
            MapDocument doc = Fixture();
            TerrainField field = FlatField();

            bool hit = EditorPicking.Pick(doc, field, new Vector3(10f, 100f, 0f), new Vector3(0f, -1f, 0f),
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Assert.Equal(SelectionKind.Placement, result.Kind);
            Assert.Equal("hut", result.Id);
            // The hut box (kind height 3, ground 0) is entered at its top y = 3, which a downward ray from
            // y = 100 reaches before the ground at y = 0, so the placement wins over terrain.
            Near(3f, result.Point.Y);
            Near(97f, result.T);
        }

        [Fact]
        public void Pick_PlacementBeatsTerrain_AtEqualT()
        {
            // A zero-height placement collapses its box onto the ground plane, so its entry T ties the terrain
            // crossing exactly. The contract puts placements and spawns ahead of terrain at equal footing.
            var doc = new MapDocument
            {
                Id = "tie",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Placements.Add(new MapPlacement { Id = "marker", Kind = "flat", X = 0f, Z = 0f });
            TerrainField field = FlatField();

            bool hit = EditorPicking.Pick(doc, field, new Vector3(0f, 100f, 0f), new Vector3(0f, -1f, 0f),
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Assert.Equal(SelectionKind.Placement, result.Kind);
            Assert.Equal("marker", result.Id);
            Near(100f, result.T);
        }

        // ---- nearest of two overlapping placements -----------------------------------------------------

        [Fact]
        public void Pick_TwoOverlappingPlacements_NearestWinsByT()
        {
            // Two placements stacked at the same spot: the taller box is entered higher up (smaller T) by a
            // downward ray, so it wins regardless of document order.
            var doc = new MapDocument
            {
                Id = "overlap",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            doc.Placements.Add(new MapPlacement { Id = "tall", Kind = "tall", X = 0f, Z = 0f });
            doc.Placements.Add(new MapPlacement { Id = "short", Kind = "short", X = 0f, Z = 0f });
            TerrainField field = FlatField();

            bool hit = EditorPicking.Pick(doc, field, new Vector3(0f, 100f, 0f), new Vector3(0f, -1f, 0f),
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Assert.Equal(SelectionKind.Placement, result.Kind);
            Assert.Equal("tall", result.Id);      // top at y = 4 (T = 96) beats the short box top at y = 1 (T = 99)
            Near(96f, result.T);
        }

        // ---- spawn pick --------------------------------------------------------------------------------

        [Fact]
        public void Pick_StraightDownOverSpawn_ReturnsSpawnAtGroundPosition()
        {
            MapDocument doc = Fixture();
            TerrainField field = FlatField();

            bool hit = EditorPicking.Pick(doc, field, new Vector3(0f, 100f, 10f), new Vector3(0f, -1f, 0f),
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Assert.Equal(SelectionKind.Spawn, result.Kind);
            Assert.Equal("wolf", result.Id);
            Near(0f, result.Point.X);
            Near(10f, result.Point.Z);
            // The fixed spawn box is 1.5 tall from the ground, so a downward ray enters its top at y = 1.5.
            Near(1.5f, result.Point.Y);
        }

        // ---- terrain fallback --------------------------------------------------------------------------

        [Fact]
        public void Pick_RayPastEverything_HitsTerrainAsNone()
        {
            MapDocument doc = Fixture();
            TerrainField field = FlatField();

            bool hit = EditorPicking.Pick(doc, field, new Vector3(50f, 100f, 50f), new Vector3(0f, -1f, 0f),
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Assert.Equal(SelectionKind.None, result.Kind);   // the ground is not a selectable element
            Assert.Equal("", result.Id);
            Near(0f, result.Point.Y);                         // flat field surface
            Near(100f, result.T);
        }

        // ---- maxDistance -------------------------------------------------------------------------------

        [Fact]
        public void Pick_BeyondMaxDistance_ReturnsFalse()
        {
            MapDocument doc = Fixture();
            TerrainField field = FlatField();

            // The hut top sits at T = 97 and the ground at T = 100, both past a 10-unit cap, so nothing is hit.
            bool hit = EditorPicking.Pick(doc, field, new Vector3(10f, 100f, 0f), new Vector3(0f, -1f, 0f),
                10f, HeightOf, out EditorPicking.PickResult result);

            Assert.False(hit);
            Assert.Equal(SelectionKind.None, result.Kind);
            Assert.Equal("", result.Id);
        }

        // ---- normalized-ray caller contract ------------------------------------------------------------

        [Fact]
        public void Pick_WithNormalizedRay_TIsWorldDistance()
        {
            // Documents the caller contract: the picker never normalizes. A pre-normalized direction makes the
            // returned T read as metres, so the hut box top (y = 3) is exactly 97 units below the y = 100 origin.
            MapDocument doc = Fixture();
            TerrainField field = FlatField();
            Vector3 dir = Vector3.Normalize(new Vector3(0f, -1f, 0f));

            bool hit = EditorPicking.Pick(doc, field, new Vector3(10f, 100f, 0f), dir,
                1000f, HeightOf, out EditorPicking.PickResult result);

            Assert.True(hit);
            Near(97f, result.T);
            Near(97f, (new Vector3(10f, 100f, 0f) - result.Point).Length());
        }

        // ---- PickTerrain -------------------------------------------------------------------------------

        [Fact]
        public void PickTerrain_DownRay_HitsGround()
        {
            TerrainField field = FlatField();

            bool hit = EditorPicking.PickTerrain(field, new Vector3(0f, 50f, 0f), new Vector3(0f, -1f, 0f),
                1000f, out Vector3 point);

            Assert.True(hit);
            Near(0f, point.X);
            Near(0f, point.Y);
            Near(0f, point.Z);
        }

        [Fact]
        public void PickTerrain_BeyondMaxDistance_ReturnsFalse()
        {
            TerrainField field = FlatField();

            // From y = 50 the ground at y = 0 is 50 units away, past a 10-unit cap.
            bool hit = EditorPicking.PickTerrain(field, new Vector3(0f, 50f, 0f), new Vector3(0f, -1f, 0f),
                10f, out Vector3 point);

            Assert.False(hit);
            Assert.Equal(default, point);
        }

        // ---- argument guards ---------------------------------------------------------------------------

        [Fact]
        public void Pick_NullArguments_Throw()
        {
            MapDocument doc = Fixture();
            TerrainField field = FlatField();
            Assert.Throws<ArgumentNullException>(() =>
                EditorPicking.Pick(null!, field, Vector3.Zero, -Vector3.UnitY, 1f, HeightOf, out _));
            Assert.Throws<ArgumentNullException>(() =>
                EditorPicking.Pick(doc, field, Vector3.Zero, -Vector3.UnitY, 1f, null!, out _));
        }
    }
}
