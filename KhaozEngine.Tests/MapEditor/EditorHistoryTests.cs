using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the engine's first undo/redo command stack: apply/push, undo/redo ordering,
    /// redo invalidation on a new edit, and gesture coalescing via <see cref="IEditorCommand.TryMerge"/>.</summary>
    public class EditorHistoryTests
    {
        static MapDocument DocWith(params MapPlacement[] placements)
        {
            var d = new MapDocument
            {
                Id = "zone",
                DisplayName = "Zone",
                Bounds = new MapBounds { MinX = -50f, MinZ = -50f, MaxX = 50f, MaxZ = 50f },
            };
            foreach (MapPlacement p in placements) d.Placements.Add(p);
            return d;
        }

        static MapPlacement P(string id, float x = 0f, float z = 0f) =>
            new MapPlacement { Id = id, Kind = "prop", X = x, Z = z };

        static MapPlacement Find(MapDocument d, string id) => d.Placements.First(p => p.Id == id);

        [Fact]
        public void Execute_AppliesCommandAndEnablesUndo()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            Assert.False(h.CanUndo);

            h.Execute(doc, new AddPlacementCommand(P("a", 5f, 6f)));

            Assert.Single(doc.Placements);
            Assert.Equal(5f, Find(doc, "a").X);
            Assert.True(h.CanUndo);
            Assert.False(h.CanRedo);
        }

        [Fact]
        public void Undo_RevertsTopCommandAndEnablesRedo()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            h.Execute(doc, new AddPlacementCommand(P("a")));

            Assert.True(h.Undo(doc));

            Assert.Empty(doc.Placements);
            Assert.False(h.CanUndo);
            Assert.True(h.CanRedo);
        }

        [Fact]
        public void Redo_ReappliesUndoneCommand()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            h.Execute(doc, new AddPlacementCommand(P("a")));
            h.Undo(doc);

            Assert.True(h.Redo(doc));

            Assert.Single(doc.Placements);
            Assert.True(h.CanUndo);
            Assert.False(h.CanRedo);
        }

        [Fact]
        public void Execute_ClearsRedoStack()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            h.Execute(doc, new AddPlacementCommand(P("a")));
            h.Execute(doc, new AddPlacementCommand(P("b")));
            h.Undo(doc);
            Assert.True(h.CanRedo);

            h.Execute(doc, new AddPlacementCommand(P("c")));

            Assert.False(h.CanRedo);
        }

        [Fact]
        public void UndoRedo_RespectLifoOrdering()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            h.Execute(doc, new AddPlacementCommand(P("a")));
            h.Execute(doc, new AddPlacementCommand(P("b")));

            Assert.True(h.Undo(doc));                 // removes b (last in)
            Assert.DoesNotContain(doc.Placements, p => p.Id == "b");
            Assert.Contains(doc.Placements, p => p.Id == "a");

            Assert.True(h.Undo(doc));                 // removes a
            Assert.Empty(doc.Placements);
        }

        [Fact]
        public void Labels_ReflectTopCommands()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            Assert.Null(h.UndoLabel);
            Assert.Null(h.RedoLabel);

            var add = new AddPlacementCommand(P("a"));
            h.Execute(doc, add);
            Assert.Equal(add.Label, h.UndoLabel);
            Assert.Null(h.RedoLabel);

            h.Undo(doc);
            Assert.Null(h.UndoLabel);
            Assert.Equal(add.Label, h.RedoLabel);
        }

        [Fact]
        public void UndoRedo_OnEmptyStack_ReturnFalse()
        {
            var doc = DocWith();
            var h = new EditorHistory();
            Assert.False(h.Undo(doc));
            Assert.False(h.Redo(doc));
        }

        [Fact]
        public void DragCoalescing_ThreeMovesCollapseToOneUndoStep()
        {
            var doc = DocWith(P("a", 0f, 0f));
            var h = new EditorHistory();
            h.Execute(doc, new MovePlacementCommand("a", 1f, 1f, null));
            h.Execute(doc, new MovePlacementCommand("a", 2f, 2f, null));
            h.Execute(doc, new MovePlacementCommand("a", 3f, 3f, null));

            Assert.Equal(3f, Find(doc, "a").X);
            Assert.Equal(3f, Find(doc, "a").Z);

            Assert.True(h.Undo(doc));                 // a single step back to the origin
            Assert.Equal(0f, Find(doc, "a").X);
            Assert.Equal(0f, Find(doc, "a").Z);
            Assert.False(h.CanUndo);                  // the three merged into one
        }

        [Fact]
        public void Merge_DifferentIds_DoNotCoalesce()
        {
            var doc = DocWith(P("a"), P("b"));
            var h = new EditorHistory();
            h.Execute(doc, new MovePlacementCommand("a", 1f, 0f, null));
            h.Execute(doc, new MovePlacementCommand("b", 2f, 0f, null));

            Assert.True(h.Undo(doc));                 // undoes b only
            Assert.Equal(0f, Find(doc, "b").X);
            Assert.Equal(1f, Find(doc, "a").X);

            Assert.True(h.Undo(doc));                 // then a
            Assert.Equal(0f, Find(doc, "a").X);
        }

        [Fact]
        public void MergeBarrier_ExecuteAfterUndoRedo_StartsNewStep()
        {
            var doc = DocWith(P("a"));
            var h = new EditorHistory();
            h.Execute(doc, new MovePlacementCommand("a", 1f, 0f, null));
            h.Execute(doc, new MovePlacementCommand("a", 2f, 0f, null));   // merges into the step above

            h.Undo(doc);
            Assert.Equal(0f, Find(doc, "a").X);
            h.Redo(doc);
            Assert.Equal(2f, Find(doc, "a").X);

            // A move after undo/redo must not coalesce into the reactivated step.
            h.Execute(doc, new MovePlacementCommand("a", 5f, 0f, null));
            Assert.True(h.Undo(doc));
            Assert.Equal(2f, Find(doc, "a").X);       // fresh step reverted on its own
            Assert.True(h.CanUndo);                   // the earlier move step is still present
        }
    }
}
