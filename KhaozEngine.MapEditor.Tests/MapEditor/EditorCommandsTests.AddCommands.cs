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

        [Fact]
        public void AddPlacement_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();
            doc.Placements.Add(P("shed", 4f, 5f));   // a second element, so removing the wrong one is observable
            AssertUndoRemovesAppendedSlot(doc, doc.Placements, doc.Placements[0],
                new AddPlacementCommand(doc.Placements[0]));
        }

        [Fact]
        public void AddSpawn_UndoRemovesAppendedSlot_NotAnEarlierAlias()
        {
            var doc = Sample();
            doc.Spawns.Add(new MapSpawn { Id = "bear-1", ArchetypeId = "bear", X = 4f, Z = 9f });
            AssertUndoRemovesAppendedSlot(doc, doc.Spawns, doc.Spawns[0], new AddSpawnCommand(doc.Spawns[0]));
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
    }
}
