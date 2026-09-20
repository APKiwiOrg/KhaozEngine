using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.MapEditor;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class EditorViewPanelTests
{
    [Fact]
    public void ViewPanel_StaysIndependentAndTogglesVisibilityWithoutRebuildCallback()
    {
        var visibility = new EditorVisibility();
        var panel = new EditorViewPanel(visibility, () => new[] { "forest", "rocks" });

        panel.Open();
        BoolRow trees = panel.Grid.Rows.OfType<BoolRow>()
            .Single(row => row.Label.Resolve() == "Trees");
        Tap(trees);

        Assert.True(panel.IsOpen);
        Assert.False(visibility.GetCategory(EditorPropCategory.Trees));
    }

    [Fact]
    public void ViewPanel_ShowAllAndLayersUseTheRealRows()
    {
        var visibility = new EditorVisibility();
        visibility.SetLayer("forest", false);
        var panel = new EditorViewPanel(visibility, () => new List<string> { "forest" });
        panel.Open();

        BoolRow showAll = panel.Grid.Rows.OfType<BoolRow>()
            .Single(row => row.Label.Resolve() == "Show All");
        Tap(showAll);

        Assert.True(visibility.GetLayer("forest"));
        Assert.Contains(panel.Grid.Rows.OfType<BoolRow>(),
            row => row.Label.Resolve() == "forest");
    }

    static void Tap(BoolRow row)
    {
        var cell = new Rect(0f, 0f, 200f, 28f);
        var at = new Vector2(100f, 14f);
        var input = new InputManager();
        input.Update(Frame(at, false, false, false)); row.Update(cell, input, 0f);
        input.Update(Frame(at, true, true, false)); row.Update(cell, input, 0f);
        input.Update(Frame(at, false, false, true)); row.Update(cell, input, 0f);
    }

    static InputState Frame(Vector2 at, bool down, bool pressed, bool released) => new(
        new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
        down ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
        pressed ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>(),
        at, Vector2.Zero, 0f, 800, 600,
        mouseReleased: released ? new HashSet<MouseButton> { MouseButton.Left } : new HashSet<MouseButton>());
}
