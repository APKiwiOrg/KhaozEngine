using System;
using System.Collections.Generic;
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
        public void RenameRegion_NowMerges()
        {
            var doc = Sample();
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // The ledgered per-keystroke-undo wart: before the TryMerge retrofit, each keystroke pushed its own
            // undo step. Successive renames of the same region (each one's old name matching the prior new name,
            // the same-name-pair chain a per-keystroke commit produces) now coalesce into one undo step.
            ed.Execute(new RenameRegionCommand("town", "t"));
            ed.Execute(new RenameRegionCommand("t", "to"));
            ed.Execute(new RenameRegionCommand("to", "town2"));
            Assert.Equal(1, ed.History.UndoDepth);
            Assert.Contains(doc.Regions, r => r.Name == "town2");

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
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

        // ---- player spawns (mirror the NPC spawn family) ----------------------------------------------

        [Fact]
        public void AddPlayerSpawn_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = "start", X = 4f, Z = 9f, Yaw = 0.5f }));

        [Fact]
        public void RemovePlayerSpawn_RoundTrips()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 3f, Z = 3f });
            AssertRoundTrip(doc, new RemovePlayerSpawnCommand("start"));
        }

        [Fact]
        public void MovePlayerSpawn_RoundTrips()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 3f, Z = 3f });
            AssertRoundTrip(doc, new MovePlayerSpawnCommand("start", 50f, 60f));
        }

        [Fact]
        public void SetPlayerSpawnEnabled_RoundTrips()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 3f, Z = 3f });
            AssertRoundTrip(doc, new SetPlayerSpawnEnabledCommand("start", false));
        }

        [Fact]
        public void SetPlayerSpawnYaw_RoundTrips()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 3f, Z = 3f, Yaw = 0.5f });
            AssertRoundTrip(doc, new SetPlayerSpawnYawCommand("start", 2.5f));
        }

        [Fact]
        public void RenamePlayerSpawnCommand_AppliesRevertsAndGuardsDuplicates()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 4f, Z = 9f });
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "rally", X = 1f, Z = 1f });

            AssertRoundTrip(doc, new RenamePlayerSpawnCommand("start", "start-2"));

            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenamePlayerSpawnCommand("start", "rally")));
            Assert.False(ed.History.CanUndo);
            Assert.Contains(doc.PlayerSpawns, s => s.Id == "start");
            Assert.Single(doc.PlayerSpawns, s => s.Id == "rally");
        }

        [Fact]
        public void AddPlayerSpawn_AbsorbsFollowingMove_OneUndoRemovesIt()
        {
            var doc = Sample();
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = "dropped", X = 1f, Z = 1f }));
            ed.Execute(new MovePlayerSpawnCommand("dropped", 5f, 6f));
            ed.Execute(new MovePlayerSpawnCommand("dropped", 9f, 2f));

            Assert.Equal(1, ed.History.UndoDepth);
            MapPlayerSpawn placed = doc.PlayerSpawns.First(s => s.Id == "dropped");
            Assert.Equal(9f, placed.X);
            Assert.Equal(2f, placed.Z);

            Assert.True(ed.Undo());
            Assert.DoesNotContain(doc.PlayerSpawns, s => s.Id == "dropped");
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void MovePlayerSpawn_MergesSameId()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 20f, Z = 5f });
            var ed = new EditorDocument(doc);
            ed.Execute(new MovePlayerSpawnCommand("start", 1f, 1f));
            ed.Execute(new MovePlayerSpawnCommand("start", 2f, 2f));
            Assert.Equal(2f, doc.PlayerSpawns.First(s => s.Id == "start").X);

            Assert.True(ed.Undo());
            Assert.Equal(20f, doc.PlayerSpawns.First(s => s.Id == "start").X);
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void SetPlayerSpawnYaw_MergesScrubs_FirstOldLastNew()
        {
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 20f, Z = 5f, Yaw = 0.2f });
            var ed = new EditorDocument(doc);
            ed.Execute(new SetPlayerSpawnYawCommand("start", 1f));
            ed.Execute(new SetPlayerSpawnYawCommand("start", 2f));
            Assert.Equal(2f, doc.PlayerSpawns.First(s => s.Id == "start").Yaw);
            Assert.Equal(1, ed.History.UndoDepth);

            Assert.True(ed.Undo());
            Assert.Equal(0.2f, doc.PlayerSpawns.First(s => s.Id == "start").Yaw);
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void AddPlayerSpawn_DeepCopiesTags_NoCallerAliasing()
        {
            // Round-5 aliasing guard: the command deep-copies the incoming spawn (fresh Tags list) at construction,
            // so mutating the caller's object or list afterward cannot leak into the document.
            var doc = Sample();
            var tags = new List<string> { "a" };
            var spawn = new MapPlayerSpawn { Id = "start", X = 1f, Z = 1f, Tags = tags };
            var cmd = new AddPlayerSpawnCommand(spawn);

            tags.Add("b");
            spawn.X = 999f;

            var ed = new EditorDocument(doc);
            ed.Execute(cmd);
            MapPlayerSpawn added = doc.PlayerSpawns.First(s => s.Id == "start");
            Assert.Equal(1f, added.X);
            Assert.Equal(new[] { "a" }, added.Tags);
        }

        [Fact]
        public void PlayerSpawnCommands_RoundTrip_MoveMerges_RenameGuarded()
        {
            // Round-trip: Add mutates the document, then undoes to a byte-identical serialized form.
            AssertRoundTrip(Sample(), new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = "start", X = 4f, Z = 9f, Yaw = 0.5f }));

            // Move coalescing: two same-id moves collapse to one undo step whose undo restores the origin.
            var doc = Sample();
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 20f, Z = 5f });
            var ed = new EditorDocument(doc);
            ed.Execute(new MovePlayerSpawnCommand("start", 1f, 1f));
            ed.Execute(new MovePlayerSpawnCommand("start", 2f, 2f));
            Assert.Equal(1, ed.History.UndoDepth);
            Assert.Equal(2f, doc.PlayerSpawns.First(s => s.Id == "start").X);

            // Rename guard: renaming onto an existing id throws in Apply and lands no undo step.
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "rally", X = 0f, Z = 0f });
            var ed2 = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed2.Execute(new RenamePlayerSpawnCommand("start", "rally")));
            Assert.False(ed2.History.CanUndo);
        }

        [Fact]
        public void EditFeature_RoundTrips()
        {
            var doc = Sample();
            MapFeature old = doc.Terrain.Features[0];
            var updated = new LakeFeatureDoc { CenterX = 1f, CenterZ = 2f, Radius = 9f, Depth = 4f };
            AssertRoundTrip(doc, new EditFeatureCommand(0, updated, old));
        }

        [Fact]
        public void AddFeature_RoundTrips() =>
            AssertRoundTrip(Sample(), new AddFeatureCommand(new LakeFeatureDoc { CenterX = 4f, CenterZ = 5f, Radius = 10f, Depth = 3f }));

        [Fact]
        public void RemoveFeature_RoundTrips() =>
            AssertRoundTrip(Sample(), new RemoveFeatureCommand(0));

        [Fact]
        public void AddFeature_RestoresOnUndo_AndAffectsWorld()
        {
            var ed = new EditorDocument(Sample());
            int before = ed.Doc.Terrain.Features.Count;

            ed.Execute(new AddFeatureCommand(new LakeFeatureDoc { CenterX = 1f, CenterZ = 1f, Radius = 5f, Depth = 1f }));
            Assert.Equal(before + 1, ed.Doc.Terrain.Features.Count);
            Assert.True(ed.WorldRebuildPending);   // features change terrain shape

            ed.Undo();
            Assert.Equal(before, ed.Doc.Terrain.Features.Count);
        }

        [Fact]
        public void RemoveFeature_RestoresAtOriginalIndex()
        {
            var doc = Sample();
            doc.Terrain.Features.Clear();
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 1f, Depth = 1f });
            var middle = new FlattenFeatureDoc { CenterX = 2f, CenterZ = 2f, Radius = 3f, TargetHeight = 1f };
            doc.Terrain.Features.Add(middle);
            doc.Terrain.Features.Add(new RidgeFeatureDoc { PointX = 5f, PointZ = 5f, Height = 2f, Width = 4f });

            var ed = new EditorDocument(doc);
            ed.Execute(new RemoveFeatureCommand(1));
            Assert.Equal(2, doc.Terrain.Features.Count);
            Assert.DoesNotContain(middle, doc.Terrain.Features);

            ed.Undo();
            Assert.Same(middle, doc.Terrain.Features[1]);   // back at the middle, not appended
        }

        [Fact]
        public void ReorderFeatureCommand_MovesAndReverts()
        {
            var doc = Sample();
            doc.Terrain.Features.Clear();
            var a = new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 1f, Depth = 1f };
            var b = new FlattenFeatureDoc { CenterX = 2f, CenterZ = 2f, Radius = 3f, TargetHeight = 1f };
            var c = new RidgeFeatureDoc { PointX = 5f, PointZ = 5f, Height = 2f, Width = 4f };
            doc.Terrain.Features.Add(a);
            doc.Terrain.Features.Add(b);
            doc.Terrain.Features.Add(c);

            var ed = new EditorDocument(doc);
            ed.Execute(new ReorderFeatureCommand(0, 2));   // move `a` to the end: it now folds last, so it wins overlaps
            Assert.Same(b, doc.Terrain.Features[0]);
            Assert.Same(c, doc.Terrain.Features[1]);
            Assert.Same(a, doc.Terrain.Features[2]);
            Assert.True(ed.WorldRebuildPending);           // reordering changes terrain shape

            Assert.True(ed.Undo());
            Assert.Same(a, doc.Terrain.Features[0]);        // revert restores the original fold order
            Assert.Same(b, doc.Terrain.Features[1]);
            Assert.Same(c, doc.Terrain.Features[2]);
        }

        [Fact]
        public void ReorderExclusionCommand_RoundTrips_AndDoesNotForceWorldRebuild()
        {
            var doc = Sample();
            doc.Exclusions.Clear();
            var e0 = new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } };
            var e1 = new MapExclusion { Shape = new DiscShapeDoc { CenterX = 9f, CenterZ = 9f, Radius = 3f } };
            doc.Exclusions.Add(e0);
            doc.Exclusions.Add(e1);
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new ReorderExclusionCommand(0, 1));   // move e0 to the end
            Assert.Same(e1, doc.Exclusions[0]);
            Assert.Same(e0, doc.Exclusions[1]);
            // Exclusions combine as a pure union, so their order never changes the scatter: no world rebuild.
            Assert.False(ed.WorldRebuildPending);

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));                 // byte-identical restore (deep equality)
            Assert.False(ed.WorldRebuildPending);            // undo of a non-world command leaves it false too
        }

        [Fact]
        public void ReorderExclusionCommand_RangeGuardsIndexes()
        {
            var doc = Sample();
            doc.Exclusions.Clear();
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 0f, CenterZ = 0f, Radius = 5f } });

            Assert.Throws<ArgumentOutOfRangeException>(() => new ReorderExclusionCommand(-1, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ReorderExclusionCommand(0, -2));
            // A valid-looking index that overruns the live list is caught precisely at apply time.
            Assert.Throws<ArgumentOutOfRangeException>(() => new EditorDocument(doc).Execute(new ReorderExclusionCommand(0, 5)));
        }

        [Fact]
        public void ReorderChangesTerrainWinner()
        {
            // A lake and a flatten cover the same ground. Terrain features fold in list order, so whichever folds
            // LAST dominates the overlap. Reordering the two flips who wins, sampled through the real terrain field
            // (MapRuntime.BuildField -> SampleHeight): this pins the user-facing "last wins" semantics end to end.
            var doc = Sample();
            doc.Terrain.Features.Clear();
            doc.Terrain.Features.Add(new LakeFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 20f, Depth = 8f });
            doc.Terrain.Features.Add(new FlattenFeatureDoc { CenterX = 0f, CenterZ = 0f, Radius = 20f, TargetHeight = 5f, Blend = 0.4f });

            var registry = MapDocRegistry.CreateDefault();
            float flattenWins = MapRuntime.BuildField(doc, registry).SampleHeight(0f, 0f);

            var ed = new EditorDocument(doc, registry);
            ed.Execute(new ReorderFeatureCommand(1, 0));   // flatten now folds first, lake folds last
            float lakeWins = MapRuntime.BuildField(doc, registry).SampleHeight(0f, 0f);

            // Flatten-last levels the centre to its target; lake-last carves the depth back out, so the overlap
            // height changes by the lake depth. The exact winner flipped.
            Assert.NotEqual(flattenWins, lakeWins);
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
        public void AddPlacement_AbsorbsFollowingMove_OneUndoRemovesIt()
        {
            var doc = Sample();
            string before = Save(doc);   // the pre-place document

            // Place-and-adjust at the command layer: the Add lands on the press edge, then same-id Moves adjust the
            // drop point. The Add absorbs each Move (folding the final X/Z/Y), so the whole gesture is ONE undo step.
            var ed = new EditorDocument(doc);
            ed.Execute(new AddPlacementCommand(new MapPlacement { Id = "dropped", Kind = "prop", X = 1f, Z = 1f }));
            ed.Execute(new MovePlacementCommand("dropped", 5f, 6f, null));
            ed.Execute(new MovePlacementCommand("dropped", 9f, 2f, null));

            Assert.Equal(1, ed.History.UndoDepth);       // absorbed: still one step
            MapPlacement placed = FindP(doc, "dropped");
            Assert.Equal(9f, placed.X);                  // final adjusted position folded into the Add
            Assert.Equal(2f, placed.Z);
            Assert.Null(placed.Y);

            // One undo removes the whole placement, restoring the pre-place document byte for byte.
            Assert.True(ed.Undo());
            Assert.DoesNotContain(doc.Placements, p => p.Id == "dropped");
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void AddSpawn_AbsorbsFollowingMove_OneUndoRemovesIt()
        {
            var doc = Sample();
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new AddSpawnCommand(new MapSpawn { Id = "dropped", ArchetypeId = "wolf", X = 1f, Z = 1f }));
            ed.Execute(new MoveSpawnCommand("dropped", 5f, 6f));
            ed.Execute(new MoveSpawnCommand("dropped", 9f, 2f));

            Assert.Equal(1, ed.History.UndoDepth);
            MapSpawn placed = doc.Spawns.First(s => s.Id == "dropped");
            Assert.Equal(9f, placed.X);
            Assert.Equal(2f, placed.Z);

            Assert.True(ed.Undo());
            Assert.DoesNotContain(doc.Spawns, s => s.Id == "dropped");
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void AddPlacement_DoesNotAbsorbDifferentIdMove()
        {
            var doc = Sample();
            doc.Placements.Add(P("other", 3f, 3f));

            // The Add only folds in a move of ITS OWN placement. A move of a different id stays a separate step.
            var ed = new EditorDocument(doc);
            ed.Execute(new AddPlacementCommand(new MapPlacement { Id = "dropped", Kind = "prop", X = 1f, Z = 1f }));
            ed.Execute(new MovePlacementCommand("other", 8f, 8f, null));
            Assert.Equal(2, ed.History.UndoDepth);       // not absorbed: a foreign-id move is its own step
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
        public void RenameFeature_RoundTrip_MergesScrubs()
        {
            var doc = Sample();
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Applies and reverts: round trip is deep-equal. Features are index-addressed (no independent id),
            // unlike RenamePlacementCommand's id-keyed variant.
            ed.Execute(new RenameFeatureCommand(0, "north-lake", ""));
            Assert.Equal("north-lake", doc.Terrain.Features[0].Name);
            Assert.False(ed.WorldRebuildPending);   // renaming does not change terrain shape
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));

            // Successive renames of the same index coalesce (a text field committed on every keystroke stays
            // one undo step, the EditFeatureCommand scrub-coalescing idiom).
            ed.Execute(new RenameFeatureCommand(0, "n", ""));
            ed.Execute(new RenameFeatureCommand(0, "no", "n"));
            ed.Execute(new RenameFeatureCommand(0, "north", "no"));
            Assert.Equal("north", doc.Terrain.Features[0].Name);
            Assert.Equal(1, ed.History.UndoDepth);

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void RenameFeatureCommand_GuardsDuplicates()
        {
            var doc = Sample();
            doc.Terrain.Features[0].Name = "north-lake";
            doc.Terrain.Features[1].Name = "south-flat";
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Guards duplicates: renaming feature 1 onto feature 0's name throws before mutating anything, and
            // leaves no undo step (the RenameRegionCommand guard idiom, ported to the index-addressed
            // feature/exclusion rename commands).
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameFeatureCommand(1, "north-lake", "south-flat")));
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));   // document untouched, byte for byte

            // Renaming a feature to its OWN current name stays legal: the duplicate scan excludes the renaming
            // feature's own index.
            ed.Execute(new RenameFeatureCommand(0, "north-lake", "north-lake"));
            Assert.Equal("north-lake", doc.Terrain.Features[0].Name);

            // Clearing a name to empty stays legal even with another already-unnamed feature present: a null or
            // empty name never collides, it carries no key to clash on.
            doc.Terrain.Features[1].Name = null;
            ed.Execute(new RenameFeatureCommand(0, "", "north-lake"));
            Assert.Null(doc.Terrain.Features[0].Name);
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
        public void RenameExclusion_RoundTrip()
        {
            var doc = Sample();
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Exclusions are index-addressed (no independent id), the same idiom as RenameFeatureCommand.
            ed.Execute(new RenameExclusionCommand(0, "market-clearing", ""));
            Assert.Equal("market-clearing", doc.Exclusions[0].Name);
            Assert.False(ed.WorldRebuildPending);   // renaming does not change scatter inputs

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void RenameExclusionCommand_GuardsDuplicates()
        {
            var doc = Sample();
            doc.Exclusions[0].Name = "market-clearing";
            doc.Exclusions.Add(new MapExclusion { Name = "second-yard", Shape = new DiscShapeDoc { CenterX = 1f, CenterZ = 1f, Radius = 5f } });
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Guards duplicates: renaming exclusion 1 onto exclusion 0's name throws before mutating anything,
            // and leaves no undo step (the RenameRegionCommand guard idiom, ported to the index-addressed
            // feature/exclusion rename commands).
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameExclusionCommand(1, "market-clearing", "second-yard")));
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));   // document untouched, byte for byte

            // Renaming an exclusion to its OWN current name stays legal: the duplicate scan excludes the
            // renaming exclusion's own index.
            ed.Execute(new RenameExclusionCommand(0, "market-clearing", "market-clearing"));
            Assert.Equal("market-clearing", doc.Exclusions[0].Name);

            // Clearing a name to empty stays legal even with another already-unnamed exclusion present: a null
            // or empty name never collides, it carries no key to clash on.
            doc.Exclusions[1].Name = null;
            ed.Execute(new RenameExclusionCommand(0, "", "market-clearing"));
            Assert.Null(doc.Exclusions[0].Name);
        }

        [Fact]
        public void EditExclusionLayers_RoundTrip_AffectsWorld_UnknownLayerRejected()
        {
            var doc = Sample();
            List<string>? original = doc.Exclusions[0].Layers;   // null in SampleDoc: applies to every layer
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(new EditExclusionLayersCommand(0, new List<string> { "trees" }, original));
            Assert.Equal(new List<string> { "trees" }, doc.Exclusions[0].Layers);
            Assert.True(ed.WorldRebuildPending);   // targeting changes scatter output

            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));

            // A second edit of the same index coalesces (checkbox-drag coalescing).
            ed.Execute(new EditExclusionLayersCommand(0, new List<string> { "trees" }, original));
            ed.Execute(new EditExclusionLayersCommand(0, null, new List<string> { "trees" }));
            Assert.Null(doc.Exclusions[0].Layers);
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);

            // An unknown layer name is not rejected by the command itself: the standard document validator on
            // save catches it, the same invariant every other layer-filter field already relies on.
            ed.Execute(new EditExclusionLayersCommand(0, new List<string> { "nope" }, original));
            Assert.Throws<MapDocumentException>(() => Save(doc));
        }

        [Fact]
        public void EditExclusionLayersCommand_ConstructorCopiesLists_NoAliasing()
        {
            var doc = Sample();
            var newLayers = new List<string> { "trees" };
            var oldLayers = new List<string> { "rocks" };
            var cmd = new EditExclusionLayersCommand(0, newLayers, oldLayers);

            // Mutate the caller's own lists after construction: if the command captured them by reference (not
            // a copy), both Apply and Revert would observe this mutation instead of the value at construction.
            newLayers.Add("mutated-new");
            oldLayers.Add("mutated-old");

            var ed = new EditorDocument(doc);
            ed.Execute(cmd);
            Assert.Equal(new List<string> { "trees" }, doc.Exclusions[0].Layers);   // construction-time value

            Assert.True(ed.Undo());
            Assert.Equal(new List<string> { "rocks" }, doc.Exclusions[0].Layers);   // construction-time old value
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

        // ---- terrain scalars (widened EditTerrainCommand) ----------------------------------------------

        [Fact]
        public void EditTerrain_AllScalars_ApplyRevertMerge()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            MapTerrain t = doc.Terrain;
            float w0 = t.WaterLevel;
            int s0 = t.Seed;
            float bb0 = t.BiomeBlend, gf0 = t.GentleFrequency, ga0 = t.GentleAmplitude, df0 = t.DetailFrequency;
            int oct0 = t.DetailOctaves;

            // Each scalar applies independently and reverts to its prior value, and a command that sets ONE
            // scalar leaves every other scalar untouched (only-set-fields apply).
            ed.Execute(new EditTerrainCommand(newBiomeBlend: 42f, oldBiomeBlend: bb0));
            Assert.Equal(42f, t.BiomeBlend);
            Assert.Equal(w0, t.WaterLevel);   // only-set: water not touched
            Assert.Equal(s0, t.Seed);         // only-set: seed not touched
            Assert.Equal(gf0, t.GentleFrequency);
            Assert.Equal(ga0, t.GentleAmplitude);
            Assert.Equal(df0, t.DetailFrequency);
            Assert.Equal(oct0, t.DetailOctaves);
            Assert.True(ed.WorldRebuildPending);   // AffectsWorld
            ed.SealGesture();

            ed.Execute(new EditTerrainCommand(newSeed: 555, oldSeed: s0));
            Assert.Equal(555, t.Seed);
            Assert.Equal(42f, t.BiomeBlend);   // the prior set field stands
            ed.SealGesture();

            ed.Execute(new EditTerrainCommand(newGentleFrequency: 0.5f, oldGentleFrequency: gf0));
            Assert.Equal(0.5f, t.GentleFrequency);
            ed.SealGesture();
            ed.Execute(new EditTerrainCommand(newGentleAmplitude: 9f, oldGentleAmplitude: ga0));
            Assert.Equal(9f, t.GentleAmplitude);
            ed.SealGesture();
            ed.Execute(new EditTerrainCommand(newDetailFrequency: 0.09f, oldDetailFrequency: df0));
            Assert.Equal(0.09f, t.DetailFrequency);
            ed.SealGesture();
            ed.Execute(new EditTerrainCommand(newDetailOctaves: 7, oldDetailOctaves: oct0));
            Assert.Equal(7, t.DetailOctaves);
        }

        [Fact]
        public void EditTerrain_ScrubMerge_SameField_FirstOldLastNew()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            float w0 = doc.Terrain.WaterLevel;

            // Two water edits of the same gesture coalesce: the level moves to the last value, but a single undo
            // reverts the whole scrub to the first old (no barrier between them).
            ed.Execute(new EditTerrainCommand(newWaterLevel: 1f, oldWaterLevel: w0));
            ed.Execute(new EditTerrainCommand(newWaterLevel: 2f, oldWaterLevel: 1f));
            Assert.Equal(2f, doc.Terrain.WaterLevel);

            Assert.True(ed.Undo());
            Assert.Equal(w0, doc.Terrain.WaterLevel);   // first-old wins the revert
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void EditTerrain_Merge_UnionOfDifferentFields_EachOldFromFirstSetter()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            float w0 = doc.Terrain.WaterLevel;
            int s0 = doc.Terrain.Seed;

            // A water-only edit and a seed-only edit of the same gesture coalesce into ONE step carrying BOTH
            // fields. One undo reverts both, each to the old captured by the FIRST command that set that field.
            ed.Execute(new EditTerrainCommand(newWaterLevel: 3f, oldWaterLevel: w0));
            ed.Execute(new EditTerrainCommand(newSeed: 88, oldSeed: s0));
            Assert.Equal(3f, doc.Terrain.WaterLevel);
            Assert.Equal(88, doc.Terrain.Seed);

            Assert.True(ed.Undo());
            Assert.Equal(w0, doc.Terrain.WaterLevel);   // union revert restores water
            Assert.Equal(s0, doc.Terrain.Seed);         // and seed, each from its own first-setter old
            Assert.False(ed.History.CanUndo);

            // Re-scrubbing water after the seed already merged keeps water's FIRST old (w0), not the seed edit's.
            var ed2 = new EditorDocument(Sample());
            float w1 = ed2.Doc.Terrain.WaterLevel;
            ed2.Execute(new EditTerrainCommand(newWaterLevel: 5f, oldWaterLevel: w1));
            ed2.Execute(new EditTerrainCommand(newSeed: 12, oldSeed: ed2.Doc.Terrain.Seed));
            ed2.Execute(new EditTerrainCommand(newWaterLevel: 6f, oldWaterLevel: 5f));
            Assert.True(ed2.Undo());
            Assert.Equal(w1, ed2.Doc.Terrain.WaterLevel);   // still the first-ever water old, not 5
        }

        // ---- biome bands -------------------------------------------------------------------------------

        [Fact]
        public void BiomeBand_AddEditRemove_RoundTrip_AffectsWorld()
        {
            var band = new MapBiomeBand
            {
                Start = 0f, End = 40f, Biome = KhaozEngine.Terrain.BiomeId.Forest, BaseHeight = 3f, HillAmplitude = 5f,
            };
            AssertRoundTrip(Sample(), new AddBiomeBandCommand(band));

            // Add is world-affecting (band shape feeds the terrain field).
            var ed = new EditorDocument(Sample());
            ed.Execute(new AddBiomeBandCommand(new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Snow }));
            Assert.True(ed.WorldRebuildPending);

            // Whole-value edit of an existing band round-trips, is world-affecting, and same-index edits merge.
            var doc = Sample();
            doc.Terrain.Biomes.Clear();
            var original = new MapBiomeBand { Start = null, End = 10f, Biome = KhaozEngine.Terrain.BiomeId.Meadow };
            doc.Terrain.Biomes.Add(original);
            var ed2 = new EditorDocument(doc);
            var v1 = new MapBiomeBand { Start = 1f, End = 10f, Biome = KhaozEngine.Terrain.BiomeId.Marsh };
            var v2 = new MapBiomeBand { Start = 2f, End = 10f, Biome = KhaozEngine.Terrain.BiomeId.Marsh };
            ed2.Execute(new EditBiomeBandCommand(0, v1, original));
            Assert.True(ed2.WorldRebuildPending);
            ed2.Execute(new EditBiomeBandCommand(0, v2, v1));   // same index, same gesture: coalesces
            Assert.Same(v2, doc.Terrain.Biomes[0]);
            Assert.True(ed2.Undo());                            // one undo reverts the whole scrub
            Assert.Same(original, doc.Terrain.Biomes[0]);
            Assert.False(ed2.History.CanUndo);

            // Remove round-trips (restores at the same index).
            var doc3 = Sample();
            doc3.Terrain.Biomes.Clear();
            var a = new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Meadow };
            var b = new MapBiomeBand { Biome = KhaozEngine.Terrain.BiomeId.Desert };
            doc3.Terrain.Biomes.Add(a);
            doc3.Terrain.Biomes.Add(b);
            var ed3 = new EditorDocument(doc3);
            ed3.Execute(new RemoveBiomeBandCommand(0));
            Assert.Single(doc3.Terrain.Biomes);
            Assert.Same(b, doc3.Terrain.Biomes[0]);
            Assert.True(ed3.Undo());
            Assert.Same(a, doc3.Terrain.Biomes[0]);   // back at index 0, not appended
        }

        [Fact]
        public void RemoveBiomeBand_OutOfRange_Throws()
        {
            var doc = Sample();
            int count = doc.Terrain.Biomes.Count;
            var ed = new EditorDocument(doc);
            Assert.Throws<ArgumentException>(() => ed.Execute(new RemoveBiomeBandCommand(count)));
        }

        // ---- scatter + companion layers ----------------------------------------------------------------

        static MapScatterLayer Layer(string name) => new MapScatterLayer
        {
            Name = name, Seed = 7, CellSize = 4f, ScaleMin = 0.5f, ScaleMax = 1.5f,
            Rules = { new MapBiomeScatterRule { Biome = KhaozEngine.Terrain.BiomeId.Meadow, Density = 0.4f,
                Kinds = { new MapPropKind { Id = "oak", Weight = 2f } } } },
        };

        [Fact]
        public void ScatterLayer_AddEditRemove_RoundTrip_AffectsWorld()
        {
            // Add a fresh, unreferenced layer round-trips (deep-equal after undo).
            AssertRoundTrip(Sample(), new AddScatterLayerCommand(Layer("grass")));

            var edAdd = new EditorDocument(Sample());
            edAdd.Execute(new AddScatterLayerCommand(Layer("grass")));
            Assert.True(edAdd.WorldRebuildPending);   // scatter layers feed the streamed prop field

            // Whole-value edit of an existing layer (a new value carrying the same name) round-trips, affects the
            // world, and same-name edits coalesce into one undo step.
            var doc = Sample();
            MapScatterLayer live = doc.ScatterLayers[0];   // "trees"
            var v1 = new MapScatterLayer { Name = "trees", CellSize = 6f };
            var v2 = new MapScatterLayer { Name = "trees", CellSize = 7f };
            var ed2 = new EditorDocument(doc);
            ed2.Execute(new EditScatterLayerCommand("trees", v1, live));
            Assert.True(ed2.WorldRebuildPending);
            Assert.Same(v1, doc.ScatterLayers[0]);
            ed2.Execute(new EditScatterLayerCommand("trees", v2, v1));   // same name, same gesture: coalesces
            Assert.Same(v2, doc.ScatterLayers[0]);
            Assert.True(ed2.Undo());
            Assert.Same(live, doc.ScatterLayers[0]);
            Assert.False(ed2.History.CanUndo);

            // Remove of an UNREFERENCED layer round-trips (restores at the same index, not appended).
            var doc3 = Sample();
            doc3.ScatterLayers.Add(Layer("grass"));   // index 1, referenced by nothing
            var ed3 = new EditorDocument(doc3);
            ed3.Execute(new RemoveScatterLayerCommand("grass"));
            Assert.True(ed3.WorldRebuildPending);
            Assert.Single(doc3.ScatterLayers);
            Assert.True(ed3.Undo());
            Assert.Equal(2, doc3.ScatterLayers.Count);
            Assert.Equal("grass", doc3.ScatterLayers[1].Name);
        }

        [Fact]
        public void EditScatterLayerCommand_WholeValueSwap_OldValueIntactOnUndo()
        {
            // The command swaps whole values, so a nested-list change on the applied value leaves the captured old
            // value intact for undo (the deep-clone discipline that builds the new value lives in the editor scene).
            var doc = Sample();
            MapScatterLayer live = doc.ScatterLayers[0];
            int liveRuleCount = live.Rules.Count;
            var clone = new MapScatterLayer { Name = "trees", Seed = live.Seed, CellSize = live.CellSize };
            foreach (MapBiomeScatterRule r in live.Rules)
                clone.Rules.Add(new MapBiomeScatterRule { Biome = r.Biome, Density = r.Density });
            clone.Rules.Add(new MapBiomeScatterRule { Biome = KhaozEngine.Terrain.BiomeId.Forest, Density = 0.9f });

            var ed = new EditorDocument(doc);
            ed.Execute(new EditScatterLayerCommand("trees", clone, live));
            Assert.Equal(liveRuleCount + 1, doc.ScatterLayers[0].Rules.Count);
            Assert.True(ed.Undo());
            Assert.Equal(liveRuleCount, doc.ScatterLayers[0].Rules.Count);   // old value untouched
        }

        [Fact]
        public void ScatterLayerRemove_ReferencedByCompanion_RejectedAndReverted()
        {
            // SampleDoc's "trees" is hosted by companion "understory" AND named by scatterOverride[0]'s filter.
            var doc = Sample();
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Removing a referenced scatter layer throws BEFORE it mutates, listing the referencing elements, and
            // lands no undo step: the document is untouched (rejected-and-reverted via the reject-before-mutate
            // guard, the standard validate-revert invariant rendered as a precise up-front reject).
            var ex = Assert.Throws<InvalidOperationException>(() => ed.Execute(new RemoveScatterLayerCommand("trees")));
            Assert.Contains("trees", ex.Message);
            Assert.Contains("understory", ex.Message);   // the companion host reference is surfaced
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));              // untouched, byte for byte

            // Clearing the references makes the same removal succeed and round-trip.
            doc.CompanionLayers.Clear();
            doc.ScatterOverrides.Clear();
            ed.Execute(new RemoveScatterLayerCommand("trees"));
            Assert.Empty(doc.ScatterLayers);
            Assert.True(ed.Undo());
            Assert.Single(doc.ScatterLayers);
        }

        [Fact]
        public void ScatterLayerRename_CascadesToReferences()
        {
            // Locked decision 10: a scatter-layer rename CASCADES through every reference (companion host, explicit
            // exclusion / override filters) so the document stays valid and no reference is silently orphaned. (The
            // brief's earlier "refs do not auto-follow" framing was superseded by the cascade decision, which is
            // lossless and friendly: the reference follows the layer rather than the rename being blocked.)
            var doc = Sample();
            doc.Exclusions[0].Layers = new List<string> { "trees" };   // an explicit exclusion filter to cascade too
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            ed.Execute(new RenameScatterLayerCommand("trees", "forest"));
            Assert.Equal("forest", doc.ScatterLayers[0].Name);
            Assert.Equal("forest", doc.CompanionLayers[0].HostLayer);           // companion host followed
            Assert.Equal(new[] { "forest" }, doc.Exclusions[0].Layers);         // exclusion filter followed
            Assert.Equal(new[] { "forest" }, doc.ScatterOverrides[0].Layers);   // override filter followed
            Assert.False(ed.WorldRebuildPending);                              // refs still resolve to the same layer
            Assert.Empty(MapDocumentValidator.Validate(doc, MapDocRegistry.CreateDefault()));   // stays valid

            // Revert reverses the whole cascade, byte for byte.
            Assert.True(ed.Undo());
            Assert.Equal(before, Save(doc));

            // The target name must be unique: renaming onto an existing layer's name throws before mutating.
            doc.ScatterLayers.Add(new MapScatterLayer { Name = "meadow-scatter" });
            string before2 = Save(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameScatterLayerCommand("trees", "meadow-scatter")));
            Assert.Equal(before2, Save(doc));
        }

        [Fact]
        public void ScatterLayerRename_ChainedMerges()
        {
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new RenameScatterLayerCommand("trees", "a"));
            ed.Execute(new RenameScatterLayerCommand("a", "b"));   // chained: coalesces into the first
            Assert.Equal("b", doc.ScatterLayers[0].Name);
            Assert.Equal("b", doc.CompanionLayers[0].HostLayer);
            Assert.True(ed.Undo());
            Assert.Equal("trees", doc.ScatterLayers[0].Name);   // one undo reverses the whole chain
            Assert.Equal("trees", doc.CompanionLayers[0].HostLayer);
            Assert.False(ed.History.CanUndo);
        }

        [Fact]
        public void CompanionLayer_AddEditRemove_HostLayerValidated()
        {
            // Add a companion round-trips and affects the world.
            var comp = new MapCompanionLayer { Name = "canopy", HostLayer = "trees", HostKinds = { "pine_a" },
                Kinds = { new MapPropKind { Id = "vine", Weight = 1f } } };
            AssertRoundTrip(Sample(), new AddCompanionLayerCommand(comp));

            var edAdd = new EditorDocument(Sample());
            edAdd.Execute(new AddCompanionLayerCommand(new MapCompanionLayer { Name = "canopy", HostLayer = "trees" }));
            Assert.True(edAdd.WorldRebuildPending);

            // Whole-value edit round-trips + same-name merge.
            var doc = Sample();
            MapCompanionLayer live = doc.CompanionLayers[0];   // "understory"
            var w1 = new MapCompanionLayer { Name = "understory", HostLayer = "trees", CountMin = 2, CountMax = 6 };
            var w2 = new MapCompanionLayer { Name = "understory", HostLayer = "trees", CountMin = 2, CountMax = 8 };
            var ed2 = new EditorDocument(doc);
            ed2.Execute(new EditCompanionLayerCommand("understory", w1, live));
            ed2.Execute(new EditCompanionLayerCommand("understory", w2, w1));
            Assert.Same(w2, doc.CompanionLayers[0]);
            Assert.True(ed2.Undo());
            Assert.Same(live, doc.CompanionLayers[0]);

            // A bogus HostLayer is caught by the standard validator on save: the editor's chooser only offers real
            // scatter layers, but a scripted edit relies on this net (the exclusion-layer-filter invariant).
            var doc3 = Sample();
            var ed3 = new EditorDocument(doc3);
            var bad = new MapCompanionLayer { Name = "understory", HostLayer = "nope" };
            ed3.Execute(new EditCompanionLayerCommand("understory", bad, doc3.CompanionLayers[0]));
            Assert.Throws<MapDocumentException>(() => Save(doc3));

            // Remove round-trips (restores at the same index).
            var doc4 = Sample();
            var ed4 = new EditorDocument(doc4);
            ed4.Execute(new RemoveCompanionLayerCommand("understory"));
            Assert.True(ed4.WorldRebuildPending);
            Assert.Empty(doc4.CompanionLayers);
            Assert.True(ed4.Undo());
            Assert.Single(doc4.CompanionLayers);
        }

        [Fact]
        public void RenameCompanionLayerCommand_GuardsDuplicates_AndMerges()
        {
            var doc = Sample();
            doc.CompanionLayers.Add(new MapCompanionLayer { Name = "canopy", HostLayer = "trees" });
            string before = Save(doc);
            var ed = new EditorDocument(doc);

            // Renaming "understory" onto "canopy" throws before mutating, leaving no undo step.
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new RenameCompanionLayerCommand("understory", "canopy")));
            Assert.False(ed.History.CanUndo);
            Assert.Equal(before, Save(doc));

            // A chained rename coalesces (no cascade: nothing references a companion by name).
            ed.Execute(new RenameCompanionLayerCommand("understory", "u1"));
            ed.Execute(new RenameCompanionLayerCommand("u1", "u2"));
            Assert.Equal("u2", doc.CompanionLayers[0].Name);
            Assert.False(ed.WorldRebuildPending);   // a companion rename changes nothing streamed
            Assert.True(ed.Undo());
            Assert.Equal("understory", doc.CompanionLayers[0].Name);
        }
    }
}
