using System;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class SculptCursorTests
{
    static readonly SculptBounds Bounds = new(-100, -100, 100, 100);

    static EditorToolController Controller(float radius = 4f) => new(new EditorDocument(new MapDocument()))
    {
        Mode = EditorToolMode.SculptTerrain,
        BrushRadius = radius,
        Field = new TerrainField(new TerrainConfig { GentleAmplitude = 4f }),
    };

    [Theory]
    [InlineData(0.1f)]
    [InlineData(4f)]
    [InlineData(32f)]
    public void The_cursor_uses_the_brush_pick_radius_and_live_terrain_height(float radius)
    {
        var controller = Controller(radius);
        var input = new EditorFrameInput(new Vector3(3f, 100f, 7f), -Vector3.UnitY);
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int count = SculptCursor.Build(controller, input, Bounds, 1f,
            pointerInViewport: true, navigationOwnsPointer: false, modalOpen: false,
            static (_, _) => true, static _ => 1f, static (_, _) => true,
            lines, out SculptOverlayFrame frame);

        Assert.True(frame.Visible);
        Assert.Equal(SculptOverlayState.Hover, frame.State);
        Assert.Equal(SculptBrushOverlay.MaxLines, count);
        foreach (SculptOverlayLine line in lines[..SculptBrushOverlay.Segments])
        {
            Vector3 point = line.Start;
            Assert.Equal(radius, Vector2.Distance(new Vector2(3f, 7f), new Vector2(point.X, point.Z)), 4);
            Assert.Equal(controller.Field!.SampleHeight(point.X, point.Z) + SculptBrushOverlay.Lift, point.Y);
        }
        Assert.True(Vector3.Distance(lines[0].Start, lines[SculptBrushOverlay.Segments - 1].Start) < radius * 0.25f);
    }

    [Theory]
    [InlineData(EditorToolMode.Select, true)]
    [InlineData(EditorToolMode.SculptTerrain, false)]
    public void Inactive_or_chrome_hovered_tools_have_no_cursor(EditorToolMode mode, bool inViewport)
    {
        var controller = Controller();
        controller.Mode = mode;
        var input = new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY);
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        Assert.Equal(0, SculptCursor.Build(controller, input, Bounds, 1f, inViewport,
            navigationOwnsPointer: false, modalOpen: false, static (_, _) => true,
            static _ => 1f, static (_, _) => true, lines, out SculptOverlayFrame frame));
        Assert.False(frame.Visible);
    }

    [Fact]
    public void A_missing_field_or_missed_pick_has_no_cursor()
    {
        var controller = Controller();
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        var miss = new EditorFrameInput(new Vector3(0f, 100f, 0f), Vector3.UnitY);
        Assert.Equal(0, SculptCursor.Build(controller, miss, Bounds, 1f, true,
            navigationOwnsPointer: false, modalOpen: false, static (_, _) => true,
            static _ => 1f, static (_, _) => true, lines, out SculptOverlayFrame missed));
        Assert.True(missed.Visible);
        Assert.Equal(SculptOverlayState.Invalid, missed.State);
        controller.Field = null;
        var down = new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY);
        Assert.Equal(0, SculptCursor.Build(controller, down, Bounds, 1f, true,
            navigationOwnsPointer: false, modalOpen: false, static (_, _) => true,
            static _ => 1f, static (_, _) => true, lines, out SculptOverlayFrame noField));
        Assert.False(noField.Visible);
    }
}
