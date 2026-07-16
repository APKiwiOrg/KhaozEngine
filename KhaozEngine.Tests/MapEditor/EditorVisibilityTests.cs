using System;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>Headless tests for <see cref="EditorVisibility"/>, the editor-session view state: every group,
    /// scatter layer, and element defaults to visible, a group toggle gates every element of that kind at once, a
    /// per-element hide is independent of the group, and the element key is the (kind, id) pair.</summary>
    public class EditorVisibilityTests
    {
        [Fact]
        public void EditorVisibility_GroupAndElementToggles()
        {
            var v = new EditorVisibility();

            // Defaults: every group visible, an unknown layer visible, nothing hidden.
            foreach (VisibilityGroup g in Enum.GetValues<VisibilityGroup>()) Assert.True(v.GetGroup(g));
            Assert.True(v.GetLayer("trees"));
            Assert.False(v.IsElementHidden(SelectionKind.Placement, "hut"));
            Assert.True(v.IsElementVisible(SelectionKind.Placement, "hut"));

            // A group toggle gates every element of that kind, leaving other groups alone.
            v.SetGroup(VisibilityGroup.Placements, false);
            Assert.False(v.GetGroup(VisibilityGroup.Placements));
            Assert.False(v.IsElementVisible(SelectionKind.Placement, "hut"));
            Assert.False(v.IsElementVisible(SelectionKind.Placement, "shed"));
            Assert.True(v.IsElementVisible(SelectionKind.Spawn, "wolf"));
            v.SetGroup(VisibilityGroup.Placements, true);
            Assert.True(v.IsElementVisible(SelectionKind.Placement, "hut"));

            // A per-element hide is independent of the group and of its siblings.
            v.SetElementHidden(SelectionKind.Placement, "hut", true);
            Assert.True(v.IsElementHidden(SelectionKind.Placement, "hut"));
            Assert.False(v.IsElementVisible(SelectionKind.Placement, "hut"));
            Assert.True(v.IsElementVisible(SelectionKind.Placement, "shed"));
            v.SetElementHidden(SelectionKind.Placement, "hut", false);
            Assert.False(v.IsElementHidden(SelectionKind.Placement, "hut"));
            Assert.True(v.IsElementVisible(SelectionKind.Placement, "hut"));

            // Scatter-layer toggles are per layer.
            v.SetLayer("trees", false);
            Assert.False(v.GetLayer("trees"));
            Assert.True(v.GetLayer("rocks"));
            v.SetLayer("trees", true);
            Assert.True(v.GetLayer("trees"));

            // The element key is the (kind, id) pair: the same id under a different kind is a different element.
            v.SetElementHidden(SelectionKind.Feature, "0", true);
            Assert.True(v.IsElementHidden(SelectionKind.Feature, "0"));
            Assert.False(v.IsElementHidden(SelectionKind.Exclusion, "0"));

            // Terrain / None kinds have no group gate, so they stay visible even when nothing has been toggled.
            Assert.True(v.IsElementVisible(SelectionKind.Terrain, ""));
            Assert.False(EditorVisibility.TryGroupFor(SelectionKind.Terrain, out _));
        }

        [Fact]
        public void ScatterOverrides_GroupGatesElement()
        {
            var v = new EditorVisibility();

            // The scatter-override kind maps to its own group, defaulting visible.
            Assert.True(EditorVisibility.TryGroupFor(SelectionKind.ScatterOverride, out VisibilityGroup group));
            Assert.Equal(VisibilityGroup.ScatterOverrides, group);
            Assert.True(v.GetGroup(VisibilityGroup.ScatterOverrides));
            Assert.True(v.IsElementVisible(SelectionKind.ScatterOverride, "0"));

            // Turning the group off hides every override, and it does not leak into the exclusion group.
            v.SetGroup(VisibilityGroup.ScatterOverrides, false);
            Assert.False(v.IsElementVisible(SelectionKind.ScatterOverride, "0"));
            Assert.True(v.IsElementVisible(SelectionKind.Exclusion, "0"));

            // A per-element hide is independent of the group and keyed by (kind, id).
            v.SetGroup(VisibilityGroup.ScatterOverrides, true);
            v.SetElementHidden(SelectionKind.ScatterOverride, "1", true);
            Assert.False(v.IsElementVisible(SelectionKind.ScatterOverride, "1"));
            Assert.True(v.IsElementVisible(SelectionKind.ScatterOverride, "0"));
        }

        [Fact]
        public void RemapIndex_ShiftsHideAcrossReorder_BothDirections()
        {
            // Moving LATER (from < to): list [A,B,C,D], Move(0, 2) -> [B,C,A,D]. A follows to slot 2, B and C
            // (the shifted-through range) each step down one, D (outside the range) is untouched.
            var later = new EditorVisibility();
            later.SetElementHidden(SelectionKind.Feature, "0", true);   // A: the moved element
            later.SetElementHidden(SelectionKind.Feature, "1", true);   // B: shifted-through range
            later.SetElementHidden(SelectionKind.Feature, "3", true);   // D: outside the range
            later.SetElementHidden(SelectionKind.Exclusion, "0", true); // a different kind: must not be touched

            later.RemapIndex(SelectionKind.Feature, fromIndex: 0, toIndex: 2);

            Assert.True(later.IsElementHidden(SelectionKind.Feature, "0"));    // B, shifted down from 1
            Assert.False(later.IsElementHidden(SelectionKind.Feature, "1"));   // C, shifted down from 2, was never hidden
            Assert.True(later.IsElementHidden(SelectionKind.Feature, "2"));    // A, the moved element, still hidden
            Assert.True(later.IsElementHidden(SelectionKind.Feature, "3"));    // D, outside the range, untouched
            Assert.True(later.IsElementHidden(SelectionKind.Exclusion, "0")); // untouched: a different kind's key

            // Moving EARLIER (from > to): list [A,B,C,D], Move(3, 1) -> [A,D,B,C]. D follows to slot 1, B and C
            // (the shifted-through range) each step up one, A (outside the range) is untouched.
            var earlier = new EditorVisibility();
            earlier.SetElementHidden(SelectionKind.Feature, "0", true);   // A: outside the range
            earlier.SetElementHidden(SelectionKind.Feature, "1", true);   // B: shifted-through range
            earlier.SetElementHidden(SelectionKind.Feature, "3", true);   // D: the moved element

            earlier.RemapIndex(SelectionKind.Feature, fromIndex: 3, toIndex: 1);

            Assert.True(earlier.IsElementHidden(SelectionKind.Feature, "0"));    // A, untouched
            Assert.True(earlier.IsElementHidden(SelectionKind.Feature, "1"));    // D, the moved element, now at 1
            Assert.True(earlier.IsElementHidden(SelectionKind.Feature, "2"));    // B, shifted up from 1
            Assert.False(earlier.IsElementHidden(SelectionKind.Feature, "3"));   // C, shifted up from 2, was never hidden

            // Equal indices: a documented no-op, nothing in the hidden set moves.
            var noop = new EditorVisibility();
            noop.SetElementHidden(SelectionKind.Feature, "1", true);
            noop.RemapIndex(SelectionKind.Feature, fromIndex: 1, toIndex: 1);
            Assert.True(noop.IsElementHidden(SelectionKind.Feature, "1"));
        }

        [Fact]
        public void RemoveIndex_DropsRemovedEntryAndShiftsLaterHidesDown()
        {
            var v = new EditorVisibility();
            v.SetElementHidden(SelectionKind.Exclusion, "0", true);   // earlier: untouched
            v.SetElementHidden(SelectionKind.Exclusion, "1", true);   // the removed element itself
            v.SetElementHidden(SelectionKind.Exclusion, "3", true);   // later: shifts down to 2
            v.SetElementHidden(SelectionKind.Feature, "1", true);     // a different kind: must not be touched

            v.RemoveIndex(SelectionKind.Exclusion, 1);

            Assert.True(v.IsElementHidden(SelectionKind.Exclusion, "0"));     // untouched
            Assert.False(v.IsElementHidden(SelectionKind.Exclusion, "1"));    // now holds the old index 2, never hidden
            Assert.True(v.IsElementHidden(SelectionKind.Exclusion, "2"));     // was 3, shifted down by one
            Assert.False(v.IsElementHidden(SelectionKind.Exclusion, "3"));    // nothing hidden at the old tail slot
            Assert.True(v.IsElementHidden(SelectionKind.Feature, "1"));       // untouched: a different kind's key
        }
    }
}
