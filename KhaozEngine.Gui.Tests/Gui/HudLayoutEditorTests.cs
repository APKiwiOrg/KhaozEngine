using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui;

public sealed class HudLayoutEditorTests
{
    static readonly Vector2 Viewport = new(1280f, 720f);
    static readonly Rect Natural = new(100f, 100f, 200f, 100f);
    readonly HudLayoutEditor _editor = new();
    readonly Pointer _pointer = new();
    readonly HudLayoutRegion[] _regions = [new("frame", Natural), new("orb", Natural)];
    bool _wasDown;

    [Fact]
    public void OnlyTheTopmostRegionCapturesAndTheGestureCannotLeakToTheWorld()
    {
        _editor.Begin(null);
        Step(new Vector2(150f, 125f), false);
        Step(new Vector2(150f, 125f), true);
        Step(new Vector2(210f, 175f), true);
        Step(new Vector2(270f, 225f), true);

        Assert.Equal("orb", _editor.DraggingId);
        Assert.Equal(Natural, _editor.Resolve("frame", Natural, Viewport));
        AssertBounds(new Rect(220f, 200f, 200f, 100f), _editor.Resolve("orb", Natural, Viewport));

        Step(new Vector2(270f, 225f), false);

        Assert.Null(_editor.DraggingId);
        Assert.False(_pointer.IsTapIn(new Rect(0f, 0f, 1280f, 720f)));
    }

    [Fact]
    public void APointerThatWasPressedElsewhereNeverStartsMovingARegion()
    {
        _editor.Begin(null);
        Step(new Vector2(20f, 20f), false);
        Step(new Vector2(20f, 20f), true);
        Step(new Vector2(150f, 125f), true);
        Step(new Vector2(200f, 150f), true);

        Assert.Null(_editor.DraggingId);
        Assert.Equal(Natural, _editor.Resolve("orb", Natural, Viewport));
    }

    [Fact]
    public void ThePressFrameDoesNotApplyMovementFromBeforeThePress()
    {
        _editor.Begin(null);
        Step(new Vector2(20f, 20f), false);
        Step(new Vector2(150f, 125f), true);

        Assert.Equal("orb", _editor.DraggingId);
        Assert.Equal(Natural, _editor.Resolve("orb", Natural, Viewport));
    }

    [Fact]
    public void DraggingBeyondTheEdgeDoesNotBankInvisibleMovement()
    {
        _editor.Begin(null);
        Step(new Vector2(150f, 125f), false);
        Step(new Vector2(150f, 125f), true);
        Step(new Vector2(1500f, 125f), true);
        Assert.Equal(1080f, _editor.Resolve("orb", Natural, Viewport).X, 3);

        Step(new Vector2(1490f, 125f), true);
        Assert.Equal(1070f, _editor.Resolve("orb", Natural, Viewport).X, 3);
    }

    [Fact]
    public void DoneReturnsADetachedSnapshotAndCancelDoesNotChangeTheSuppliedSettings()
    {
        var saved = new Dictionary<string, HudLayoutPosition>
        {
            ["frame"] = new(0.75f, 0.25f),
            ["future-region"] = new(0.25f, 0.75f),
        };
        _editor.Begin(saved);
        _editor.Reset(["frame", "orb"]);
        var committed = _editor.Commit();

        Assert.False(_editor.IsEditing);
        Assert.False(committed.ContainsKey("frame"));
        Assert.Equal(new HudLayoutPosition(0.75f, 0.25f), saved["frame"]);
        Assert.Equal(new HudLayoutPosition(0.25f, 0.75f), committed["future-region"]);

        _editor.Begin(committed);
        _editor.Reset(["future-region"]);
        _editor.Cancel();
        _editor.Cancel();

        Assert.True(committed.ContainsKey("future-region"));
        Assert.False(_editor.IsEditing);
        Assert.Null(_editor.DraggingId);
        Assert.Throws<InvalidOperationException>(() => _editor.Commit());
    }

    [Fact]
    public void ResetThenCancelKeepsTheOriginalPositionOnReopen()
    {
        var saved = new Dictionary<string, HudLayoutPosition> { ["frame"] = new(0.75f, 0.25f) };
        _editor.Begin(saved);
        _editor.Reset(["frame"]);
        Assert.Equal(Natural, _editor.Resolve("frame", Natural, Viewport));
        _editor.Cancel();
        _editor.Begin(saved);

        Assert.Equal(new Rect(860f, 130f, 200f, 100f), _editor.Resolve("frame", Natural, Viewport));
    }

    [Fact]
    public void AResizeDuringCaptureDoesNotTurnTheCoordinateChangeIntoADrag()
    {
        _editor.Begin(null);
        Step(new Vector2(150f, 125f), false);
        Step(new Vector2(150f, 125f), true);
        Step(new Vector2(210f, 175f), true);

        var smaller = new Vector2(640f, 360f);
        Step(new Vector2(130f, 75f), true, smaller);
        AssertBounds(new Rect(30f, 50f, 200f, 100f), _editor.Resolve("orb", Natural, smaller));

        Step(new Vector2(140f, 85f), true, smaller);
        AssertBounds(new Rect(40f, 60f, 200f, 100f), _editor.Resolve("orb", Natural, smaller));
    }

    [Fact]
    public void BeginCopiesSavedValuesAndDropsAnInvalidRegionWithoutLosingTheOthers()
    {
        var saved = new Dictionary<string, HudLayoutPosition>
        {
            ["frame"] = new(0.75f, 0.25f),
            ["orb"] = new(float.NaN, 0.25f),
        };
        _editor.Begin(saved);
        saved["frame"] = new(0.25f, 0.75f);

        Assert.Equal(new Rect(860f, 130f, 200f, 100f), _editor.Resolve("frame", Natural, Viewport));
        Assert.Equal(Natural, _editor.Resolve("orb", Natural, Viewport));
        Assert.False(_editor.Commit().ContainsKey("orb"));
    }

    static void AssertBounds(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
        Assert.Equal(expected.Width, actual.Width, 3);
        Assert.Equal(expected.Height, actual.Height, 3);
    }

    void Step(Vector2 at, bool down, Vector2? viewport = null)
    {
        Vector2 canvas = viewport ?? Viewport;
        var held = new HashSet<MouseButton>();
        var pressed = new HashSet<MouseButton>();
        var released = new HashSet<MouseButton>();
        if (down) held.Add(MouseButton.Left);
        if (down && !_wasDown) pressed.Add(MouseButton.Left);
        if (!down && _wasDown) released.Add(MouseButton.Left);
        _pointer.Update(new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
            held, pressed, at, Vector2.Zero, 0, (int)canvas.X, (int)canvas.Y, mouseReleased: released));
        _editor.Update(_pointer, _regions, canvas);
        _wasDown = down;
    }
}
