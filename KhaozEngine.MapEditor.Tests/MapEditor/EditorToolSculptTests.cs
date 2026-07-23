using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The sculpt tool driven through the controller (T2, #271): a press-drag-release stroke is one undo
    /// step, creates tiles on demand, undoes to a byte-identical document, clamps to the document bounds, reports a
    /// bounded dirty region (the partial-rebuild path), and is deterministic.</summary>
    public class EditorToolSculptTests
    {
        static readonly Vector3 Down = new(0f, -1f, 0f);

        static TerrainField FlatField() => new(new TerrainConfig { GentleAmplitude = 0f });
        static MapDocument SampleDoc() => KhaozEngine.Tests.MapDoc.MapDocumentFileTests.SampleDoc();
        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        static (EditorDocument ed, EditorToolController c) Make(float min = -100f, float max = 100f)
        {
            var md = new MapDocument
            {
                Id = "sculpt-zone",
                Bounds = new MapBounds { MinX = min, MinZ = min, MaxX = max, MaxZ = max },
            };
            md.Terrain.GentleAmplitude = 0f;
            var ed = new EditorDocument(md);
            var c = new EditorToolController(ed)
            {
                Field = FlatField(),
                GizmoScale = 1f,
                Mode = EditorToolMode.SculptTerrain,
                Brush = SculptBrush.Raise,
                BrushRadius = 4f,
                BrushStrength = 10f,
            };
            return (ed, c);
        }

        static EditorFrameInput Press(Vector3 o) => new(o, Down, pointerPressed: true, pointerDown: true, dt: 0.016f);
        static EditorFrameInput Drag(Vector3 o) => new(o, Down, pointerDown: true, dt: 0.016f);
        static EditorFrameInput Release(Vector3 o) => new(o, Down, pointerReleased: true, dt: 0.016f);

        static void Stroke(EditorToolController c)
        {
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            c.Update(Drag(new Vector3(2f, 100f, 0f)));
            c.Update(Drag(new Vector3(4f, 100f, 2f)));
            c.Update(Release(new Vector3(4f, 100f, 2f)));
        }

        [Fact]
        public void A_stroke_creates_tiles_and_is_one_undo_step()
        {
            (EditorDocument ed, EditorToolController c) = Make();
            Stroke(c);
            Assert.NotNull(ed.Doc.TerrainOverrides);
            Assert.True(ed.Doc.TerrainOverrides!.TileCount >= 1);
            Assert.Equal(1, ed.History.UndoDepth);       // the whole drag coalesced into one undo entry
        }

        [Fact]
        public void Undo_removes_the_created_layer()
        {
            (EditorDocument ed, EditorToolController c) = Make();
            Stroke(c);
            Assert.True(ed.Undo());
            Assert.Null(ed.Doc.TerrainOverrides);         // the layer the stroke created is gone
        }

        [Fact]
        public void Stroke_then_undo_is_byte_identical_on_a_rich_document()
        {
            MapDocument doc = SampleDoc();
            var ed = new EditorDocument(doc);
            var c = new EditorToolController(ed)
            {
                Field = MapRuntime.BuildField(doc, ed.Registry),
                GizmoScale = 1f,
                Mode = EditorToolMode.SculptTerrain,
                Brush = SculptBrush.Raise,
                BrushRadius = 5f,
                BrushStrength = 12f,
            };
            string before = Save(doc);
            Stroke(c);
            Assert.NotEqual(before, Save(doc));           // the sculpt actually changed the terrain
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));              // and undo returns it exactly
        }

        [Fact]
        public void The_footprint_is_clamped_to_the_document_bounds()
        {
            // Bounds [0,16] hold exactly one 32-cell tile at cellSize 0.5. A brush near the corner reaches into the
            // negative-cell tiles, which must be dropped so the saved document validates.
            var md = new MapDocument
            {
                Id = "edge-zone",
                Bounds = new MapBounds { MinX = 0f, MinZ = 0f, MaxX = 16f, MaxZ = 16f },
            };
            md.Terrain.GentleAmplitude = 0f;
            var ed = new EditorDocument(md);
            var c = new EditorToolController(ed)
            {
                Field = FlatField(),
                GizmoScale = 1f,
                Mode = EditorToolMode.SculptTerrain,
                Brush = SculptBrush.Raise,
                BrushRadius = 4f,
                BrushStrength = 10f,
            };
            c.Update(Press(new Vector3(1f, 100f, 1f)));
            c.Update(Release(new Vector3(1f, 100f, 1f)));

            Assert.NotNull(ed.Doc.TerrainOverrides);
            Assert.True(ed.Doc.TerrainOverrides!.TryGetTile(0, 0, out _));      // the in-bounds tile was painted
            Assert.False(ed.Doc.TerrainOverrides.TryGetTile(-1, -1, out _));    // straddling tiles were clamped away
            Assert.False(ed.Doc.TerrainOverrides.TryGetTile(-1, 0, out _));
            Assert.False(ed.Doc.TerrainOverrides.TryGetTile(0, -1, out _));
            _ = Save(ed.Doc);   // validates on write; throws if any stored tile left the bounds
        }

        [Fact]
        public void A_dab_reports_a_bounded_dirty_region()
        {
            (EditorDocument ed, EditorToolController c) = Make();
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            Assert.True(ed.WorldRebuildPending);
            Assert.True(ed.PendingRebuildRegion.HasValue);   // bounded -> the partial (dirty-region) rebuild path
            RectArea r = ed.PendingRebuildRegion!.Value;
            Assert.True(r.MinX <= 0f && r.MaxX >= 0f && r.MinZ <= 0f && r.MaxZ >= 0f);   // covers the brush centre
        }

        [Fact]
        public void A_press_outside_sculpt_mode_does_not_sculpt()
        {
            (EditorDocument ed, EditorToolController c) = Make();
            c.Mode = EditorToolMode.Select;
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            c.Update(Release(new Vector3(0f, 100f, 0f)));
            Assert.Null(ed.Doc.TerrainOverrides);
        }

        [Fact]
        public void The_same_input_sequence_produces_the_same_document()
        {
            MapDocument a = SampleDoc(), b = SampleDoc();
            var edA = new EditorDocument(a);
            var edB = new EditorDocument(b);
            EditorToolController ControllerFor(MapDocument doc, EditorDocument ed) => new(ed)
            {
                Field = MapRuntime.BuildField(doc, ed.Registry),
                GizmoScale = 1f,
                Mode = EditorToolMode.SculptTerrain,
                Brush = SculptBrush.Raise,
                BrushRadius = 5f,
                BrushStrength = 8f,
            };
            Stroke(ControllerFor(a, edA));
            Stroke(ControllerFor(b, edB));
            Assert.Equal(Save(a), Save(b));
        }
    }
}
