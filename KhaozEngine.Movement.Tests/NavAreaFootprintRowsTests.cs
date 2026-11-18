using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NavAreaFootprintRowsTests
{
    internal const int Size = 16;
    internal const uint LowerArea = 1u, UpperArea = 2u;
    internal const double SurfaceReach = 0.5d;

    [Theory]
    [InlineData(2.5f, 5.5f, 13.5f, 5.5f, 0.5f)] // Horizontal, tangent to the next row.
    [InlineData(5.5f, 2.5f, 5.5f, 13.5f, 0.5f)] // Vertical.
    [InlineData(2.5f, 2.5f, 13.5f, 13.5f, 0f)] // Grid corners at zero radius.
    [InlineData(13.5f, 13.5f, 2.5f, 2.5f, 0f)] // Reverse through the same corners.
    [InlineData(13.5f, 2.5f, 2.5f, 11.5f, 1.75f)] // Negative slope and a radius larger than a cell.
    [InlineData(6.5f, 6.5f, 6.5f, 6.5f, 0.75f)] // Zero length.
    [InlineData(0.5f, 0.5f, 14.5f, 13.5f, 0.5f)] // Tangent to the inclusive capture minimum.
    [InlineData(2.5f, 5.4999995f, 13.5f, 5.4999995f, 0.5f)] // One float below tangency.
    [InlineData(2.5f, 5.5000005f, 13.5f, 5.5000005f, 0.5f)] // One float above tangency.
    [InlineData(2.5f, 5.4999995f, 13.5f, 5.5000005f, 1.25f)] // Small nonzero Z delta.
    [InlineData(2.5f, 2.5f, 13.5f, 11.5f, 0.3f, 1048576f, 0.25f)] // Translated coordinates.
    [InlineData(2.5f, 2.5f, 13.5f, 11.5f, 0.3f, 0f, 5.684341886080802e-14f)] // Tiny grid.
    public void EveryIndividuallyExcludedColumnMatchesTheRectangle(float ax, float az, float bx, float bz,
        float radiusCells, float origin = 0f, float cellSize = 1f)
    {
        PhysicsNavBakeOptions options = Options(origin, -origin, cellSize);
        var from = new Vector3(origin + ax * cellSize, 0f, -origin + az * cellSize);
        var to = new Vector3(origin + bx * cellSize, 0f, -origin + bz * cellSize);
        MoveTuning tuning = MoveTuning.Default with { CapsuleRadius = radiusCells * cellSize };
        PhysicsNavColumns open = Columns(options);
        Assert.True(Footprint(open, tuning).AcceptsSegment(from, to));

        int refusals = 0;
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                PhysicsNavColumns columns = Columns(options, (x, z));
                bool expected = Rectangle(columns, tuning, from, to, excluded: UpperArea);
                var footprint = Footprint(columns, tuning).ExcludingAreas(UpperArea);
                Assert.Equal(expected, footprint.AcceptsSegment(from, to));
                Assert.Equal(expected, footprint.AcceptsExclusions(from, to));
                if (!expected) refusals++;
            }
        Assert.InRange(refusals, 1, Size * Size - 1);
    }

    [Theory]
    [InlineData(0f, 0f, true, true)] // Unselected upper surface cannot refuse the segment.
    [InlineData(4f, 4f, false, false)] // Selected upper surface is excluded.
    [InlineData(0f, 4f, false, false)] // A height range intersects both bands.
    [InlineData(-2f, -2f, false, true)] // Exclusions do not apply the support reach rule.
    public void StackedColumnsKeepTheHeightAndExclusionRules(float low, float high, bool accepted, bool exclusionsAccepted)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0.3f };
        var columns = Columns(Options());
        var footprint = Footprint(columns, tuning).ExcludingAreas(UpperArea);
        Vector3 from = new(2.5f, low, 2.5f), to = new(13.5f, high, 11.5f);
        Assert.Equal(accepted, Rectangle(columns, tuning, from, to, excluded: UpperArea));
        Assert.Equal(accepted, footprint.AcceptsSegment(from, to));
        Assert.Equal(exclusionsAccepted, Rectangle(columns, tuning, from, to, excluded: UpperArea, onlyExclusions: true));
        Assert.Equal(exclusionsAccepted, footprint.AcceptsExclusions(from, to));
    }

    [Theory]
    [InlineData(15.499999f, true)]
    [InlineData(15.5f, false)]
    [InlineData(15.500001f, false)]
    public void HalfOpenCaptureMaximumStillRefusesAtTangency(float end, bool accepted)
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0.5f };
        var columns = Columns(Options());
        Vector3 from = new(2.5f, 0f, 2.5f), to = new(end, 0f, 11.5f);
        Assert.Equal(accepted, Rectangle(columns, tuning, from, to));
        Assert.Equal(accepted, Footprint(columns, tuning).AcceptsSegment(from, to));
    }

    [Fact]
    public void LongDiagonalDoesNotTestMostOfItsRectangle()
    {
        var tuning = MoveTuning.Default with { CapsuleRadius = 0.25f };
        var columns = Columns(Options());
        Vector3 from = new(1.5f, 0f, 1.5f), to = new(14.5f, 0f, 14.5f);
        Assert.True(Rectangle(columns, tuning, from, to));
        Assert.True(Footprint(columns, tuning).AcceptsSegmentForTests(from, to, out long candidates));
        // The legacy 14 by 14 rectangle is mostly corner cells far from the swept line.
        Assert.InRange(candidates, 1, 14 * 14 / 2 - 1);
    }

    [Fact]
    public void UncertifiedCellScaleRetainsTheRectangle()
    {
        const float cellSize = 5.684341886080802e-14f;
        var tuning = MoveTuning.Default with { CapsuleRadius = 0.25f * cellSize };
        var columns = Columns(Options(size: cellSize));
        Vector3 from = new(1.5f * cellSize, 0f, 1.5f * cellSize), to = new(14.5f * cellSize, 0f, 14.5f * cellSize);
        Assert.True(Rectangle(columns, tuning, from, to));
        Assert.True(Footprint(columns, tuning).AcceptsSegmentForTests(from, to, out long candidates));
        Assert.Equal(14L * 14, candidates);
    }

    internal static PhysicsNavBakeOptions Options(float x = 0f, float z = 0f, float size = 1f)
        => new(x, z, x + Size * size, z + Size * size, size, 8f, 9f, 0.8f, Size * Size, 2 * Size * Size);

    internal static PhysicsNavColumns Columns(PhysicsNavBakeOptions options, (int X, int Z)? excludesLower = null)
    {
        var starts = new int[Size * Size + 1];
        var surfaces = new PhysicsNavSurface[2 * Size * Size];
        for (int i = 0; i < Size * Size; i++)
        {
            starts[i] = 2 * i;
            surfaces[2 * i] = new(0f, 4f, excludesLower == (i % Size, i / Size) ? UpperArea : LowerArea);
            surfaces[2 * i + 1] = new(4f, float.PositiveInfinity, UpperArea);
        }
        starts[^1] = surfaces.Length;
        return PhysicsNavColumns.Own(options, Size, Size, starts, surfaces, []);
    }

    internal static NavAreaFootprint Footprint(PhysicsNavColumns columns, MoveTuning tuning)
        => new(columns, columns.Options, tuning, default, SurfaceReach);

    // Frozen rectangle traversal with the unchanged exact geometry predicate. No row bound is used here.
    internal static bool Rectangle(PhysicsNavColumns columns, MoveTuning tuning, Vector3 from, Vector3 to,
        uint excluded = 0u, bool onlyExclusions = false)
    {
        var options = columns.Options;
        double radius = tuning.CapsuleRadius, size = options.CellSize;
        double minX = Math.Min(from.X, to.X) - radius, maxX = Math.Max(from.X, to.X) + radius;
        double minZ = Math.Min(from.Z, to.Z) - radius, maxZ = Math.Max(from.Z, to.Z) + radius;
        if (minX < options.MinX || minZ < options.MinZ || maxX >= options.MaxX || maxZ >= options.MaxZ)
            return false;
        int Cell(double value, float origin) => (int)Math.Floor((value - origin) / size);
        int x0 = Cell(minX, options.MinX), x1 = Cell(maxX, options.MinX);
        int z0 = Cell(minZ, options.MinZ), z1 = Cell(maxZ, options.MinZ);
        double low = Math.Min(from.Y, to.Y), high = Math.Max(from.Y, to.Y);
        for (int z = z0; z <= z1; z++)
            for (int x = x0; x <= x1; x++)
            {
                if (!NavFootprintGeometry.Touches(from, to, options.MinX + x * size, options.MinZ + z * size, size, radius))
                    continue;
                if ((uint)x >= (uint)columns.Width || (uint)z >= (uint)columns.Height) return false;
                // The fixture always has two surfaces, with their shared selection boundary at Y = 2.
                var column = columns.GetColumn(x, z);
                if (low <= 2d && !Accepts(column[0], low, Math.Min(high, 2d))) return false;
                if (high >= 2d && !Accepts(column[1], Math.Max(low, 2d), high)) return false;
            }
        return true;

        bool Accepts(PhysicsNavSurface surface, double a, double b) => (surface.Areas & excluded) == 0u &&
            (onlyExclusions || (surface.Height - a <= SurfaceReach && b - surface.Height <= SurfaceReach));
    }
}
