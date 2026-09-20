using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class SculptBrushOverlayTests
{
    static readonly SculptBounds WideBounds = new(-100, -100, 100, 100);

    static EditorToolController Controller(SculptBrush brush = SculptBrush.Raise, float radius = 8f)
    {
        var controller = new EditorToolController(new EditorDocument(new MapDocument()))
        {
            Mode = EditorToolMode.SculptTerrain,
            Brush = brush,
            BrushRadius = radius,
            Field = new TerrainField(new TerrainConfig { GentleAmplitude = 0f }),
        };
        return controller;
    }

    [Fact]
    public void Build_follows_slope_and_places_half_strength_falloff_guide()
    {
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        static float Slope(float x, float z) => x * 0.5f - z * 0.25f;

        int count = SculptBrushOverlay.Build(
            new Vector3(10f, Slope(10f, 20f), 20f), radius: 8f, WideBounds, cellSize: 1f,
            Slope, static (_, _) => true, static (_, _) => true, lines);

        Assert.Equal(SculptBrushOverlay.MaxLines, count);
        Assert.Equal(SculptBrushOverlay.Segments,
            CountPart(lines[..count], SculptOverlayPart.OuterFootprint));
        Assert.Equal(SculptBrushOverlay.Segments,
            CountPart(lines[..count], SculptOverlayPart.FalloffGuide));
        Assert.Equal(2, CountPart(lines[..count], SculptOverlayPart.CenterMarker));
        foreach (SculptOverlayLine line in lines[..count])
        {
            Assert.Equal(Slope(line.Start.X, line.Start.Z) + SculptBrushOverlay.Lift, line.Start.Y, 4);
            Assert.Equal(Slope(line.End.X, line.End.Z) + SculptBrushOverlay.Lift, line.End.Y, 4);
        }

        SculptOverlayLine guide = lines[SculptBrushOverlay.Segments];
        Assert.Equal(4f, Vector2.Distance(new Vector2(10f, 20f), new Vector2(guide.Start.X, guide.Start.Z)), 4);
        Assert.Equal(0.5f, TerrainSculptBrush.Falloff(0.5f), 4);
    }

    [Fact]
    public void Build_clips_to_paintable_bounds_and_discards_non_finite_samples()
    {
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        var bounds = new SculptBounds(0, 0, 6, 6);
        static float Height(float x, float z) => x > 5.5f && z > 2f ? float.NaN : x + z;

        int count = SculptBrushOverlay.Build(
            new Vector3(5f, 5f, 3f), radius: 4f, bounds, cellSize: 1f,
            Height, static (_, _) => true, static (_, _) => true, lines);

        Assert.InRange(count, 1, SculptBrushOverlay.MaxLines - 1);
        Assert.All(lines[..count].ToArray(), line =>
        {
            Assert.InRange(line.Start.X, 0f, 6f);
            Assert.InRange(line.Start.Z, 0f, 6f);
            Assert.InRange(line.End.X, 0f, 6f);
            Assert.InRange(line.End.Z, 0f, 6f);
            Assert.True(IsFinite(line.Start));
            Assert.True(IsFinite(line.End));
        });
    }

    [Fact]
    public void Build_omits_segments_that_cross_unloaded_terrain()
    {
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int count = SculptBrushOverlay.Build(
            new Vector3(3f, 0f, 0f), radius: 4f, WideBounds, cellSize: 1f,
            static (_, _) => 0f, static (x, _) => x < 5f, static (_, _) => true, lines);

        Assert.InRange(count, 1, SculptBrushOverlay.MaxLines - 1);
        Assert.All(lines[..count].ToArray(), line =>
        {
            Assert.True(line.Start.X < 5f);
            Assert.True(line.End.X < 5f);
        });
        Assert.DoesNotContain(lines[..count].ToArray(), line =>
            line.Part == SculptOverlayPart.OuterFootprint && (line.Start.X >= 5f || line.End.X >= 5f));
    }

    [Fact]
    public void Build_requires_the_fixed_reusable_capacity_before_writing()
    {
        var tooSmall = new SculptOverlayLine[SculptBrushOverlay.MaxLines - 1];

        Assert.Throws<ArgumentException>(() => SculptBrushOverlay.Build(
            Vector3.Zero, radius: 4f, WideBounds, cellSize: 1f,
            static (_, _) => 0f, static (_, _) => true, static (_, _) => true, tooSmall));
        Assert.All(tooSmall, line => Assert.Equal(default, line));
    }

    [Fact]
    public void Build_uses_the_screen_derived_center_marker_size()
    {
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        float halfSize = SculptBrushOverlay.ScreenMarkerHalfSize(240f);

        int count = SculptBrushOverlay.Build(
            Vector3.Zero, 8f, halfSize, WideBounds, 1f,
            static (_, _) => 0f, static (_, _) => true, static (_, _) => true, lines);

        SculptOverlayLine[] marker = lines[..count].ToArray()
            .Where(line => line.Part == SculptOverlayPart.CenterMarker).ToArray();
        Assert.Equal(2, marker.Length);
        Assert.Equal(6f, halfSize, 4);
        Assert.Equal(12f, Vector3.Distance(marker[0].Start, marker[0].End), 4);
        Assert.Equal(12f, Vector3.Distance(marker[1].Start, marker[1].End), 4);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Cursor_suppresses_chrome_navigation_and_modal_frames(
        bool pointerInViewport, bool navigationOwnsPointer, bool modalOpen)
    {
        EditorToolController controller = Controller();
        var input = new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY);
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int count = SculptCursor.Build(controller, input, WideBounds, cellSize: 1f,
            pointerInViewport, navigationOwnsPointer, modalOpen, static (_, _) => true,
            static _ => 1f, static (_, _) => true, lines, out SculptOverlayFrame frame);

        Assert.Equal(0, count);
        Assert.False(frame.Visible);
    }

    [Fact]
    public void Cursor_reports_hover_active_and_invalid_with_operation_labels()
    {
        EditorToolController controller = Controller(SculptBrush.Lower);
        var hover = new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY);
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int hoverCount = SculptCursor.Build(controller, hover, WideBounds, 1f,
            true, false, false, static (_, _) => true, static _ => 1f,
            static (_, _) => true, lines, out SculptOverlayFrame hoverFrame);
        Assert.Equal(SculptOverlayState.Hover, hoverFrame.State);
        Assert.True(hoverFrame.Visible);
        Assert.Equal("Lower", MapEditorStrings.Resolve(hoverFrame.OperationLabel));
        Assert.Equal("Hover", MapEditorStrings.Resolve(hoverFrame.StateLabel));
        Assert.True(hoverCount > 0);

        controller.Update(new EditorFrameInput(new Vector3(0f, 100f, 0f), -Vector3.UnitY,
            pointerPressed: true, pointerDown: true, dt: 0.016f));
        int activeCount = SculptCursor.Build(controller, hover, WideBounds, 1f,
            true, false, false, static (_, _) => true, static _ => 1f,
            static (_, _) => true, lines, out SculptOverlayFrame activeFrame);
        Assert.Equal(SculptOverlayState.Active, activeFrame.State);
        Assert.Equal("Active", MapEditorStrings.Resolve(activeFrame.StateLabel));
        Assert.True(activeCount > 0);

        int invalidCount = SculptCursor.Build(controller,
            new EditorFrameInput(new Vector3(0f, 100f, 0f), Vector3.UnitY), WideBounds, 1f,
            true, false, false, static (_, _) => true, static _ => 1f,
            static (_, _) => true, lines, out SculptOverlayFrame invalidFrame);
        Assert.Equal(0, invalidCount);
        Assert.True(invalidFrame.Visible);
        Assert.Equal(SculptOverlayState.Invalid, invalidFrame.State);
        Assert.Equal("Unavailable", MapEditorStrings.Resolve(invalidFrame.StateLabel));
    }

    [Fact]
    public void Cursor_has_no_valid_footprint_when_center_is_unloaded_or_outside_bounds()
    {
        EditorToolController controller = Controller();
        var input = new EditorFrameInput(new Vector3(8f, 100f, 8f), -Vector3.UnitY);
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int unloaded = SculptCursor.Build(controller, input, WideBounds, 1f,
            true, false, false, static (_, _) => false, static _ => 1f,
            static (_, _) => true, lines, out SculptOverlayFrame unloadedFrame);
        int outside = SculptCursor.Build(controller, input, new SculptBounds(0, 0, 4, 4), 1f,
            true, false, false, static (_, _) => true, static _ => 1f,
            static (_, _) => true, lines, out SculptOverlayFrame outsideFrame);

        Assert.Equal(0, unloaded);
        Assert.Equal(SculptOverlayState.Invalid, unloadedFrame.State);
        Assert.Equal(0, outside);
        Assert.Equal(SculptOverlayState.Invalid, outsideFrame.State);
    }

    static bool IsFinite(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    static int CountPart(ReadOnlySpan<SculptOverlayLine> lines, SculptOverlayPart part)
    {
        int count = 0;
        foreach (SculptOverlayLine line in lines)
            if (line.Part == part) count++;
        return count;
    }
}
