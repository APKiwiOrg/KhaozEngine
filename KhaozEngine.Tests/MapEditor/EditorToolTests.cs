using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="EditorToolController"/>, the GPU-free per-frame editing policy: tool
    /// mode transitions (Escape resets to Select, Delete removes the selection, a mode switch cancels the gesture),
    /// ground-snapped Add commands from a synthetic ray over a flat field, a Select-mode gizmo drag coalescing into
    /// one undo step, and the draw modes rubber-banding a disc / rect into an exclusion or region. Every ray is
    /// passed pre-normalized (the caller-normalizes contract), so a pick T reads directly as a world distance.</summary>
    public class EditorToolTests
    {
        // Flat field at y = 0 everywhere (single default meadow band, gentle roll zeroed): ground-snap arithmetic
        // stays exact, matching EditorPickingTests / ViewportWorldTests.
        static TerrainField FlatField() => new TerrainField(new TerrainConfig { GentleAmplitude = 0f });

        static float HeightOf(string kind) => kind switch { "hut" => 3f, _ => 2f };

        static readonly Vector3 Down = new(0f, -1f, 0f);

        static (EditorDocument doc, EditorToolController c) Make()
        {
            var md = new MapDocument
            {
                Id = "tool-zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            var doc = new EditorDocument(md);
            var c = new EditorToolController(doc) { Field = FlatField(), HeightOf = HeightOf, GizmoScale = 1f };
            return (doc, c);
        }

        static void Near(float expected, float actual, float eps = 1e-3f) =>
            Assert.True(System.MathF.Abs(expected - actual) < eps, $"expected ~{expected} but got {actual}");

        static EditorFrameInput Press(Vector3 origin, bool shift = false) =>
            new(origin, Down, pointerPressed: true, pointerDown: true, shift: shift, dt: 0.016f);

        static EditorFrameInput Drag(Vector3 origin) => new(origin, Down, pointerDown: true, dt: 0.016f);

        static EditorFrameInput Release(Vector3 origin) => new(origin, Down, pointerReleased: true, dt: 0.016f);

        // ---- mode transitions --------------------------------------------------------------------------

        [Fact]
        public void Mode_DefaultsToSelect()
        {
            var (_, c) = Make();
            Assert.Equal(EditorToolMode.Select, c.Mode);
        }

        [Fact]
        public void Escape_ReturnsToSelect()
        {
            var (_, c) = Make();
            c.Mode = EditorToolMode.PlacePlacement;
            c.Update(new EditorFrameInput(default, default, escapePressed: true));
            Assert.Equal(EditorToolMode.Select, c.Mode);
        }

        [Fact]
        public void ModeSwitch_CancelsInFlightDrag()
        {
            var (doc, c) = Make();
            var p = new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f };
            doc.Doc.Placements.Add(p);
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.Update(Press(new Vector3(0.6f, 100f, 0f)));   // grab the +X translate arrow
            Assert.True(c.IsDragging);

            c.Mode = EditorToolMode.PlaceSpawn;
            Assert.False(c.IsDragging);
        }

        [Fact]
        public void Escape_CancelsInFlightDraw()
        {
            var (_, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            Assert.True(c.IsDrawing);

            c.Update(new EditorFrameInput(default, default, escapePressed: true));
            Assert.False(c.IsDrawing);
            Assert.Equal(EditorToolMode.Select, c.Mode);
        }

        [Fact]
        public void Delete_RemovesSelectedPlacement_ClearsSelection()
        {
            var (doc, c) = Make();
            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut" });
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.Update(new EditorFrameInput(default, default, deletePressed: true));

            Assert.Empty(doc.Doc.Placements);
            Assert.True(doc.Selection.IsEmpty);
            Assert.Equal(1, doc.History.UndoDepth);
        }

        [Fact]
        public void Delete_RemovesSelectedSpawn()
        {
            var (doc, c) = Make();
            doc.Doc.Spawns.Add(new MapSpawn { Id = "s1", ArchetypeId = "wolf" });
            doc.Selection.Set(SelectionKind.Spawn, "s1");

            c.Update(new EditorFrameInput(default, default, deletePressed: true));

            Assert.Empty(doc.Doc.Spawns);
            Assert.True(doc.Selection.IsEmpty);
        }

        [Fact]
        public void Delete_WithEmptySelection_IsNoOp()
        {
            var (doc, c) = Make();
            c.Update(new EditorFrameInput(default, default, deletePressed: true));
            Assert.False(doc.History.CanUndo);
        }

        // ---- select: pick --------------------------------------------------------------------------------

        [Fact]
        public void Select_PicksPlacement_ThenClearsOverGround()
        {
            var (doc, c) = Make();
            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut", X = 10f, Z = 0f });

            c.Update(Press(new Vector3(10f, 100f, 0f)));   // straight down over the placement
            Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);
            Assert.Equal("p1", doc.Selection.Id);

            c.Update(Press(new Vector3(50f, 100f, 50f)));  // empty ground: gizmo misses, terrain clears
            Assert.True(doc.Selection.IsEmpty);
        }

        // ---- place modes ---------------------------------------------------------------------------------

        [Fact]
        public void PlacePlacement_GroundSnapsClickIntoAddCommand()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlacePlacement;
            c.PlaceKind = "hut";

            c.Update(Press(new Vector3(5f, 100f, -3f)));

            Assert.Single(doc.Doc.Placements);
            MapPlacement p = doc.Doc.Placements[0];
            Near(5f, p.X);
            Near(-3f, p.Z);
            Assert.Null(p.Y);                 // ground-snap mode preserved (Y re-samples at load)
            Assert.Equal("hut", p.Kind);
            Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);
            Assert.Equal(p.Id, doc.Selection.Id);
            Assert.Equal(1, doc.History.UndoDepth);
        }

        [Fact]
        public void PlaceSpawn_GroundSnapsClickIntoAddCommand()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlaceSpawn;
            c.SpawnArchetype = "wolf";

            c.Update(Press(new Vector3(2f, 100f, 2f)));

            Assert.Single(doc.Doc.Spawns);
            MapSpawn s = doc.Doc.Spawns[0];
            Near(2f, s.X);
            Near(2f, s.Z);
            Assert.Equal("wolf", s.ArchetypeId);
            Assert.Equal(SelectionKind.Spawn, doc.Selection.Kind);
        }

        // ---- select: gizmo drag --------------------------------------------------------------------------

        [Fact]
        public void SelectDrag_MergesMoveCommands_IntoOneUndoStep()
        {
            var (doc, c) = Make();
            var p = new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f };
            doc.Doc.Placements.Add(p);
            doc.Selection.Set(SelectionKind.Placement, "p1");

            // Press on the +X ground arrow (x in [0, 1.2]) at the gizmo over the origin, then drag the ground hit
            // out along +X and release. TranslateXZ tracks the pointer, so the object follows.
            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));   // ground hit moves +2 -> X = 2
            c.Update(Drag(new Vector3(5.6f, 100f, 0f)));   // ground hit moves +5 -> X = 5
            c.Update(Release(new Vector3(5.6f, 100f, 0f)));

            Assert.False(c.IsDragging);
            Near(5f, p.X);
            Near(0f, p.Z);
            Assert.Equal(1, doc.History.UndoDepth);   // the whole drag is one undo step

            Assert.True(doc.Undo());
            Near(0f, p.X);                            // undo restores the pre-drag position
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void SelectDrag_SeparateDrags_AreSeparateUndoSteps()
        {
            var (doc, c) = Make();
            var p = new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f };
            doc.Doc.Placements.Add(p);
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));
            c.Update(Release(new Vector3(2.6f, 100f, 0f)));   // first drag: X = 2, sealed

            // Second drag: grab the arrow again at the new gizmo position (X = 2) and push further.
            c.Update(Press(new Vector3(2.6f, 100f, 0f)));
            c.Update(Drag(new Vector3(4.6f, 100f, 0f)));       // +2 more -> X = 4
            c.Update(Release(new Vector3(4.6f, 100f, 0f)));

            Near(4f, p.X);
            Assert.Equal(2, doc.History.UndoDepth);            // the seal kept the two drags apart
        }

        // ---- draw modes ----------------------------------------------------------------------------------

        [Fact]
        public void DrawExclusion_DiscDrag_EmitsExclusion_WithCenterAndRadius()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;

            c.Update(Press(new Vector3(3f, 100f, 4f)));    // center at ground (3, 4)
            c.Update(Release(new Vector3(3f, 100f, 8f)));  // radius = |(3,4) -> (3,8)| = 4

            Assert.Single(doc.Doc.Exclusions);
            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Exclusions[0].Shape);
            Near(3f, disc.CenterX);
            Near(4f, disc.CenterZ);
            Near(4f, disc.Radius);
            Assert.True(doc.WorldRebuildPending);           // scatter inputs changed
            Assert.Equal(1, doc.History.UndoDepth);
            Assert.Equal(SelectionKind.Exclusion, doc.Selection.Kind);
        }

        [Fact]
        public void DrawExclusion_ShiftDrag_EmitsRect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;

            c.Update(Press(new Vector3(-2f, 100f, -1f), shift: true));   // shift latched -> rect
            c.Update(Release(new Vector3(6f, 100f, 5f)));

            var rect = Assert.IsType<RectShapeDoc>(doc.Doc.Exclusions[0].Shape);
            Near(-2f, rect.MinX);
            Near(-1f, rect.MinZ);
            Near(6f, rect.MaxX);
            Near(5f, rect.MaxZ);
        }

        [Fact]
        public void DrawRegion_DiscDrag_EmitsRegion_WithAutoUniqueName()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawRegion;

            c.Update(Press(new Vector3(0f, 100f, 0f)));
            c.Update(Release(new Vector3(3f, 100f, 0f)));   // radius 3

            Assert.Single(doc.Doc.Regions);
            Assert.Equal("region-1", doc.Doc.Regions[0].Name);
            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Regions[0].Shape);
            Near(3f, disc.Radius);
            Assert.False(doc.WorldRebuildPending);          // regions are game-interpreted, not terrain-affecting

            // A second region auto-names past the first.
            c.Update(Press(new Vector3(20f, 100f, 20f)));
            c.Update(Release(new Vector3(23f, 100f, 20f)));
            Assert.Equal("region-2", doc.Doc.Regions[1].Name);
        }

        [Fact]
        public void DrawExclusion_DegenerateClick_EmitsNothing()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;

            c.Update(Press(new Vector3(4f, 100f, 4f)));
            c.Update(Release(new Vector3(4f, 100f, 4f)));   // zero radius -> no shape

            Assert.Empty(doc.Doc.Exclusions);
            Assert.False(doc.History.CanUndo);
        }
    }
}
