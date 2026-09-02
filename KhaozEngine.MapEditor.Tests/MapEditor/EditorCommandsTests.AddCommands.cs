using System;
using System.Collections.Generic;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>The Add*Command family's shared contracts, split out of the main
    /// <see cref="EditorCommandsTests"/> partial: the append-slot revert idiom (#76) and the add-time
    /// duplicate-name guard (#75).</summary>
    public partial class EditorCommandsTests
    {
        /// <summary>Appends <paramref name="alias"/>, an element the document ALREADY holds at index 0, through
        /// <paramref name="add"/>, then undoes and asserts the APPENDED slot went away rather than the earlier
        /// occurrence of that same instance. The MapDoc element types carry no Equals override, so a
        /// reference-based Revert (List.Remove) strips the FIRST reference-equal match and leaves the list
        /// reordered instead of restored. That is the observable half of the #24 identity trap the whole family
        /// now avoids by capturing the slot at Apply and reverting with RemoveAt.</summary>
        static void AssertUndoRemovesAppendedSlot<T>(MapDocument doc, List<T> list, T alias, IEditorCommand add)
            where T : class
        {
            Assert.Same(alias, list[0]);            // the alias really is the earlier occurrence
            Assert.True(list.Count >= 2);           // and a later element exists, so first-versus-last is visible
            int baseline = list.Count;
            string before = Save(doc);

            var ed = new EditorDocument(doc);
            ed.Execute(add);
            Assert.Equal(baseline + 1, list.Count);
            Assert.Same(alias, list[baseline]);     // appended at the end, so the captured slot is the last one

            Assert.True(ed.Undo());

            Assert.Equal(baseline, list.Count);
            Assert.Same(alias, list[0]);            // the earlier occurrence survived: the appended slot went
            Assert.Equal(before, Save(doc));
            Assert.False(ed.IsDirty);
        }

        /// <summary>The same assertion for the id-guarded Add commands, which can no longer be handed an element
        /// the document already holds: the guard (#766) rejects the colliding id before the append, so the alias
        /// state has to be built AFTER the apply instead of before it. Executes <paramref name="add"/>, then makes
        /// index 0 reference-equal to the appended element, then undoes and asserts the APPENDED slot went rather
        /// than the earlier occurrence. Reverting by reference would strip index 0 and leave the list reordered.
        /// Nothing inserts before the captured slot, which is what the slot idiom relies on.</summary>
        static void AssertUndoRemovesAppendedSlot_AliasedAfterApply<T>(MapDocument doc, List<T> list, T added, IEditorCommand add)
            where T : class
        {
            Assert.True(list.Count >= 1);           // an earlier element exists, so first-versus-last is visible
            int baseline = list.Count;

            var ed = new EditorDocument(doc);
            ed.Execute(add);
            Assert.Equal(baseline + 1, list.Count);
            Assert.Same(added, list[baseline]);     // appended at the end, so the captured slot is the last one

            list[0] = added;                        // the alias, planted where a reference-based revert would bite

            Assert.True(ed.Undo());

            Assert.Equal(baseline, list.Count);
            Assert.Same(added, list[0]);            // the earlier occurrence survived: the appended slot went
        }

        [Fact]
        public void AddPlacement_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();                     // carries one placement, "inn"
            MapPlacement shed = P("shed", 4f, 5f);   // ONE instance: the command appends the very object it is given
            AssertUndoRemovesAppendedSlot_AliasedAfterApply(doc, doc.Placements, shed, new AddPlacementCommand(shed));
        }

        [Fact]
        public void AddSpawn_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();                     // carries one spawn, "wolf-1"
            var bear = new MapSpawn { Id = "bear-1", ArchetypeId = "bear", X = 4f, Z = 9f };
            AssertUndoRemovesAppendedSlot_AliasedAfterApply(doc, doc.Spawns, bear, new AddSpawnCommand(bear));
        }

        [Fact]
        public void AddExclusion_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();
            doc.Exclusions.Add(new MapExclusion { Shape = new DiscShapeDoc { CenterX = 10f, CenterZ = 10f, Radius = 4f } });
            AssertUndoRemovesAppendedSlot(doc, doc.Exclusions, doc.Exclusions[0],
                new AddExclusionCommand(doc.Exclusions[0]));
        }

        [Fact]
        public void AddBiomeBand_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();
            doc.Terrain.Biomes.Add(new MapBiomeBand { Biome = BiomeId.Forest, BaseHeight = 3f, HillAmplitude = 2f });
            AssertUndoRemovesAppendedSlot(doc, doc.Terrain.Biomes, doc.Terrain.Biomes[0],
                new AddBiomeBandCommand(doc.Terrain.Biomes[0]));
        }

        [Fact]
        public void AddFeature_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            // Sample() already carries two features (a lake and a flatten), so nothing needs seeding here.
            var doc = Sample();
            AssertUndoRemovesAppendedSlot(doc, doc.Terrain.Features, doc.Terrain.Features[0],
                new AddFeatureCommand(doc.Terrain.Features[0]));
        }

        [Fact]
        public void AddRegionCommand_GuardsDuplicateName()
        {
            // Regression for #75. Region names are unique-required, but AddRegionCommand used to append without
            // consulting GuardNoRegion (whose only caller was the region rename), so a colliding add landed an
            // undo step and left the collision for MapDocumentValidator to report at save time, long after the
            // gesture that caused it. Apply now rejects before it mutates, the shape its guarded Add siblings
            // use: History.Execute applies BEFORE it pushes, so a throwing Apply lands no undo step.
            var doc = Sample();   // carries exactly one region, "town"
            string before = Save(doc);
            int baseline = doc.Regions.Count;

            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(new AddRegionCommand(
                new MapRegion { Name = "town", Shape = new DiscShapeDoc { CenterX = 9f, CenterZ = 9f, Radius = 2f } })));

            Assert.Equal(baseline, doc.Regions.Count);
            Assert.Single(doc.Regions, r => r.Name == "town");   // no second "town" landed
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
            Assert.False(ed.IsDirty);
        }

        /// <summary>Executes <paramref name="add"/>, whose id already exists in <paramref name="list"/>, and
        /// asserts it was refused with nothing mutated and no undo step pushed. The #75 shape, applied to the
        /// three id-keyed Add commands that still skipped their guard (#766). History.Execute applies BEFORE it
        /// pushes, so a throwing Apply lands no undo step.</summary>
        static void AssertDuplicateAddIsRefused<T>(MapDocument doc, List<T> list, IEditorCommand add)
        {
            string before = Save(doc);
            int baseline = list.Count;

            var ed = new EditorDocument(doc);
            Assert.Throws<InvalidOperationException>(() => ed.Execute(add));

            Assert.Equal(baseline, list.Count);
            Assert.Equal(before, Save(doc));
            Assert.False(ed.History.CanUndo);
            Assert.False(ed.IsDirty);
        }

        [Fact]
        public void AddPlacementCommand_GuardsDuplicateId()
        {
            var doc = Sample();   // carries exactly one placement, "inn"
            AssertDuplicateAddIsRefused(doc, doc.Placements,
                new AddPlacementCommand(new MapPlacement { Id = "inn", Kind = "rock", X = 9f, Z = 9f }));
            Assert.Single(doc.Placements, p => p.Id == "inn");
            Assert.Equal("building_inn", doc.Placements[0].Kind);   // the original, not the rejected one
        }

        [Fact]
        public void AddSpawnCommand_GuardsDuplicateId()
        {
            var doc = Sample();   // carries exactly one spawn, "wolf-1"
            AssertDuplicateAddIsRefused(doc, doc.Spawns,
                new AddSpawnCommand(new MapSpawn { Id = "wolf-1", ArchetypeId = "bear", X = 9f, Z = 9f }));
            Assert.Single(doc.Spawns, s => s.Id == "wolf-1");
            Assert.Equal("wolf", doc.Spawns[0].ArchetypeId);
        }

        [Fact]
        public void AddPlayerSpawnCommand_GuardsDuplicateId()
        {
            var doc = Sample();   // carries no player spawn, so seed the one being collided with
            doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start", X = 1f, Z = 2f });
            AssertDuplicateAddIsRefused(doc, doc.PlayerSpawns,
                new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = "start", X = 9f, Z = 9f }));
            Assert.Single(doc.PlayerSpawns, s => s.Id == "start");
            Assert.Equal(1f, doc.PlayerSpawns[0].X);
        }

        [Fact]
        public void GuardedAdds_StillRedoAfterAnUndo()
        {
            // The guard reads the document at Apply time, and a redo re-applies. Undo took the element out, so
            // the redo must not see its own id as a collision.
            var doc = Sample();
            var ed = new EditorDocument(doc);
            ed.Execute(new AddPlacementCommand(P("obelisk", 5f, 5f)));
            ed.Execute(new AddSpawnCommand(new MapSpawn { Id = "bear-1", ArchetypeId = "bear", X = 4f, Z = 9f }));
            ed.Execute(new AddPlayerSpawnCommand(new MapPlayerSpawn { Id = "start", X = 4f, Z = 9f }));
            string after = Save(doc);

            Assert.True(ed.Undo());
            Assert.True(ed.Undo());
            Assert.True(ed.Undo());
            Assert.True(ed.Redo());
            Assert.True(ed.Redo());
            Assert.True(ed.Redo());

            Assert.Equal(after, Save(doc));
        }
    }
}
