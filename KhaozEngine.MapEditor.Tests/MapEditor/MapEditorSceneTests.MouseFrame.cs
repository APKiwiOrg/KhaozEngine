using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.MapEditor
{
    /// <summary>
    /// The mouse frame builder for <see cref="MapEditorSceneTests"/>, in its own file because the test class
    /// itself is at its file-size baseline and may not grow.
    /// </summary>
    public partial class MapEditorSceneTests
    {
        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        // A minimal mouse frame for driving InputManager headless (the SceneManagerTests Frame idiom).
        InputState MouseFrame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(down);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }
    }
}
