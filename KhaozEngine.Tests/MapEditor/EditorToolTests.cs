using System.Collections.Generic;
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

        // Like Make() but with one scatter layer, so a BakeRegion rect gesture has a layer to freeze and its
        // command actually executes (BakeLayer resolves to the document's first scatter layer).
        static (EditorDocument doc, EditorToolController c) MakeWithScatterLayer()
        {
            var md = new MapDocument
            {
                Id = "tool-zone",
                Bounds = new MapBounds { MinX = -100f, MinZ = -100f, MaxX = 100f, MaxZ = 100f },
            };
            md.Terrain.GentleAmplitude = 0f;
            md.ScatterLayers.Add(new MapScatterLayer
            {
                Name = "trees",
                Seed = 4242,
                CellSize = 10f,
                Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = { new MapPropKind { Id = "pine_a", Weight = 1f } } } },
            });
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

        // A held-and-moved frame carrying an explicit screen-space travel, for the body-drag arming threshold.
        static EditorFrameInput BodyDrag(Vector3 origin, float travel) =>
            new(origin, Down, pointerDown: true, pointerTravel: travel, dt: 0.016f);

        // A press point on an object's body that clears every gizmo handle (arrows, ring, scale cube) at gizmo
        // scale 1 over the origin: x = z = -0.4 misses the +X / +Z / +Y arrow boxes (half-width 0.15), the scale
        // cube at (0.85, 0.85), and the yaw ring band at radius 1, while sitting inside a 1.0-wide spawn box and a
        // hut placement's 1.8-wide box. The vertical ray still hits the object AABB, so the pick selects it.
        static Vector3 BodyPoint => new(-0.4f, 100f, -0.4f);

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
        public void SelectDrag_MovesSelectedSpawn_MergesIntoOneUndoStep()
        {
            var (doc, c) = Make();
            var s = new MapSpawn { Id = "s1", ArchetypeId = "wolf", X = 0f, Z = 0f };
            doc.Doc.Spawns.Add(s);

            // Select via pick: nothing is selected yet, so the first press falls through to EditorPicking rather
            // than the gizmo (there is no gizmo without a selection).
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            Assert.Equal(SelectionKind.Spawn, doc.Selection.Kind);
            Assert.Equal("s1", doc.Selection.Id);
            Assert.False(c.IsDragging);

            // Now the spawn is selected, its gizmo (Marker affordance) sits at its position and draws the XZ
            // arrows: press on the +X ground arrow (x in [0, 1.2]) and drag the ground hit out along +X.
            // TranslateXZ is the only handle a spawn honours (RestrictHandle blocks Y / yaw / scale), matching
            // what the newly-visible arrows offer.
            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));   // ground hit moves +2 -> X = 2
            c.Update(Drag(new Vector3(5.6f, 100f, 0f)));   // ground hit moves +5 -> X = 5
            c.Update(Release(new Vector3(5.6f, 100f, 0f)));

            Assert.False(c.IsDragging);
            Near(5f, s.X);
            Near(0f, s.Z);
            Assert.Equal(1, doc.History.UndoDepth);   // the whole drag coalesces into one MoveSpawnCommand step

            Assert.True(doc.Undo());
            Near(0f, s.X);                            // undo restores the pre-drag position
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

        // ---- drags vs vanished objects -------------------------------------------------------------------

        [Fact]
        public void DeleteDuringDrag_CancelsDragWithoutThrowing()
        {
            var (doc, c) = Make();
            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f });
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.Update(Press(new Vector3(0.6f, 100f, 0f)));   // grab the +X translate arrow
            Assert.True(c.IsDragging);

            // The delete edge lands mid-drag: DeleteSelection removes p1 BEFORE the select-mode step runs in the
            // same frame, so the drag continuation must not execute a move on the vanished id (no throw).
            c.Update(new EditorFrameInput(new Vector3(2.6f, 100f, 0f), Down,
                pointerDown: true, deletePressed: true, dt: 0.016f));

            Assert.False(c.IsDragging);
            Assert.Empty(doc.Doc.Placements);
            Assert.Equal(1, doc.History.UndoDepth);   // just the remove, no move commands on the vanished id

            // Further drag frames and the release stay inert.
            c.Update(Drag(new Vector3(5.6f, 100f, 0f)));
            c.Update(Release(new Vector3(5.6f, 100f, 0f)));
            Assert.Equal(1, doc.History.UndoDepth);
            Assert.False(c.IsDragging);
        }

        [Fact]
        public void UndoDrainDuringDrag_CancelsDrag()
        {
            var (doc, c) = Make();
            doc.Execute(new AddPlacementCommand(new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f }));
            doc.SealGesture();
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));   // one move step on top of the add

            // An undo drain mid-drag removes the move AND the object's own Add: the dragged id is gone.
            Assert.True(doc.Undo());
            Assert.True(doc.Undo());
            Assert.Empty(doc.Doc.Placements);

            c.Update(Drag(new Vector3(5.6f, 100f, 0f)));   // must cancel cleanly, not throw
            Assert.False(c.IsDragging);
            c.Update(Release(new Vector3(5.6f, 100f, 0f)));
            Assert.Empty(doc.Doc.Placements);
            Assert.False(doc.History.CanUndo);             // no new commands were emitted
        }

        // ---- gesture barrier on grab ---------------------------------------------------------------------

        [Fact]
        public void InspectorEditThenDrag_AreSeparateUndoSteps()
        {
            var (doc, c) = Make();
            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f });
            doc.Selection.Set(SelectionKind.Placement, "p1");

            // An inspector-style edit right before the drag: a merge-capable move of the same placement.
            doc.Execute(new MovePlacementCommand("p1", 1f, 0f, null));
            Assert.Equal(1, doc.History.UndoDepth);

            // Gizmo grab at the placement's new position, then drag + release: the grab must seal the gesture,
            // so the drag's moves start a NEW undo step instead of coalescing into the inspector edit.
            c.Update(Press(new Vector3(1.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(3.6f, 100f, 0f)));
            c.Update(Release(new Vector3(3.6f, 100f, 0f)));

            Assert.Equal(2, doc.History.UndoDepth);
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
            Assert.Equal(EditorToolMode.Select, c.Mode);    // one shot: the first draw disarmed the tool

            // A second region auto-names past the first. The draw tool is one shot, so re-arm it before drawing.
            c.Mode = EditorToolMode.DrawRegion;
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

        [Fact]
        public void DrawScatterOverride_DiscDrag_EmitsOverride_WithCenterAndRadius()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawScatterOverride;

            c.Update(Press(new Vector3(3f, 100f, 4f)));    // center at ground (3, 4)
            c.Update(Release(new Vector3(3f, 100f, 8f)));  // radius = |(3,4) -> (3,8)| = 4

            Assert.Single(doc.Doc.ScatterOverrides);
            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(3f, disc.CenterX);
            Near(4f, disc.CenterZ);
            Near(4f, disc.Radius);
            Near(1f, doc.Doc.ScatterOverrides[0].DensityMultiplier);   // fresh override keeps the default multiplier
            Assert.True(doc.WorldRebuildPending);           // scatter inputs changed
            Assert.Equal(1, doc.History.UndoDepth);
            Assert.Equal(SelectionKind.ScatterOverride, doc.Selection.Kind);
            Assert.Equal("0", doc.Selection.Id);
        }

        [Fact]
        public void DrawScatterOverride_ShiftDrag_EmitsRect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawScatterOverride;

            c.Update(Press(new Vector3(-2f, 100f, -1f), shift: true));   // shift latched -> rect
            c.Update(Release(new Vector3(6f, 100f, 5f)));

            var rect = Assert.IsType<RectShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(-2f, rect.MinX);
            Near(-1f, rect.MinZ);
            Near(6f, rect.MaxX);
            Near(5f, rect.MaxZ);
        }

        [Fact]
        public void DrawScatterOverride_DegenerateClick_EmitsNothing()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawScatterOverride;

            c.Update(Press(new Vector3(4f, 100f, 4f)));
            c.Update(Release(new Vector3(4f, 100f, 4f)));   // zero radius -> no shape

            Assert.Empty(doc.Doc.ScatterOverrides);
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void DrawScatterOverride_CompletedGesture_ReturnsToSelect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawScatterOverride;

            c.Update(Press(new Vector3(3f, 100f, 4f)));
            c.Update(Release(new Vector3(3f, 100f, 8f)));   // radius 4 -> a real override commits

            Assert.Single(doc.Doc.ScatterOverrides);
            Assert.Equal(EditorToolMode.Select, c.Mode);    // one shot: the commit disarms the tool
        }

        // ---- one-shot draw tools -------------------------------------------------------------------------

        [Fact]
        public void DrawExclusion_CompletedGesture_ReturnsToSelect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;

            c.Update(Press(new Vector3(3f, 100f, 4f)));
            c.Update(Release(new Vector3(3f, 100f, 8f)));   // radius 4 -> a real exclusion commits

            Assert.Single(doc.Doc.Exclusions);
            Assert.Equal(EditorToolMode.Select, c.Mode);    // one shot: the commit disarms the tool
        }

        [Fact]
        public void DrawRegion_CompletedGesture_ReturnsToSelect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawRegion;

            c.Update(Press(new Vector3(0f, 100f, 0f)));
            c.Update(Release(new Vector3(3f, 100f, 0f)));   // radius 3 -> a real region commits

            Assert.Single(doc.Doc.Regions);
            Assert.Equal(EditorToolMode.Select, c.Mode);
        }

        [Fact]
        public void BakeRegion_CompletedGesture_ReturnsToSelect()
        {
            var (doc, c) = MakeWithScatterLayer();
            c.Mode = EditorToolMode.BakeRegion;

            c.Update(Press(new Vector3(-15f, 100f, -15f)));
            c.Update(Release(new Vector3(15f, 100f, 15f)));   // a real rect over a scatter layer commits

            Assert.Equal(1, doc.History.UndoDepth);           // the bake command ran
            Assert.Equal(EditorToolMode.Select, c.Mode);
        }

        [Fact]
        public void AbandonedGesture_DoesNotReturnToSelect()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.DrawExclusion;

            // A degenerate click emits no command, so the one-shot return never fires and the tool stays armed
            // for the next draw. Abandonment (Escape / mode switch) keeps its own behavior, tested elsewhere.
            c.Update(Press(new Vector3(4f, 100f, 4f)));
            c.Update(Release(new Vector3(4f, 100f, 4f)));     // zero radius -> nothing committed

            Assert.Empty(doc.Doc.Exclusions);
            Assert.Equal(EditorToolMode.DrawExclusion, c.Mode);
        }

        // ---- mode hint -----------------------------------------------------------------------------------

        [Fact]
        public void ModeHint_ReflectsModeAndPlaceKind()
        {
            var (_, c) = Make();

            Assert.Equal(EditorToolMode.Select, c.Mode);
            Assert.Contains("select", c.ModeHint, System.StringComparison.OrdinalIgnoreCase);

            c.Mode = EditorToolMode.PlacePlacement;
            c.PlaceKind = "hut";
            Assert.Contains("hut", c.ModeHint, System.StringComparison.Ordinal);

            c.Mode = EditorToolMode.PlaceSpawn;
            c.SpawnArchetype = "wolf";
            Assert.Contains("wolf", c.ModeHint, System.StringComparison.Ordinal);

            c.Mode = EditorToolMode.DrawExclusion;
            Assert.Contains("one shot", c.ModeHint, System.StringComparison.OrdinalIgnoreCase);

            // No em / en dashes or prose semicolons in any hint (the shipped-writing punctuation rule).
            foreach (EditorToolMode m in System.Enum.GetValues<EditorToolMode>())
            {
                c.Mode = m;
                string hint = c.ModeHint;
                Assert.DoesNotContain((char)0x2014, hint);   // no em dash
                Assert.DoesNotContain((char)0x2013, hint);   // no en dash
                Assert.DoesNotContain(';', hint);
            }
        }

        // ---- overlay picking (Select mode) ----------------------------------------------------------------

        [Fact]
        public void OverlayPick_SelectsRegionExclusionFeature()
        {
            var (doc, c) = Make();
            // A big region disc, a medium exclusion disc, and a lake feature (a marker-radius disc at its
            // center), all concentric on the origin, plus a lone region off to the side. No placement or spawn,
            // so every pick falls through EditorPicking's terrain hit into the overlay test.
            doc.Doc.Regions.Add(new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 30f } });
            doc.Doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f } });
            doc.Doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 8f, Depth = 2f });

            // Inside the lake marker (< 1.5 m of its center): features outrank exclusions and regions.
            c.Update(Press(new Vector3(0.5f, 100f, 0.5f)));
            Assert.Equal(SelectionKind.Feature, doc.Selection.Kind);
            Assert.Equal("0", doc.Selection.Id);

            // Inside the exclusion but clear of the marker: exclusions outrank the region they sit in.
            c.Update(Press(new Vector3(5f, 100f, 0f)));
            Assert.Equal(SelectionKind.Exclusion, doc.Selection.Kind);
            Assert.Equal("0", doc.Selection.Id);

            // Inside the region only: the region wins.
            c.Update(Press(new Vector3(20f, 100f, 0f)));
            Assert.Equal(SelectionKind.Region, doc.Selection.Kind);
            Assert.Equal("town", doc.Selection.Id);

            // Clear of every overlay: the selection clears.
            c.Update(Press(new Vector3(60f, 100f, 60f)));
            Assert.True(doc.Selection.IsEmpty);
        }

        // ---- shape / feature gizmo drags --------------------------------------------------------------------

        [Fact]
        public void ShapeDrag_MovesCenterThroughCommand()
        {
            var (doc, c) = Make();
            doc.Doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            doc.Selection.Set(SelectionKind.Exclusion, "0");

            // Grab the +X translate arrow on the shape-center gizmo, drag the ground hit out +2 on X, release.
            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));
            c.Update(Release(new Vector3(2.6f, 100f, 0f)));

            Assert.False(c.IsDragging);
            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Exclusions[0].Shape);
            Near(2f, disc.CenterX);
            Near(0f, disc.CenterZ);
            Near(5f, disc.Radius);                      // a translate leaves the radius alone
            Assert.True(doc.WorldRebuildPending);        // exclusion shape edits rebuild the streamed world
            Assert.Equal(1, doc.History.UndoDepth);      // the whole drag is one coalesced undo step

            Assert.True(doc.Undo());
            disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Exclusions[0].Shape);
            Near(0f, disc.CenterX);                      // undo restores the pre-drag center
        }

        [Fact]
        public void ShapeScale_ResizesRadius()
        {
            var (doc, c) = Make();
            doc.Doc.Regions.Add(new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            doc.Selection.Set(SelectionKind.Region, "town");

            // Grab the corner scale cube (at (0.85, 0, 0.85) at gizmo scale 1) and drag out to double the radius
            // measured from the shape center, so the scale factor is exactly 2.
            c.Update(Press(new Vector3(0.85f, 100f, 0.85f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(1.7f, 100f, 1.7f)));
            c.Update(Release(new Vector3(1.7f, 100f, 1.7f)));

            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Regions[0].Shape);
            Near(10f, disc.Radius);                      // 5 * 2
            Near(0f, disc.CenterX);                      // the center is preserved under a scale
            Near(0f, disc.CenterZ);
            Assert.False(doc.WorldRebuildPending);        // regions are game-interpreted, never rebuild the world
            Assert.Equal(1, doc.History.UndoDepth);

            Assert.True(doc.Undo());
            disc = Assert.IsType<DiscShapeDoc>(doc.Doc.Regions[0].Shape);
            Near(5f, disc.Radius);
        }

        [Fact]
        public void ScatterOverrideDrag_MovesCenterThroughCommand_OneUndoStep()
        {
            var (doc, c) = Make();
            doc.Doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            doc.Selection.Set(SelectionKind.ScatterOverride, "0");

            // The override shape sits its gizmo at the shape center too -> MoveScale, no yaw ring.
            Assert.Equal(GizmoAffordance.MoveScale, c.TryGizmo(out _));

            // Grab the +X translate arrow, drag the ground hit out +2 on X across two held frames, release.
            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));
            c.Update(Release(new Vector3(2.6f, 100f, 0f)));

            Assert.False(c.IsDragging);
            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(2f, disc.CenterX);
            Near(0f, disc.CenterZ);
            Near(5f, disc.Radius);                      // a translate leaves the radius alone
            Assert.True(doc.WorldRebuildPending);        // override shape edits rebuild the streamed world
            Assert.Equal(1, doc.History.UndoDepth);      // the whole drag coalesces into one undo step

            Assert.True(doc.Undo());
            disc = Assert.IsType<DiscShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(0f, disc.CenterX);                      // undo restores the pre-drag center
        }

        [Fact]
        public void ScatterOverrideScale_ResizesRadius_OneUndoStep()
        {
            var (doc, c) = Make();
            doc.Doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });
            doc.Selection.Set(SelectionKind.ScatterOverride, "0");

            // Grab the corner scale cube (at (0.85, 0, 0.85) at gizmo scale 1) and drag out to double the radius.
            c.Update(Press(new Vector3(0.85f, 100f, 0.85f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(1.7f, 100f, 1.7f)));
            c.Update(Release(new Vector3(1.7f, 100f, 1.7f)));

            var disc = Assert.IsType<DiscShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(10f, disc.Radius);                      // 5 * 2
            Near(0f, disc.CenterX);                      // the center is preserved under a scale
            Assert.True(doc.WorldRebuildPending);
            Assert.Equal(1, doc.History.UndoDepth);

            Assert.True(doc.Undo());
            disc = Assert.IsType<DiscShapeDoc>(doc.Doc.ScatterOverrides[0].Shape);
            Near(5f, disc.Radius);
        }

        [Fact]
        public void ScatterOverrideDelete_RemovesSelected_NotifiesIndexRemoved()
        {
            var (doc, c) = Make();
            SelectionKind removedKind = SelectionKind.None;
            int removedIndex = -1;
            c.OnIndexRemoved = (k, i) => { removedKind = k; removedIndex = i; };
            doc.Doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 6f } });
            doc.Selection.Set(SelectionKind.ScatterOverride, "0");

            c.Update(new EditorFrameInput(default, default, deletePressed: true));

            Assert.Empty(doc.Doc.ScatterOverrides);
            Assert.True(doc.Selection.IsEmpty);
            Assert.True(doc.WorldRebuildPending);
            Assert.Equal(1, doc.History.UndoDepth);
            Assert.Equal(SelectionKind.ScatterOverride, removedKind);   // the index-remap notification fired
            Assert.Equal(0, removedIndex);

            Assert.True(doc.Undo());
            Assert.Single(doc.Doc.ScatterOverrides);      // the removed override is restored
        }

        // ---- feature placement / delete ---------------------------------------------------------------------

        [Fact]
        public void FeaturePlace_AddsDefaultLakeAtClick()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.EditFeature;
            c.PlaceFeatureType = "lake";

            c.Update(Press(new Vector3(5f, 100f, -3f)));

            Assert.Single(doc.Doc.Terrain.Features);
            var lake = Assert.IsType<LakeFeatureDoc>(doc.Doc.Terrain.Features[0]);
            Near(5f, lake.CenterX);
            Near(-3f, lake.CenterZ);
            Near(10f, lake.Radius);                      // r10 default
            Near(3f, lake.Depth);                        // d3 default
            Assert.Equal(SelectionKind.Feature, doc.Selection.Kind);
            Assert.Equal("0", doc.Selection.Id);
            Assert.True(doc.WorldRebuildPending);         // features affect the streamed world
            Assert.Equal(EditorToolMode.Select, c.Mode);  // one shot back to Select
            Assert.Equal(1, doc.History.UndoDepth);
        }

        // ---- yaw ring on rotatable features ----------------------------------------------------------------

        [Fact]
        public void RotatableFeature_GetsRingAffordance_OthersDoNot()
        {
            var (doc, c) = Make();
            var rimWithPass = new RimFeatureDoc { CenterX = 0f, CenterZ = 0f, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f };
            rimWithPass.Passes.Add(new RimPassDoc { AngleRadians = 0f, HalfWidth = 0.5f });
            doc.Doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = 0f, PointZ = 0f, DirectionX = 1f, DirectionZ = 0f, Height = 5f, Width = 8f });   // 0
            doc.Doc.Terrain.Features.Add(rimWithPass);                                                                                                    // 1
            doc.Doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f, Depth = 3f });                                    // 2
            doc.Doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f, TargetHeight = 0f });                          // 3
            doc.Doc.Terrain.Features.Add(new RimFeatureDoc { CenterX = 0f, CenterZ = 0f, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f });        // 4, zero passes
            doc.Doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });

            // Ridge and a rim with at least one pass expose an orientation -> MoveScaleRotate, ring in the mesh set.
            foreach (string rotatableId in new[] { "0", "1" })
            {
                doc.Selection.Set(SelectionKind.Feature, rotatableId);
                GizmoAffordance a = c.TryGizmo(out _);
                Assert.Equal(GizmoAffordance.MoveScaleRotate, a);
                Assert.Contains(GizmoMesh.YawRing, MapEditorScene.ComputeGizmoMeshes(a));
            }

            // Lake and flatten are rotationally symmetric -> MoveScale, no ring in the mesh set.
            foreach (string symmetricId in new[] { "2", "3" })
            {
                doc.Selection.Set(SelectionKind.Feature, symmetricId);
                GizmoAffordance a = c.TryGizmo(out _);
                Assert.Equal(GizmoAffordance.MoveScale, a);
                Assert.DoesNotContain(GizmoMesh.YawRing, MapEditorScene.ComputeGizmoMeshes(a));
            }

            // A rim with zero passes has nothing to rotate either (rotationally symmetric too) -> MoveScale, no ring.
            doc.Selection.Set(SelectionKind.Feature, "4");
            GizmoAffordance passlessRim = c.TryGizmo(out _);
            Assert.Equal(GizmoAffordance.MoveScale, passlessRim);
            Assert.DoesNotContain(GizmoMesh.YawRing, MapEditorScene.ComputeGizmoMeshes(passlessRim));

            // A disc shape has no rotational field either -> MoveScale, no ring.
            doc.Selection.Set(SelectionKind.Exclusion, "0");
            GizmoAffordance ex = c.TryGizmo(out _);
            Assert.Equal(GizmoAffordance.MoveScale, ex);
            Assert.DoesNotContain(GizmoMesh.YawRing, MapEditorScene.ComputeGizmoMeshes(ex));
        }

        [Fact]
        public void RingDrag_RotatesRidge_OneUndoStep()
        {
            var (doc, c) = Make();
            var ridge = new RidgeFeatureDoc { PointX = 0f, PointZ = 0f, DirectionX = 1f, DirectionZ = 0f, Height = 5f, Width = 8f };
            doc.Doc.Terrain.Features.Add(ridge);
            doc.Selection.Set(SelectionKind.Feature, "0");

            // Grab the yaw ring at radius 1 (45 degrees, clear of the arrow boxes and the corner cube), then sweep
            // the ground hit to 135 degrees and release. The ridge's direction turns by the swept angle.
            float d = GizmoGeometry.RingRadius * System.MathF.Sqrt(0.5f);   // ~0.707: on the ring band at 45 deg
            c.Update(Press(new Vector3(d, 100f, d)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(-d, 100f, d)));      // sweep to 135 deg
            c.Update(Release(new Vector3(-d, 100f, d)));

            Assert.False(c.IsDragging);
            var edited = Assert.IsType<RidgeFeatureDoc>(doc.Doc.Terrain.Features[0]);
            // Start +X swept a quarter turn toward +Z (the grabbed ring point moved from 45deg to 135deg, the +Z
            // side): the ridge direction tracks the cursor and lands on +Z, not -Z. YawDelta itself is -pi/2 for
            // this sweep (it is pre-signed for CreateRotationY), so the call site negates it before handing it to
            // FeatureGeometry.Rotated's standard-convention rotation. See the comment at the ApplyDrag call site.
            Near(0f, edited.DirectionX);
            Near(1f, edited.DirectionZ);
            Assert.True(doc.WorldRebuildPending);           // features affect the streamed world
            Assert.Equal(1, doc.History.UndoDepth);         // the whole ring drag is one coalesced undo step

            Assert.True(doc.Undo());
            var restored = Assert.IsType<RidgeFeatureDoc>(doc.Doc.Terrain.Features[0]);
            Near(1f, restored.DirectionX);                  // undo restores the pre-drag direction exactly
            Near(0f, restored.DirectionZ);
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void RingDrag_RotatesRimPasses_TrackingCursor()
        {
            var (doc, c) = Make();
            var rim = new RimFeatureDoc { CenterX = 0f, CenterZ = 0f, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f };
            rim.Passes.Add(new RimPassDoc { AngleRadians = 0f, HalfWidth = 0.5f });
            doc.Doc.Terrain.Features.Add(rim);
            doc.Selection.Set(SelectionKind.Feature, "0");

            // Same gesture as the ridge test: grab the ring at 45 deg, sweep to 135 deg (a quarter turn toward +Z).
            // The rim has no single heading, so it rotates delta-only, but it must track the cursor exactly like
            // the ridge: the pass angle lands near +pi/2, not -pi/2.
            float d = GizmoGeometry.RingRadius * System.MathF.Sqrt(0.5f);
            c.Update(Press(new Vector3(d, 100f, d)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(-d, 100f, d)));
            c.Update(Release(new Vector3(-d, 100f, d)));

            Assert.False(c.IsDragging);
            var edited = Assert.IsType<RimFeatureDoc>(doc.Doc.Terrain.Features[0]);
            Assert.Single(edited.Passes);
            Near(System.MathF.PI / 2f, edited.Passes[0].AngleRadians);
            Assert.True(doc.WorldRebuildPending);
            Assert.Equal(1, doc.History.UndoDepth);
        }

        [Fact]
        public void RingDrag_OnLake_CannotArm()
        {
            var (doc, c) = Make();
            doc.Doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 10f, Depth = 3f });
            doc.Selection.Set(SelectionKind.Feature, "0");

            // A ray at the ring band over a rotationally symmetric feature: RestrictHandle strips the yaw ring, so
            // no gesture arms and no rotate command runs.
            float d = GizmoGeometry.RingRadius * System.MathF.Sqrt(0.5f);
            c.Update(Press(new Vector3(d, 100f, d)));

            Assert.False(c.IsDragging);
            Assert.False(doc.History.CanUndo);              // nothing rotated
        }

        [Fact]
        public void FeatureDelete_RemovesSelected()
        {
            var (doc, c) = Make();
            doc.Doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 1f, CenterZ = 2f, Radius = 6f, Depth = 3f });
            doc.Selection.Set(SelectionKind.Feature, "0");

            c.Update(new EditorFrameInput(default, default, deletePressed: true));

            Assert.Empty(doc.Doc.Terrain.Features);
            Assert.True(doc.Selection.IsEmpty);
            Assert.True(doc.WorldRebuildPending);
            Assert.Equal(1, doc.History.UndoDepth);

            Assert.True(doc.Undo());
            Assert.Single(doc.Doc.Terrain.Features);      // the removed feature is restored
        }

        // ---- body drag (grab the object, not just the handles) --------------------------------------------

        [Fact]
        public void BodyDrag_MovesSelectedSpawn_WithoutTouchingArrows()
        {
            var (doc, c) = Make();
            var s = new MapSpawn { Id = "s1", ArchetypeId = "wolf", X = 0f, Z = 0f };
            doc.Doc.Spawns.Add(s);
            doc.Selection.Set(SelectionKind.Spawn, "s1");

            // Press the spawn body clear of every gizmo handle: selection stays, but no drag arms yet.
            c.Update(Press(BodyPoint));
            Assert.False(c.IsDragging);
            Assert.Equal(SelectionKind.Spawn, doc.Selection.Kind);
            Assert.Equal(0, doc.History.UndoDepth);

            // Move past the arming threshold: the SAME TranslateXZ path the arrows use takes over and the spawn
            // follows the ground hit (press ground point x = -0.4, dragged to x = 4.6, so +5 on X).
            c.Update(BodyDrag(new Vector3(4.6f, 100f, -0.4f), EditorToolController.BodyDragThreshold + 1f));
            Assert.True(c.IsDragging);
            c.Update(Release(new Vector3(4.6f, 100f, -0.4f)));

            Assert.False(c.IsDragging);
            Near(5f, s.X);
            Near(0f, s.Z);
            Assert.Equal(1, doc.History.UndoDepth);   // the whole body drag is one undo step

            Assert.True(doc.Undo());
            Near(0f, s.X);                            // undo restores the pre-drag position
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void BodyDrag_SelectThenDrag_OneGesture()
        {
            var (doc, c) = Make();
            var p = new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f };
            doc.Doc.Placements.Add(p);
            // Nothing selected: the press must both select the placement AND arm the body drag in one hold.

            c.Update(Press(BodyPoint));
            Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);   // selection lands on press
            Assert.Equal("p1", doc.Selection.Id);
            Assert.False(c.IsDragging);

            c.Update(BodyDrag(new Vector3(4.6f, 100f, -0.4f), EditorToolController.BodyDragThreshold + 1f));
            Assert.True(c.IsDragging);
            c.Update(Release(new Vector3(4.6f, 100f, -0.4f)));

            Near(5f, p.X);
            Near(0f, p.Z);
            Assert.Null(p.Y);                          // ground-snap preserved through the drag
            Assert.Equal(1, doc.History.UndoDepth);    // select-then-drag is one undo step (the move)

            Assert.True(doc.Undo());
            Near(0f, p.X);
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void BodyTap_SelectsWithoutMoving()
        {
            var (doc, c) = Make();
            var s = new MapSpawn { Id = "s1", ArchetypeId = "wolf", X = 0f, Z = 0f };
            doc.Doc.Spawns.Add(s);

            // Press then release below the threshold: a plain tap. Selection sticks, nothing moves, no undo step.
            c.Update(Press(BodyPoint));
            Assert.Equal(SelectionKind.Spawn, doc.Selection.Kind);
            c.Update(Release(BodyPoint));

            Assert.False(c.IsDragging);
            Near(0f, s.X);
            Near(0f, s.Z);
            Assert.Equal(0, doc.History.UndoDepth);
        }

        [Fact]
        public void BodyDrag_BelowThreshold_NeverMoves()
        {
            var (doc, c) = Make();
            var s = new MapSpawn { Id = "s1", ArchetypeId = "wolf", X = 0f, Z = 0f };
            doc.Doc.Spawns.Add(s);
            doc.Selection.Set(SelectionKind.Spawn, "s1");

            c.Update(Press(BodyPoint));
            // Held frames that stay under the threshold never arm the drag.
            c.Update(BodyDrag(new Vector3(0.2f, 100f, -0.4f), EditorToolController.BodyDragThreshold - 1f));
            Assert.False(c.IsDragging);
            c.Update(BodyDrag(new Vector3(0.2f, 100f, -0.4f), EditorToolController.BodyDragThreshold - 0.01f));
            Assert.False(c.IsDragging);
            c.Update(Release(new Vector3(0.2f, 100f, -0.4f)));

            Assert.False(c.IsDragging);
            Near(0f, s.X);
            Assert.Equal(0, doc.History.UndoDepth);   // sub-threshold hold leaves no history
        }

        [Fact]
        public void BodyDrag_EscapeCancelsPending()
        {
            var (doc, c) = Make();
            var s = new MapSpawn { Id = "s1", ArchetypeId = "wolf", X = 0f, Z = 0f };
            doc.Doc.Spawns.Add(s);
            doc.Selection.Set(SelectionKind.Spawn, "s1");

            c.Update(Press(BodyPoint));               // pending body drag recorded (still below threshold)
            c.Update(new EditorFrameInput(default, default, escapePressed: true));   // Escape cancels it

            // A later above-threshold move must NOT arm a drag: the pending state was cancelled.
            c.Update(BodyDrag(new Vector3(6.6f, 100f, -0.4f), EditorToolController.BodyDragThreshold + 5f));
            Assert.False(c.IsDragging);
            Near(0f, s.X);
            Assert.Equal(0, doc.History.UndoDepth);
        }

        // ---- place-and-adjust (place on press, adjust on hold, one undo step) -----------------------------

        [Fact]
        public void PlaceMode_PressHoldAdjustRelease_OneHistoryEntry()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlacePlacement;
            c.PlaceKind = "hut";

            // Press places the hut immediately (feedback + selection), still gripped.
            c.Update(Press(new Vector3(5f, 100f, -3f)));
            Assert.Single(doc.Doc.Placements);
            Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);
            Near(5f, doc.Doc.Placements[0].X);
            Near(-3f, doc.Doc.Placements[0].Z);

            // Hold-adjust: while down, the placed hut tracks the ground hit.
            c.Update(Drag(new Vector3(9f, 100f, 1f)));
            Near(9f, doc.Doc.Placements[0].X);
            Near(1f, doc.Doc.Placements[0].Z);
            c.Update(Drag(new Vector3(12f, 100f, 4f)));
            Near(12f, doc.Doc.Placements[0].X);
            Near(4f, doc.Doc.Placements[0].Z);

            c.Update(Release(new Vector3(12f, 100f, 4f)));   // seal the gesture

            // The whole place-and-adjust is ONE undo step whose undo removes the placement.
            Assert.Equal(1, doc.History.UndoDepth);
            Assert.Null(doc.Doc.Placements[0].Y);            // ground-snap preserved through the adjust
            Assert.True(doc.Undo());
            Assert.Empty(doc.Doc.Placements);
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void PlaceMode_SpawnPressHoldAdjustRelease_OneHistoryEntry()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlaceSpawn;
            c.SpawnArchetype = "wolf";

            c.Update(Press(new Vector3(2f, 100f, 2f)));
            Assert.Single(doc.Doc.Spawns);
            c.Update(Drag(new Vector3(7f, 100f, -1f)));
            Near(7f, doc.Doc.Spawns[0].X);
            Near(-1f, doc.Doc.Spawns[0].Z);
            c.Update(Release(new Vector3(7f, 100f, -1f)));

            Assert.Equal(1, doc.History.UndoDepth);
            Assert.True(doc.Undo());
            Assert.Empty(doc.Doc.Spawns);
        }

        [Fact]
        public void PlaceMode_PlainClick_UnchangedBehavior()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlacePlacement;
            c.PlaceKind = "hut";

            // A plain click (press then release, no hold-move) still lands exactly one Add, one undo step.
            c.Update(Press(new Vector3(5f, 100f, -3f)));
            c.Update(Release(new Vector3(5f, 100f, -3f)));

            Assert.Single(doc.Doc.Placements);
            MapPlacement p = doc.Doc.Placements[0];
            Near(5f, p.X);
            Near(-3f, p.Z);
            Assert.Null(p.Y);
            Assert.Equal("hut", p.Kind);
            Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);
            Assert.Equal(1, doc.History.UndoDepth);

            // A second plain click is its own separate undo step (the release sealed the first).
            c.Update(Press(new Vector3(8f, 100f, 8f)));
            c.Update(Release(new Vector3(8f, 100f, 8f)));
            Assert.Equal(2, doc.Doc.Placements.Count);
            Assert.Equal(2, doc.History.UndoDepth);
        }

        // ---- player spawns (mirror the NPC spawn gestures) ------------------------------------------------

        [Fact]
        public void PlaceSpawn_PlayerStart_GroundSnapsClickIntoAddCommand()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlaceSpawn;
            c.PlacingPlayerSpawn = true;   // the pinned "player spawn" palette entry: a click stamps a player start
            c.SpawnArchetype = "wolf";     // set but ignored while placing a player spawn

            c.Update(Press(new Vector3(2f, 100f, 2f)));

            Assert.Empty(doc.Doc.Spawns);   // an NPC spawn was NOT placed
            Assert.Single(doc.Doc.PlayerSpawns);
            MapPlayerSpawn s = doc.Doc.PlayerSpawns[0];
            Near(2f, s.X);
            Near(2f, s.Z);
            Assert.Equal("player-1", s.Id);   // unique auto-id "player-N"
            Assert.True(s.Enabled);
            Assert.Equal(SelectionKind.PlayerSpawn, doc.Selection.Kind);
            Assert.Equal("player-1", doc.Selection.Id);
        }

        [Fact]
        public void PlaceSpawn_PlayerStart_UniqueIdIsLowestFree()
        {
            var (doc, c) = Make();
            doc.Doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1" });
            c.Mode = EditorToolMode.PlaceSpawn;
            c.PlacingPlayerSpawn = true;

            c.Update(Press(new Vector3(3f, 100f, 4f)));

            Assert.Equal(2, doc.Doc.PlayerSpawns.Count);
            Assert.Equal("player-2", doc.Doc.PlayerSpawns[1].Id);   // player-1 was taken, so the next free is player-2
        }

        [Fact]
        public void SelectDrag_MovesSelectedPlayerSpawn_MergesIntoOneUndoStep()
        {
            var (doc, c) = Make();
            var s = new MapPlayerSpawn { Id = "player-1", X = 0f, Z = 0f };
            doc.Doc.PlayerSpawns.Add(s);

            // Select via pick: the first press falls through to EditorPicking (no gizmo without a selection).
            c.Update(Press(new Vector3(0f, 100f, 0f)));
            Assert.Equal(SelectionKind.PlayerSpawn, doc.Selection.Kind);
            Assert.Equal("player-1", doc.Selection.Id);
            Assert.False(c.IsDragging);

            // Now selected, the Marker gizmo sits at the spawn and draws the XZ arrows. Press the +X ground arrow
            // and drag out along +X. TranslateXZ is the only handle a player spawn honours (RestrictHandle blocks
            // Y / yaw / scale, same as an NPC spawn).
            c.Update(Press(new Vector3(0.6f, 100f, 0f)));
            Assert.True(c.IsDragging);
            c.Update(Drag(new Vector3(2.6f, 100f, 0f)));   // +2 -> X = 2
            c.Update(Drag(new Vector3(5.6f, 100f, 0f)));   // +5 -> X = 5
            c.Update(Release(new Vector3(5.6f, 100f, 0f)));

            Assert.False(c.IsDragging);
            Near(5f, s.X);
            Near(0f, s.Z);
            Assert.Equal(1, doc.History.UndoDepth);   // the whole drag coalesces into one MovePlayerSpawnCommand step

            Assert.True(doc.Undo());
            Near(0f, s.X);                            // undo restores the pre-drag position
            Assert.False(doc.History.CanUndo);
        }

        [Fact]
        public void BodyDrag_MovesSelectedPlayerSpawn_WithoutTouchingArrows()
        {
            var (doc, c) = Make();
            var s = new MapPlayerSpawn { Id = "player-1", X = 0f, Z = 0f };
            doc.Doc.PlayerSpawns.Add(s);
            doc.Selection.Set(SelectionKind.PlayerSpawn, "player-1");

            // Press the body clear of every gizmo handle: selection stays, no drag arms yet.
            c.Update(Press(BodyPoint));
            Assert.False(c.IsDragging);
            Assert.Equal(SelectionKind.PlayerSpawn, doc.Selection.Kind);
            Assert.Equal(0, doc.History.UndoDepth);

            // Past the arming threshold the SAME TranslateXZ path takes over (press ground x = -0.4 dragged to 4.6).
            c.Update(BodyDrag(new Vector3(4.6f, 100f, -0.4f), EditorToolController.BodyDragThreshold + 1f));
            Assert.True(c.IsDragging);
            c.Update(Release(new Vector3(4.6f, 100f, -0.4f)));

            Assert.False(c.IsDragging);
            Near(5f, s.X);
            Near(0f, s.Z);
            Assert.Equal(1, doc.History.UndoDepth);   // the whole body drag is one undo step
        }

        [Fact]
        public void Delete_RemovesSelectedPlayerSpawn()
        {
            var (doc, c) = Make();
            doc.Doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "player-1" });
            doc.Selection.Set(SelectionKind.PlayerSpawn, "player-1");

            c.Update(new EditorFrameInput(default, default, deletePressed: true));

            Assert.Empty(doc.Doc.PlayerSpawns);
            Assert.True(doc.Selection.IsEmpty);
            Assert.Equal(1, doc.History.UndoDepth);
        }

        [Fact]
        public void PlaceMode_PlayerSpawnPressHoldAdjustRelease_OneHistoryEntry()
        {
            var (doc, c) = Make();
            c.Mode = EditorToolMode.PlaceSpawn;
            c.PlacingPlayerSpawn = true;

            c.Update(Press(new Vector3(2f, 100f, 2f)));
            Assert.Single(doc.Doc.PlayerSpawns);
            c.Update(Drag(new Vector3(7f, 100f, -1f)));   // adjust while held: the Add absorbs the same-id Move
            Near(7f, doc.Doc.PlayerSpawns[0].X);
            Near(-1f, doc.Doc.PlayerSpawns[0].Z);
            c.Update(Release(new Vector3(7f, 100f, -1f)));

            Assert.Equal(1, doc.History.UndoDepth);   // place-and-adjust is one undo step
            Assert.True(doc.Undo());
            Assert.Empty(doc.Doc.PlayerSpawns);       // whose undo removes the spawn
        }

        // ---- duplicate (Cmd+D, decision 8) ----------------------------------------------------------------

        // Full-document JSON, for a byte-equal restore check after an undo (cheap here: these are tiny fixtures).
        static string Json(EditorDocument doc) => MapDocumentFile.SaveText(doc.Doc, doc.Registry);

        public static IEnumerable<object[]> DuplicateKinds() => new[]
        {
            new object[] { "placement" },
            new object[] { "spawn" },
            new object[] { "playerSpawn" },
            new object[] { "featureNamed" },
            new object[] { "featureUnnamed" },
            new object[] { "exclusionNamed" },
            new object[] { "exclusionUnnamed" },
            new object[] { "scatterOverrideNamed" },
            new object[] { "scatterOverrideUnnamed" },
            new object[] { "region" },
            new object[] { "biomeBand" },
            new object[] { "scatterLayer" },
            new object[] { "companionLayer" },
        };

        [Theory]
        [MemberData(nameof(DuplicateKinds))]
        public void CmdD_DuplicatesEachKind_UniqueIdentity_OneUndo(string kind)
        {
            var (doc, c) = Make();
            switch (kind)
            {
                case "placement":
                {
                    doc.Doc.Placements.Add(new MapPlacement
                    { Id = "p1", Kind = "hut", X = 1f, Z = 2f, Yaw = 0.3f, Scale = 1.2f, Tags = { "a" } });
                    doc.Selection.Set(SelectionKind.Placement, "p1");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Placements.Count);
                    Assert.Equal(SelectionKind.Placement, doc.Selection.Kind);
                    string newId = doc.Selection.Id;
                    Assert.NotEqual("p1", newId);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Placement, newId), result);
                    MapPlacement dup = doc.Doc.Placements[1];
                    Assert.Equal(newId, dup.Id);
                    Near(3f, dup.X); Near(4f, dup.Z);
                    Assert.Equal("hut", dup.Kind);
                    Near(0.3f, dup.Yaw); Near(1.2f, dup.Scale);
                    Assert.Equal(new List<string> { "a" }, dup.Tags);
                    Assert.NotSame(doc.Doc.Placements[0].Tags, dup.Tags);
                    Near(1f, doc.Doc.Placements[0].X);   // source untouched

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Placements);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "spawn":
                {
                    doc.Doc.Spawns.Add(new MapSpawn
                    { Id = "s1", ArchetypeId = "wolf", X = 5f, Z = 6f, Enabled = false, Tags = { "hostile" } });
                    doc.Selection.Set(SelectionKind.Spawn, "s1");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Spawns.Count);
                    Assert.Equal(SelectionKind.Spawn, doc.Selection.Kind);
                    string newId = doc.Selection.Id;
                    Assert.NotEqual("s1", newId);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Spawn, newId), result);
                    MapSpawn dup = doc.Doc.Spawns[1];
                    Assert.Equal(newId, dup.Id);
                    Near(7f, dup.X); Near(8f, dup.Z);
                    Assert.Equal("wolf", dup.ArchetypeId);
                    Assert.False(dup.Enabled);
                    Assert.Equal(new List<string> { "hostile" }, dup.Tags);
                    Assert.NotSame(doc.Doc.Spawns[0].Tags, dup.Tags);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Spawns);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "playerSpawn":
                {
                    doc.Doc.PlayerSpawns.Add(new MapPlayerSpawn
                    { Id = "player-1", X = 1f, Z = 1f, Yaw = 0.5f, Enabled = true, Tags = { "start" } });
                    doc.Selection.Set(SelectionKind.PlayerSpawn, "player-1");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.PlayerSpawns.Count);
                    Assert.Equal(SelectionKind.PlayerSpawn, doc.Selection.Kind);
                    string newId = doc.Selection.Id;
                    Assert.NotEqual("player-1", newId);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.PlayerSpawn, newId), result);
                    MapPlayerSpawn dup = doc.Doc.PlayerSpawns[1];
                    Assert.Equal(newId, dup.Id);
                    Near(3f, dup.X); Near(3f, dup.Z);
                    Near(0.5f, dup.Yaw);
                    Assert.True(dup.Enabled);
                    Assert.Equal(new List<string> { "start" }, dup.Tags);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.PlayerSpawns);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "featureNamed":
                {
                    doc.Doc.Terrain.Features.Add(new LakeFeatureDoc
                    { Name = "MyLake", CenterX = 1f, CenterZ = 2f, Radius = 5f, Depth = 3f });
                    doc.Selection.Set(SelectionKind.Feature, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Terrain.Features.Count);
                    Assert.Equal(SelectionKind.Feature, doc.Selection.Kind);
                    Assert.Equal("1", doc.Selection.Id);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Feature, "1"), result);
                    var dup = Assert.IsType<LakeFeatureDoc>(doc.Doc.Terrain.Features[1]);
                    // The source is named, and AddFeatureCommand carries no add-time uniqueness guard, so
                    // DuplicateSelection uniquifies it itself: "-copy" via the same UniqueName helper every other
                    // auto-named kind uses, so the first copy is "-copy-1", not the bare "-copy".
                    Assert.Equal("MyLake-copy-1", dup.Name);
                    Near(3f, dup.CenterX); Near(4f, dup.CenterZ);
                    Near(5f, dup.Radius); Near(3f, dup.Depth);
                    Assert.Equal("MyLake", ((LakeFeatureDoc)doc.Doc.Terrain.Features[0]).Name);   // source untouched

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Terrain.Features);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "featureUnnamed":
                {
                    doc.Doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 4f, CenterZ = 5f, Radius = 2f, TargetHeight = 1f });
                    doc.Selection.Set(SelectionKind.Feature, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Feature, "1"), result);
                    var dup = Assert.IsType<FlattenFeatureDoc>(doc.Doc.Terrain.Features[1]);
                    Assert.Null(dup.Name);   // unnamed stays unnamed: nothing to uniquify
                    Near(6f, dup.CenterX); Near(7f, dup.CenterZ);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Terrain.Features);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "exclusionNamed":
                {
                    doc.Doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });   // the Layers filter below must resolve
                    doc.Doc.Exclusions.Add(new MapExclusion
                    {
                        Name = "Zone1",
                        Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 3f },
                        Layers = new List<string> { "trees" },
                    });
                    doc.Selection.Set(SelectionKind.Exclusion, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Exclusions.Count);
                    Assert.Equal(SelectionKind.Exclusion, doc.Selection.Kind);
                    Assert.Equal("1", doc.Selection.Id);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Exclusion, "1"), result);
                    MapExclusion dup = doc.Doc.Exclusions[1];
                    Assert.Equal("Zone1-copy-1", dup.Name);
                    var dupShape = Assert.IsType<DiscShapeDoc>(dup.Shape);
                    Near(3f, dupShape.CenterX); Near(4f, dupShape.CenterZ); Near(3f, dupShape.Radius);
                    Assert.Equal(new List<string> { "trees" }, dup.Layers!);
                    Assert.NotSame(doc.Doc.Exclusions[0].Layers, dup.Layers);
                    Assert.Equal("Zone1", doc.Doc.Exclusions[0].Name);   // source untouched

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Exclusions);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "exclusionUnnamed":
                {
                    doc.Doc.Exclusions.Add(new MapExclusion { Shape = new RectShapeDoc { MinX = 0f, MinZ = 0f, MaxX = 2f, MaxZ = 2f } });
                    doc.Selection.Set(SelectionKind.Exclusion, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Exclusion, "1"), result);
                    MapExclusion dup = doc.Doc.Exclusions[1];
                    Assert.Null(dup.Name);
                    var dupShape = Assert.IsType<RectShapeDoc>(dup.Shape);
                    Near(2f, dupShape.MinX); Near(2f, dupShape.MinZ); Near(4f, dupShape.MaxX); Near(4f, dupShape.MaxZ);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Exclusions);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "scatterOverrideNamed":
                {
                    doc.Doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });   // the Layers filter below must resolve
                    doc.Doc.ScatterOverrides.Add(new MapScatterOverrideDoc
                    {
                        Name = "Grove",
                        Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 2f, Radius = 3f },
                        DensityMultiplier = 2.5f,
                        Kinds = new List<MapPropKind> { new MapPropKind { Id = "pine", Weight = 2f } },
                        Layers = new List<string> { "trees" },
                    });
                    doc.Selection.Set(SelectionKind.ScatterOverride, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.ScatterOverrides.Count);
                    Assert.Equal(SelectionKind.ScatterOverride, doc.Selection.Kind);
                    Assert.Equal("1", doc.Selection.Id);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.ScatterOverride, "1"), result);
                    MapScatterOverrideDoc dup = doc.Doc.ScatterOverrides[1];
                    Assert.Equal("Grove-copy-1", dup.Name);
                    var dupShape = Assert.IsType<DiscShapeDoc>(dup.Shape);
                    Near(3f, dupShape.CenterX); Near(4f, dupShape.CenterZ); Near(3f, dupShape.Radius);
                    Near(2.5f, dup.DensityMultiplier);
                    Assert.Single(dup.Kinds!);
                    Assert.Equal("pine", dup.Kinds![0].Id);
                    Near(2f, dup.Kinds[0].Weight);
                    Assert.Equal(new List<string> { "trees" }, dup.Layers!);
                    // Fresh containers AND fresh MapPropKind elements: the Values command copies the Kinds list but
                    // not its elements, so Duplicate must rebuild each MapPropKind or a scrub of the clone's kind
                    // would mutate the source's.
                    Assert.NotSame(doc.Doc.ScatterOverrides[0].Kinds, dup.Kinds);
                    Assert.NotSame(doc.Doc.ScatterOverrides[0].Kinds![0], dup.Kinds[0]);
                    Assert.NotSame(doc.Doc.ScatterOverrides[0].Layers, dup.Layers);
                    Assert.Equal("Grove", doc.Doc.ScatterOverrides[0].Name);   // source untouched

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.ScatterOverrides);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "scatterOverrideUnnamed":
                {
                    doc.Doc.ScatterOverrides.Add(new MapScatterOverrideDoc { Shape = new RectShapeDoc { MinX = 0f, MinZ = 0f, MaxX = 2f, MaxZ = 2f } });
                    doc.Selection.Set(SelectionKind.ScatterOverride, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.ScatterOverride, "1"), result);
                    MapScatterOverrideDoc dup = doc.Doc.ScatterOverrides[1];
                    Assert.Null(dup.Name);   // unnamed stays unnamed: nothing to uniquify
                    Assert.Null(dup.Kinds);  // absent lists stay absent (not empty)
                    Assert.Null(dup.Layers);
                    var dupShape = Assert.IsType<RectShapeDoc>(dup.Shape);
                    Near(2f, dupShape.MinX); Near(2f, dupShape.MinZ); Near(4f, dupShape.MaxX); Near(4f, dupShape.MaxZ);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.ScatterOverrides);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "region":
                {
                    doc.Doc.Regions.Add(new MapRegion
                    { Name = "town", Shape = new DiscShapeDoc { CenterX = 5f, CenterZ = 5f, Radius = 2f }, Tags = { "safe" } });
                    doc.Selection.Set(SelectionKind.Region, "town");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Regions.Count);
                    Assert.Equal(SelectionKind.Region, doc.Selection.Kind);
                    string newName = doc.Selection.Id;
                    // A region's name IS its identity (always set, always unique), so a duplicate takes the
                    // standard generated "region-N" name exactly like a freshly drawn region, not "town-copy".
                    Assert.Equal("region-1", newName);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.Region, newName), result);
                    MapRegion dup = doc.Doc.Regions[1];
                    Assert.Equal(newName, dup.Name);
                    var dupShape = Assert.IsType<DiscShapeDoc>(dup.Shape);
                    Near(7f, dupShape.CenterX); Near(7f, dupShape.CenterZ);
                    Assert.Equal(new List<string> { "safe" }, dup.Tags);
                    Assert.NotSame(doc.Doc.Regions[0].Tags, dup.Tags);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Regions);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "biomeBand":
                {
                    doc.Doc.Terrain.Biomes.Add(new MapBiomeBand
                    { Start = 10f, End = 20f, Biome = BiomeId.Meadow, BaseHeight = 1f, HillAmplitude = 0.5f });
                    doc.Selection.Set(SelectionKind.BiomeBand, "0");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.Terrain.Biomes.Count);
                    Assert.Equal(SelectionKind.BiomeBand, doc.Selection.Kind);
                    Assert.Equal("1", doc.Selection.Id);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.BiomeBand, "1"), result);
                    MapBiomeBand dup = doc.Doc.Terrain.Biomes[1];
                    // A band has no name and no position (an elevation range, not a placed element): a verbatim
                    // clone, distinct instance, no offset.
                    Assert.Equal(10f, dup.Start);
                    Assert.Equal(20f, dup.End);
                    Assert.Equal(BiomeId.Meadow, dup.Biome);
                    Near(1f, dup.BaseHeight); Near(0.5f, dup.HillAmplitude);
                    Assert.NotSame(doc.Doc.Terrain.Biomes[0], dup);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.Terrain.Biomes);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "scatterLayer":
                {
                    doc.Doc.ScatterLayers.Add(new MapScatterLayer
                    {
                        Name = "trees", Seed = 42, CellSize = 4f, Jitter = 1f, MaxHeight = 10f, ScaleMin = 0.5f, ScaleMax = 1.5f,
                        Rules = { new MapBiomeScatterRule { Biome = BiomeId.Meadow, Density = 0.5f, Kinds = { new MapPropKind { Id = "pine", Weight = 2f } } } },
                    });
                    doc.Selection.Set(SelectionKind.ScatterLayer, "trees");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);
                    Assert.Equal(2, doc.Doc.ScatterLayers.Count);
                    Assert.Equal(SelectionKind.ScatterLayer, doc.Selection.Kind);
                    string newName = doc.Selection.Id;
                    // Name-keyed, no position: UniqueName("trees-copy", exists) always appends "-N" from 1, so
                    // the first copy is "trees-copy-1" (never the bare "trees-copy").
                    Assert.Equal("trees-copy-1", newName);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.ScatterLayer, newName), result);
                    MapScatterLayer dup = doc.Doc.ScatterLayers[1];
                    Assert.Equal(newName, dup.Name);
                    Assert.Equal(42, dup.Seed);
                    Near(4f, dup.CellSize); Near(1f, dup.Jitter);
                    Assert.Equal(10f, dup.MaxHeight);
                    Near(0.5f, dup.ScaleMin); Near(1.5f, dup.ScaleMax);
                    Assert.Single(dup.Rules);
                    Assert.Equal(BiomeId.Meadow, dup.Rules[0].Biome);
                    Near(0.5f, dup.Rules[0].Density);
                    Assert.Single(dup.Rules[0].Kinds);
                    Assert.Equal("pine", dup.Rules[0].Kinds[0].Id);
                    Near(2f, dup.Rules[0].Kinds[0].Weight);
                    Assert.NotSame(doc.Doc.ScatterLayers[0].Rules, dup.Rules);
                    Assert.NotSame(doc.Doc.ScatterLayers[0].Rules[0].Kinds, dup.Rules[0].Kinds);
                    Assert.Equal("trees", doc.Doc.ScatterLayers[0].Name);   // source untouched

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.ScatterLayers);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                case "companionLayer":
                {
                    doc.Doc.ScatterLayers.Add(new MapScatterLayer { Name = "trees" });
                    doc.Doc.CompanionLayers.Add(new MapCompanionLayer
                    {
                        Name = "ring", HostLayer = "trees", Seed = 7, HostKinds = { "pine" },
                        Kinds = { new MapPropKind { Id = "bush", Weight = 1f } },
                        CountMin = 2, CountMax = 4, RadiusMin = 0.5f, RadiusMax = 1f, ScaleMin = 0.8f, ScaleMax = 1.2f, MaxHeight = 5f,
                    });
                    doc.Selection.Set(SelectionKind.CompanionLayer, "ring");
                    string before = Json(doc);

                    EditorToolController.DuplicateResult? result = c.DuplicateSelection();

                    Assert.Equal(1, doc.History.UndoDepth);   // the scatter layer above landed no command (direct Add)
                    Assert.Equal(2, doc.Doc.CompanionLayers.Count);
                    Assert.Equal(SelectionKind.CompanionLayer, doc.Selection.Kind);
                    string newName = doc.Selection.Id;
                    Assert.Equal("ring-copy-1", newName);
                    Assert.Equal(new EditorToolController.DuplicateResult(SelectionKind.CompanionLayer, newName), result);
                    MapCompanionLayer dup = doc.Doc.CompanionLayers[1];
                    Assert.Equal(newName, dup.Name);
                    Assert.Equal("trees", dup.HostLayer);
                    Assert.Equal(7, dup.Seed);
                    Assert.Equal(new List<string> { "pine" }, dup.HostKinds);
                    Assert.Single(dup.Kinds);
                    Assert.Equal("bush", dup.Kinds[0].Id);
                    Assert.Equal(2, dup.CountMin); Assert.Equal(4, dup.CountMax);
                    Near(0.5f, dup.RadiusMin); Near(1f, dup.RadiusMax);
                    Near(0.8f, dup.ScaleMin); Near(1.2f, dup.ScaleMax);
                    Assert.Equal(5f, dup.MaxHeight);
                    Assert.NotSame(doc.Doc.CompanionLayers[0].HostKinds, dup.HostKinds);
                    Assert.NotSame(doc.Doc.CompanionLayers[0].Kinds, dup.Kinds);

                    Assert.True(doc.Undo());
                    Assert.Single(doc.Doc.CompanionLayers);
                    Assert.Equal(before, Json(doc));
                    break;
                }
                default:
                    Assert.Fail($"unhandled duplicate-kind case '{kind}'");
                    break;
            }
        }

        [Fact]
        public void CmdD_TerrainSelected_NoOp()
        {
            var (doc, c) = Make();
            doc.Selection.Set(SelectionKind.Terrain, "");

            EditorToolController.DuplicateResult? result = c.DuplicateSelection();

            Assert.False(doc.History.CanUndo);
            Assert.Equal(SelectionKind.Terrain, doc.Selection.Kind);
            Assert.Null(result);
        }

        [Fact]
        public void CmdD_EmptySelection_IsNoOp()
        {
            var (doc, c) = Make();
            Assert.True(doc.Selection.IsEmpty);

            EditorToolController.DuplicateResult? result = c.DuplicateSelection();

            Assert.False(doc.History.CanUndo);
            Assert.True(doc.Selection.IsEmpty);
            Assert.Null(result);
        }

        // A custom feature type FeatureGeometry.Translated does not know how to offset (not one of the four
        // built-ins), the same "unknown type, no guess" case MapEditorOverlayTests.UnknownFeatureDoc covers for
        // the overlay draw list. DuplicateSelection must no-op (null result, no command, selection untouched)
        // rather than adding an un-offset clone, so an automation caller sees a clean "nothing happened" signal
        // instead of a false success.
        sealed class UnknownFeatureDoc : MapFeature
        {
            public override string Type => "unknown";
        }

        [Fact]
        public void CmdD_CustomFeatureType_NoOp()
        {
            // No Json(doc) before/after round-trip here (unlike the theory above): "unknown" is not a type the
            // default MapDocRegistry knows, so serializing it would throw. The in-memory assertions below are
            // enough to confirm nothing was mutated.
            var (doc, c) = Make();
            var source = new UnknownFeatureDoc { Name = "mystery" };
            doc.Doc.Terrain.Features.Add(source);
            doc.Selection.Set(SelectionKind.Feature, "0");

            EditorToolController.DuplicateResult? result = c.DuplicateSelection();

            Assert.Null(result);
            Assert.False(doc.History.CanUndo);
            Assert.Single(doc.Doc.Terrain.Features);
            Assert.Same(source, doc.Doc.Terrain.Features[0]);
            Assert.Equal(SelectionKind.Feature, doc.Selection.Kind);
            Assert.Equal("0", doc.Selection.Id);   // selection untouched, nothing was created to select
        }

        [Fact]
        public void CmdD_Offset_AppliedToPositionedKinds()
        {
            var (doc, c) = Make();

            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f });
            doc.Selection.Set(SelectionKind.Placement, "p1");
            c.DuplicateSelection();
            Near(2f, doc.Doc.Placements[1].X); Near(2f, doc.Doc.Placements[1].Z);
            Near(0f, doc.Doc.Placements[0].X); Near(0f, doc.Doc.Placements[0].Z);   // source untouched

            doc.Doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 10f, CenterZ = 10f, Radius = 2f, Depth = 1f });
            doc.Selection.Set(SelectionKind.Feature, "0");
            c.DuplicateSelection();
            var dupLake = Assert.IsType<LakeFeatureDoc>(doc.Doc.Terrain.Features[1]);
            Near(12f, dupLake.CenterX); Near(12f, dupLake.CenterZ);
            Near(10f, ((LakeFeatureDoc)doc.Doc.Terrain.Features[0]).CenterX);   // source untouched

            doc.Doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 1f, Radius = 1f } });
            doc.Selection.Set(SelectionKind.Exclusion, "0");
            c.DuplicateSelection();
            var dupDisc = Assert.IsType<DiscShapeDoc>(doc.Doc.Exclusions[1].Shape);
            Near(3f, dupDisc.CenterX); Near(3f, dupDisc.CenterZ);
            Near(1f, ((DiscShapeDoc)doc.Doc.Exclusions[0].Shape!).CenterX);   // source untouched

            doc.Doc.Regions.Add(new MapRegion { Name = "zone", Shape = new RectShapeDoc { MinX = 0f, MinZ = 0f, MaxX = 4f, MaxZ = 4f } });
            doc.Selection.Set(SelectionKind.Region, "zone");
            c.DuplicateSelection();
            var dupRect = Assert.IsType<RectShapeDoc>(doc.Doc.Regions[1].Shape);
            Near(2f, dupRect.MinX); Near(2f, dupRect.MinZ); Near(6f, dupRect.MaxX); Near(6f, dupRect.MaxZ);
            Near(0f, ((RectShapeDoc)doc.Doc.Regions[0].Shape!).MinX);   // source untouched
        }

        [Fact]
        public void CmdD_ThenDragTheDuplicate_TwoSeparateUndoSteps()
        {
            // Guards the TryMerge safety point (task 4 brief): a duplicate calls SealGesture right after its Add
            // (mirroring DeleteSelection), so a LATER drag of the fresh duplicate can never silently fold into
            // the Add's own undo step. Place-and-adjust is for an actual place gesture, not a duplicate.
            var (doc, c) = Make();
            doc.Doc.Placements.Add(new MapPlacement { Id = "p1", Kind = "hut", X = 0f, Z = 0f });
            doc.Selection.Set(SelectionKind.Placement, "p1");

            c.DuplicateSelection();
            Assert.Equal(1, doc.History.UndoDepth);
            string dupId = doc.Selection.Id;

            // The exact same-id Move an Add would absorb mid-gesture, executed directly here (the gizmo drag
            // plumbing is covered elsewhere): without the seal this would silently merge into the duplicate's Add.
            doc.Execute(new MovePlacementCommand(dupId, 50f, 50f, null));

            Assert.Equal(2, doc.History.UndoDepth);   // two separate steps, not merged into one

            Assert.True(doc.Undo());
            Assert.Equal(1, doc.History.UndoDepth);
            Near(2f, doc.Doc.Placements[1].X);   // the move undid, the duplicate (at its +2 offset) is still there

            Assert.True(doc.Undo());
            Assert.Single(doc.Doc.Placements);   // the duplicate itself undid too
        }
    }
}
