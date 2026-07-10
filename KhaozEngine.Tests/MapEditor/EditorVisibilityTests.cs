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
    }
}
