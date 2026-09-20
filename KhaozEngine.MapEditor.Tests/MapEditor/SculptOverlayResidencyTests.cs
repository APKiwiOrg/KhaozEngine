using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class SculptOverlayResidencyTests
{
    const float ChunkSize = 64f;
    static readonly SculptBounds WideBounds = new(-1000, -1000, 1000, 1000);

    [Fact]
    public void Segment_loaded_rejects_an_unloaded_corner_cell_between_loaded_endpoints()
    {
        (Vector2 _, Vector2 from, Vector2 to) = CornerCrossingCircleSegment();

        Assert.Equal(new ChunkCoord(0, -1), ChunkGrid.CoordOf(from.X, from.Y, ChunkSize));
        Assert.Equal(new ChunkCoord(-1, 0), ChunkGrid.CoordOf(to.X, to.Y, ChunkSize));
        Assert.True(LoadedCell(0, -1));
        Assert.True(LoadedCell(-1, 0));

        Assert.False(SculptBrushOverlay.SegmentIsLoaded(from, to, ChunkSize, LoadedCell));
    }

    [Fact]
    public void Build_omits_the_circle_chord_that_crosses_an_unloaded_corner_cell()
    {
        (Vector2 center, Vector2 from, Vector2 to) = CornerCrossingCircleSegment();
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int count = SculptBrushOverlay.Build(new Vector3(center.X, 0f, center.Y), 256f,
            WideBounds, 1f, static (_, _) => 0f, LoadedWorld, SegmentLoaded, lines);

        Assert.Equal(0, CountMatchingOuterChords(lines[..count], from, to));
        Assert.True(CountPart(lines[..count], SculptOverlayPart.OuterFootprint)
            < SculptBrushOverlay.Segments);
    }

    [Fact]
    public void Build_omits_the_falloff_chord_that_crosses_an_unloaded_corner_cell()
    {
        (Vector2 center, Vector2 from, Vector2 to) = CornerCrossingCircleSegment();
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];

        int count = SculptBrushOverlay.Build(new Vector3(center.X, 0f, center.Y), 512f,
            WideBounds, 1f, static (_, _) => 0f, LoadedWorld, SegmentLoaded, lines);

        Assert.Equal(0, CountMatchingChords(
            lines[..count], SculptOverlayPart.FalloffGuide, from, to));
        Assert.True(CountPart(lines[..count], SculptOverlayPart.FalloffGuide)
            < SculptBrushOverlay.Segments);
    }

    [Fact]
    public void Build_omits_a_center_cross_line_that_crosses_an_unloaded_hole()
    {
        Span<SculptOverlayLine> lines = stackalloc SculptOverlayLine[SculptBrushOverlay.MaxLines];
        var center = new Vector3(-100f, 0f, 32f);

        int count = SculptBrushOverlay.Build(center, 4f, 200f,
            WideBounds, 1f, static (_, _) => 0f, LoadedWorld, SegmentLoaded, lines);

        SculptOverlayLine[] markers = lines[..count].ToArray()
            .Where(line => line.Part == SculptOverlayPart.CenterMarker).ToArray();
        Assert.Single(markers);
        Assert.Equal(markers[0].Start.X, markers[0].End.X, 4);
    }

    [Fact]
    public void Segment_loaded_rejects_pathological_finite_ranges_without_visiting_cells()
    {
        int visits = 0;
        bool Loaded(int _, int __)
        {
            visits++;
            return true;
        }

        Assert.False(SculptBrushOverlay.SegmentIsLoaded(
            Vector2.Zero, new Vector2(1_000_000f, 1_000_000f), 1f, Loaded));
        Assert.False(SculptBrushOverlay.SegmentIsLoaded(
            new Vector2(float.MaxValue, 0f), new Vector2(float.MaxValue, 1f), 1f, Loaded));
        Assert.Equal(0, visits);
    }

    static (Vector2 Center, Vector2 From, Vector2 To) CornerCrossingCircleSegment()
    {
        const float radius = 256f;
        Vector2 from = CirclePoint(8, radius);
        Vector2 to = CirclePoint(9, radius);
        Vector2 translation = new Vector2(0.1f, 0.1f) - (from + to) * 0.5f;
        return (translation, from + translation, to + translation);
    }

    static Vector2 CirclePoint(int sample, float radius)
    {
        float angle = sample * (2f * MathF.PI / SculptBrushOverlay.Segments);
        return new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
    }

    static bool LoadedCell(int x, int z) => x != 0 || z != 0;

    static bool LoadedWorld(float x, float z)
    {
        ChunkCoord chunk = ChunkGrid.CoordOf(x, z, ChunkSize);
        return LoadedCell(chunk.X, chunk.Z);
    }

    static bool SegmentLoaded(Vector2 from, Vector2 to) =>
        SculptBrushOverlay.SegmentIsLoaded(from, to, ChunkSize, LoadedCell);

    static int CountMatchingOuterChords(
        ReadOnlySpan<SculptOverlayLine> lines, Vector2 expectedFrom, Vector2 expectedTo) =>
        CountMatchingChords(lines, SculptOverlayPart.OuterFootprint, expectedFrom, expectedTo);

    static int CountMatchingChords(
        ReadOnlySpan<SculptOverlayLine> lines, SculptOverlayPart part,
        Vector2 expectedFrom, Vector2 expectedTo)
    {
        int count = 0;
        foreach (SculptOverlayLine line in lines)
        {
            if (line.Part != part) continue;
            var from = new Vector2(line.Start.X, line.Start.Z);
            var to = new Vector2(line.End.X, line.End.Z);
            if ((Near(from, expectedFrom) && Near(to, expectedTo))
                || (Near(from, expectedTo) && Near(to, expectedFrom))) count++;
        }
        return count;
    }

    static int CountPart(ReadOnlySpan<SculptOverlayLine> lines, SculptOverlayPart part)
    {
        int count = 0;
        foreach (SculptOverlayLine line in lines)
            if (line.Part == part) count++;
        return count;
    }

    static bool Near(Vector2 a, Vector2 b) => Vector2.DistanceSquared(a, b) < 1e-6f;
}
