using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the concrete editor commands and the EditorDocument choke point: apply/revert
    /// deep-equality round-trips (compared via <see cref="MapDocumentFile.SaveText"/> string equality, the MapDoc
    /// idiom), same-gesture merge coalescing, world-rebuild classification, saved-point dirty tracking, and the
    /// DocumentChanged signal.</summary>
    public class EditorCommandsTests
    {
        static MapDocument Sample() => KhaozEngine.Tests.MapDoc.MapDocumentFileTests.SampleDoc();

        static string Save(MapDocument d) => MapDocumentFile.SaveText(d);

        static MapPlacement P(string id, float x = 0f, float z = 0f) =>
            new MapPlacement { Id = id, Kind = "prop", X = x, Z = z };

        static MapPlacement FindP(MapDocument d, string id) => d.Placements.First(p => p.Id == id);

        /// <summary>Executes a command through an EditorDocument, asserts it mutated the document, then undoes it
        /// and asserts the serialized form is byte-identical to the pre-edit document (deep equality).</summary>
        static void AssertRoundTrip(MapDocument doc, IEditorCommand command)
        {
            string before = Save(doc);
            var ed = new EditorDocument(doc);
            ed.Execute(command);
            Assert.NotEqual(before, Save(doc));
            Assert.True(ed.History.CanUndo);
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
        }

        // ---- apply/revert round-trips, one per command -------------------------------------------------

        [Fact]
        public void AddPlacement_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddPlacementCommand(new MapPlacement { Id = "obelisk", Kind = "rock", X = 5f, Z = 5f }));

        [Fact]
        public void RemovePlacement_RoundTrips() =>
            AssertRoundTrip(Sample(), new RemovePlacementCommand("inn"));

        [Fact]
        public void MovePlacement_RoundTrips() =>
            AssertRoundTrip(Sample(), new MovePlacementCommand("inn", 99f, 88f, 7f));

        [Fact]
        public void RotatePlacement_RoundTrips() =>
            AssertRoundTrip(Sample(), new RotatePlacementCommand("inn", 2.5f));

        [Fact]
        public void ScalePlacement_RoundTrips() =>
            AssertRoundTrip(Sample(), new ScalePlacementCommand("inn", 2f));

        [Fact]
        public void AddSpawn_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddSpawnCommand(new MapSpawn { Id = "bear-1", ArchetypeId = "bear", X = 4f, Z = 9f }));

        [Fact]
        public void RemoveSpawn_RoundTrips() =>
            AssertRoundTrip(Sample(), new RemoveSpawnCommand("wolf-1"));

        [Fact]
        public void MoveSpawn_RoundTrips() =>
            AssertRoundTrip(Sample(), new MoveSpawnCommand("wolf-1", 50f, 60f));

        [Fact]
        public void SetSpawnEnabled_RoundTrips() =>
            AssertRoundTrip(Sample(), new SetSpawnEnabledCommand("wolf-1", false));

        [Fact]
        public void AddExclusion_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddExclusionCommand(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 1f, Radius = 5f } }));

        [Fact]
        public void RemoveExclusion_RoundTrips() =>
            AssertRoundTrip(Sample(), new RemoveExclusionCommand(0));

        [Fact]
        public void AddRegion_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddRegionCommand(new MapRegion { Name = "camp", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 3f } }));

        [Fact]
        public void RemoveRegion_RoundTrips() =>
            AssertRoundTrip(Sample(), new RemoveRegionCommand("town"));

        [Fact]
        public void RenameRegion_RoundTrips() =>
            AssertRoundTrip(Sample(), new RenameRegionCommand("town", "village"));

        [Fact]
        public void RenameRegionCommand_GuardsDuplicates()
        {
            var doc = Sample();
            doc.Regions.Add(new MapRegion { Name = "village", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 3f } });   // a second region, so a duplicate target name exists

            // Guards duplicates: renaming onto a name already in the document throws (the GuardNoRegion throw-pattern)
            // and leaves the history untouched, because History.Execute applies BEFORE it pushes, so a throwing
            // Apply never lands an undo step or mutates the source name.
            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameRegionCommand("town", "village")));
            Assert.False(ed.History.CanUndo);
            Assert.Contains(doc.Regions, r => r.Name == "town");     // source name intact
            Assert.Single(doc.Regions, r => r.Name == "village");    // target still unique: no clone
        }

        [Fact]
        public void RenamePlacementCommand_AppliesRevertsAndGuardsDuplicates()
        {
            var doc = Sample();
            doc.Placements.Add(P("shed"));   // a second placement, so a duplicate target id exists

            // Applies and reverts: "inn" -> "tavern" round-trips deep-equal (id-keyed selection would follow).
            AssertRoundTrip(doc, new RenamePlacementCommand("inn", "tavern"));

            // Guards duplicates: renaming onto an id already in the document throws (the FindPlacement throw-pattern)
            // and leaves the history untouched, because History.Execute applies BEFORE it pushes, so a throwing
            // Apply never lands an undo step or mutates the source id.
            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenamePlacementCommand("inn", "shed")));
            Assert.False(ed.History.CanUndo);
            Assert.Contains(doc.Placements, p => p.Id == "inn");    // source id intact
            Assert.Single(doc.Placements, p => p.Id == "shed");     // target still unique: no clone
        }

        [Fact]
        public void RenameSpawnCommand_AppliesRevertsAndGuardsDuplicates()
        {
            var doc = Sample();
            doc.Spawns.Add(new MapSpawn { Id = "bear-1", ArchetypeId = "bear", X = 4f, Z = 9f });

            AssertRoundTrip(doc, new RenameSpawnCommand("wolf-1", "wolf-2"));

            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameSpawnCommand("wolf-1", "bear-1")));
            Assert.False(ed.History.CanUndo);
            Assert.Contains(doc.Spawns, s => s.Id == "wolf-1");
            Assert.Single(doc.Spawns, s => s.Id == "bear-1");
        }

        [Fact]
        public void EditFeature_RoundTrips()
        {
            var doc = Sample();
            MapFeature old = doc.Terrain.Features[0];
            var updated = new LakeFeatureDoc { CenterX = 1f, CenterZ = 2f, Radius = 9f, Depth = 4f };
            AssertRoundTrip(doc, new EditFeatureCommand(0, updated, old));
        }

        // ---- removed placement restores at its original index ------------------------------------------

        [Fact]
        public void RemovePlacement_RestoresAtOriginalIndex()
        {
            var doc = Sample();
            doc.Placements.Clear();
            doc.Placements.Add(P("a"));
            doc.Placements.Add(P("b"));
            doc.Placements.Add(P("c"));
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new RemovePlacementCommand("b"));
            Assert.Equal(2, doc.Placements.Count);
            Assert.DoesNotContain(doc.Placements, p => p.Id == "b");

            ed.Undo();
            Assert.Equal(3, doc.Placements.Count);
            Assert.Equal("b", doc.Placements[1].Id);   // back at the middle, not appended
            Assert.Equal(before, Save(doc));
        }

        // ---- gesture merge coalescing ------------------------------------------------------------------

        [Fact]
        public void MovePlacement_MergesSameId()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new MovePlacementCommand("inn", 1f, 1f, null));
            ed.Execute(new MovePlacementCommand("inn", 2f, 2f, null));
            ed.Execute(new MovePlacementCommand("inn", 3f, 3f, null));
            Assert.Equal(3f, FindP(doc, "inn").X);

            Assert.True(ed.Undo());
            Assert.Equal(-30f, FindP(doc, "inn").X);    // SampleDoc's inn origin
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void RotatePlacement_MergesSameId()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new RotatePlacementCommand("inn", 1f));
            ed.Execute(new RotatePlacementCommand("inn", 2f));
            Assert.Equal(2f, FindP(doc, "inn").Yaw);

            Assert.True(ed.Undo());
            Assert.Equal(1.2f, FindP(doc, "inn").Yaw);  // SampleDoc's inn yaw
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void ScalePlacement_MergesSameId()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new ScalePlacementCommand("inn", 2f));
            ed.Execute(new ScalePlacementCommand("inn", 3f));
            Assert.Equal(3f, FindP(doc, "inn").Scale);

            Assert.True(ed.Undo());
            Assert.Equal(1f, FindP(doc, "inn").Scale);
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void MoveSpawn_MergesSameId()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new MoveSpawnCommand("wolf-1", 1f, 1f));
            ed.Execute(new MoveSpawnCommand("wolf-1", 2f, 2f));
            Assert.Equal(2f, doc.Spawns.First(s => s.Id == "wolf-1").X);

            Assert.True(ed.Undo());
            Assert.Equal(20f, doc.Spawns.First(s => s.Id == "wolf-1").X);   // SampleDoc's wolf-1 origin
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void EditFeature_MergesSameIndex()
        {
            var doc = Sample();
            MapFeature original = doc.Terrain.Features[0];
            var v1 = new LakeFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 10f, Depth = 2f };
            var v2 = new LakeFeatureDoc { CenterX = 2f, CenterZ = 2f, Radius = 11f, Depth = 3f };
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new EditFeatureCommand(0, v1, original));
            ed.Execute(new EditFeatureCommand(0, v2, v1));
            Assert.Same(v2, doc.Terrain.Features[0]);

            Assert.True(ed.Undo());
            Assert.Same(original, doc.Terrain.Features[0]);   // back to the original feature in one step
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));
        }

        [Fact]
        public void EditTerrainCommand_AppliesRevertsAndMerges()
        {
            var doc = Sample();
            float original = doc.Terrain.WaterLevel;   // SampleDoc's -0.5
            var ed = new EditorDocument(doc);

            // Apply sets the new water level and forces a wholesale world rebuild (scatter honours water).
            ed.Execute(new EditTerrainCommand(newWaterLevel: -1.2f, oldWaterLevel: original));
            Assert.Equal(-1.2f, doc.Terrain.WaterLevel);
            Assert.True(ed.WorldRebuildPending);

            // A second edit of the same gesture coalesces into the first (scrub coalescing), so the level moves
            // but no new undo step is pushed.
            ed.Execute(new EditTerrainCommand(newWaterLevel: -2.4f, oldWaterLevel: -1.2f));
            Assert.Equal(-2.4f, doc.Terrain.WaterLevel);

            // One undo reverts the whole scrub back to the pre-edit level, and nothing is left to undo.
            Assert.True(ed.Undo());
            Assert.Equal(original, doc.Terrain.WaterLevel);
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void EditFeature_DifferentIndex_DoesNotMerge()
        {
            var doc = Sample();
            MapFeature f0 = doc.Terrain.Features[0];
            MapFeature f1 = doc.Terrain.Features[1];
            var newF0 = new LakeFeatureDoc { CenterX = 9f, CenterZ = 9f, Radius = 5f, Depth = 1f };
            var newF1 = new FlattenFeatureDoc { CenterX = 8f, CenterZ = 8f, Radius = 6f, TargetHeight = 3f };

            var ed = new EditorDocument(doc);
            ed.Execute(new EditFeatureCommand(0, newF0, f0));
            ed.Execute(new EditFeatureCommand(1, newF1, f1));

            Assert.True(ed.Undo());                       // undoes index 1 only
            Assert.Same(f1, doc.Terrain.Features[1]);
            Assert.Same(newF0, doc.Terrain.Features[0]);
            Assert.True(ed.History.CanUndo);
        }

        [Fact]
        public void EditExclusionShapeCommand_AppliesRevertsMerges()
        {
            var doc = Sample();
            var original = Assert.IsType<DiscShapeDoc>(doc.Exclusions[0].Shape);
            var v1 = new DiscShapeDoc { CenterX = -32f, CenterZ = 22f, Radius = 40f };
            var v2 = new RectShapeDoc { MinX = -72f, MinZ = -18f, MaxX = 8f, MaxZ = 62f };
            string before = Save(doc);

            // Apply replaces the shape instance and forces a world rebuild (scatter inputs changed).
            var ed = new EditorDocument(doc);
            ed.Execute(new EditExclusionShapeCommand(0, v1, original));
            Assert.Same(v1, doc.Exclusions[0].Shape);
            Assert.True(ed.WorldRebuildPending);

            // A second edit of the same index coalesces (scrub coalescing): the shape moves on, no new undo step.
            ed.Execute(new EditExclusionShapeCommand(0, v2, v1));
            Assert.Same(v2, doc.Exclusions[0].Shape);

            // One undo reverts the whole gesture to the original shape, deep-equal to the pre-edit document.
            Assert.True(ed.Undo());
            Assert.Same(original, doc.Exclusions[0].Shape);
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));
        }

        [Fact]
        public void EditRegionShapeCommand_AppliesRevertsMerges_NoWorldRebuild()
        {
            var doc = Sample();
            var original = Assert.IsType<DiscShapeDoc>(doc.Regions[0].Shape);   // SampleDoc's "town"
            var v1 = new DiscShapeDoc { CenterX = -32f, CenterZ = 22f, Radius = 20f };
            var v2 = new DiscShapeDoc { CenterX = -32f, CenterZ = 22f, Radius = 21f };
            string before = Save(doc);

            // Apply replaces the shape; regions are game-interpreted, so no world rebuild is forced.
            var ed = new EditorDocument(doc);
            ed.Execute(new EditRegionShapeCommand("town", v1, original));
            Assert.Same(v1, doc.Regions[0].Shape);
            Assert.False(ed.WorldRebuildPending);

            // A second edit of the same region name coalesces into the first.
            ed.Execute(new EditRegionShapeCommand("town", v2, v1));
            Assert.Same(v2, doc.Regions[0].Shape);

            Assert.True(ed.Undo());
            Assert.Same(original, doc.Regions[0].Shape);
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));
        }

        // ---- world-rebuild classification --------------------------------------------------------------

        [Fact]
        public void WorldRebuildPending_TrueOnlyForAffectsWorldCommands()
        {
            var ed = new EditorDocument(Sample());
            Assert.False(ed.WorldRebuildPending);

            ed.Execute(new AddPlacementCommand(P("x", 1f, 1f)));
            Assert.False(ed.WorldRebuildPending);          // placement edits leave it false

            ed.Execute(new MoveSpawnCommand("wolf-1", 3f, 3f));
            Assert.False(ed.WorldRebuildPending);          // spawn edits leave it false

            ed.Execute(new AddRegionCommand(new MapRegion { Name = "r", Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 1f } }));
            Assert.False(ed.WorldRebuildPending);          // region edits leave it false

            ed.Execute(new AddExclusionCommand(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 2f } }));
            Assert.True(ed.WorldRebuildPending);           // exclusion edits force a rebuild

            ed.AcknowledgeWorldRebuild();
            Assert.False(ed.WorldRebuildPending);

            var updated = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 3f, Depth = 1f };
            ed.Execute(new EditFeatureCommand(0, updated, ed.Doc.Terrain.Features[0]));
            Assert.True(ed.WorldRebuildPending);           // feature edits force a rebuild

            ed.AcknowledgeWorldRebuild();
            Assert.False(ed.WorldRebuildPending);
        }

        [Fact]
        public void WorldRebuildPending_SetOnUndoOfAffectsWorldCommand()
        {
            var ed = new EditorDocument(Sample());
            ed.Execute(new AddExclusionCommand(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 2f } }));
            ed.AcknowledgeWorldRebuild();
            Assert.False(ed.WorldRebuildPending);

            ed.Undo();
            Assert.True(ed.WorldRebuildPending);           // undoing a terrain-shape edit also needs a rebuild

            ed.AcknowledgeWorldRebuild();
            ed.Redo();
            Assert.True(ed.WorldRebuildPending);
        }

        // ---- saved-point dirty tracking ----------------------------------------------------------------

        [Fact]
        public void IsDirty_TracksSavedPointAcrossExecuteUndoRedo()
        {
            var ed = new EditorDocument(Sample());
            Assert.False(ed.IsDirty);

            ed.Execute(new AddPlacementCommand(P("x")));
            Assert.True(ed.IsDirty);

            ed.MarkSaved();
            Assert.False(ed.IsDirty);

            ed.Execute(new AddPlacementCommand(P("y")));
            Assert.True(ed.IsDirty);

            ed.Undo();                                     // back to the saved point
            Assert.False(ed.IsDirty);

            ed.Undo();                                     // before the saved point
            Assert.True(ed.IsDirty);

            ed.Redo();                                     // forward to the saved point
            Assert.False(ed.IsDirty);
        }

        [Fact]
        public void SealGesture_SplitsSameIdDrags()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new MovePlacementCommand("inn", 1f, 1f, null));
            ed.Execute(new MovePlacementCommand("inn", 2f, 2f, null));   // merges: one drag gesture
            ed.SealGesture();                                            // drag release
            ed.SealGesture();                                            // idempotent
            ed.Execute(new MovePlacementCommand("inn", 3f, 3f, null));   // a second drag on the same id

            Assert.True(ed.Undo());                        // undoes the second drag only
            Assert.Equal(2f, FindP(doc, "inn").X);         // back to the seal point
            Assert.True(ed.History.CanUndo);

            Assert.True(ed.Undo());                        // then the first drag as its own step
            Assert.Equal(-30f, FindP(doc, "inn").X);       // SampleDoc's inn origin
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void Merge_DoesNotCrossMarkSaved()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new MovePlacementCommand("inn", 1f, 1f, null));
            ed.MarkSaved();
            string saved = Save(doc);

            // A save is a gesture boundary: the next same-id move must NOT merge into the saved
            // command, otherwise the depth marker still matches and IsDirty silently reads false
            // while the document changed (unsaved-change loss on close).
            ed.Execute(new MovePlacementCommand("inn", 2f, 2f, null));
            Assert.True(ed.IsDirty);

            Assert.True(ed.Undo());                        // one step back to the saved state
            Assert.Equal(saved, Save(doc));
            Assert.False(ed.IsDirty);
        }

        [Fact]
        public void IsDirty_DiscardedSavedBranch_StaysDirty()
        {
            var ed = new EditorDocument(Sample());
            ed.Execute(new AddPlacementCommand(P("x")));
            ed.MarkSaved();
            ed.Undo();
            Assert.True(ed.IsDirty);                       // one edit below the saved point

            // A fresh edit here discards the redo branch that held the saved point, so the saved state is
            // no longer reachable and the document must read dirty even though the depth matches again.
            ed.Execute(new AddPlacementCommand(P("y")));
            Assert.True(ed.IsDirty);
        }

        // ---- DocumentChanged and selection stub --------------------------------------------------------

        [Fact]
        public void DocumentChanged_FiresPerMutation()
        {
            var ed = new EditorDocument(Sample());
            int n = 0;
            ed.DocumentChanged += () => n++;

            ed.Execute(new AddPlacementCommand(P("x")));
            Assert.Equal(1, n);
            ed.Undo();
            Assert.Equal(2, n);
            ed.Redo();
            Assert.Equal(3, n);
        }

        [Fact]
        public void EditorDocument_SelectionStartsEmpty_AndRegistryDefaults()
        {
            var ed = new EditorDocument(Sample());
            Assert.True(ed.Selection.IsEmpty);
            Assert.Equal(SelectionKind.None, ed.Selection.Kind);
            Assert.NotNull(ed.Registry);
        }
    }
}
