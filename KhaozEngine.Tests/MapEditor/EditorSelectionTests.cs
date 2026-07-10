using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for the single-selection model: Set/Clear/None routing, id normalization, and the
    /// Changed event semantics. Set always fires Changed (even when the kind and id are unchanged) - a documented
    /// choice pinned here; Clear fires only when a real selection was cleared.</summary>
    public class EditorSelectionTests
    {
        [Fact]
        public void FreshSelection_IsEmptyNoneAndBlankId()
        {
            var sel = new EditorSelection();
            Assert.True(sel.IsEmpty);
            Assert.Equal(SelectionKind.None, sel.Kind);
            Assert.Equal("", sel.Id);
        }

        [Fact]
        public void Set_UpdatesKindAndId_AndFiresChanged()
        {
            var sel = new EditorSelection();
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Set(SelectionKind.Placement, "hut");

            Assert.False(sel.IsEmpty);
            Assert.Equal(SelectionKind.Placement, sel.Kind);
            Assert.Equal("hut", sel.Id);
            Assert.Equal(1, changed);
        }

        [Fact]
        public void Set_FiresChanged_EvenWhenValueUnchanged()
        {
            // Documented choice from Task 2: Set fires Changed unconditionally for a concrete kind, so a repeat
            // selection of the same element still notifies subscribers. Pinned here so it is not "optimized" away.
            var sel = new EditorSelection();
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Set(SelectionKind.Spawn, "wolf");
            sel.Set(SelectionKind.Spawn, "wolf");
            sel.Set(SelectionKind.Spawn, "wolf");

            Assert.Equal(3, changed);
        }

        [Fact]
        public void Set_NullId_NormalizesToEmptyString()
        {
            var sel = new EditorSelection();
            sel.Set(SelectionKind.Feature, null!);

            Assert.Equal(SelectionKind.Feature, sel.Kind);
            Assert.Equal("", sel.Id);
        }

        [Fact]
        public void Set_None_ClearsSelection()
        {
            var sel = new EditorSelection();
            sel.Set(SelectionKind.Placement, "hut");
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Set(SelectionKind.None, "ignored");

            Assert.True(sel.IsEmpty);
            Assert.Equal(SelectionKind.None, sel.Kind);
            Assert.Equal("", sel.Id);
            Assert.Equal(1, changed);        // routes through Clear, which had a selection to clear
        }

        [Fact]
        public void Clear_FiresChanged_OnlyWhenSomethingWasSelected()
        {
            var sel = new EditorSelection();
            sel.Set(SelectionKind.Region, "town");
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Clear();
            Assert.True(sel.IsEmpty);
            Assert.Equal(1, changed);        // a real selection was cleared

            sel.Clear();
            Assert.Equal(1, changed);        // redundant clear raises no spurious event
        }

        [Fact]
        public void Clear_OnEmptySelection_DoesNotFireChanged()
        {
            var sel = new EditorSelection();
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Clear();

            Assert.True(sel.IsEmpty);
            Assert.Equal(0, changed);
        }

        [Fact]
        public void Set_None_OnEmptySelection_DoesNotFireChanged()
        {
            var sel = new EditorSelection();
            int changed = 0;
            sel.Changed += () => changed++;

            sel.Set(SelectionKind.None, "x");

            Assert.True(sel.IsEmpty);
            Assert.Equal(0, changed);        // Set(None,..) on an empty selection is a no-op clear
        }
    }
}
