using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Collision;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapEditor;

/// <summary>The visible role of one terrain sculpt overlay segment.</summary>
internal enum SculptOverlayPart
{
    OuterFootprint,
    FalloffGuide,
    CenterMarker,
}

/// <summary>The interaction state shown by the terrain sculpt overlay and its label.</summary>
internal enum SculptOverlayState
{
    Hover,
    Active,
    Invalid,
}

/// <summary>One caller-buffered world-space line in the terrain sculpt overlay.</summary>
internal readonly record struct SculptOverlayLine(Vector3 Start, Vector3 End, SculptOverlayPart Part);

/// <summary>The current overlay label and optional world anchor shared by the 3D and UI passes.</summary>
internal readonly record struct SculptOverlayFrame(
    bool Visible, SculptOverlayState State, StringId OperationLabel, StringId StateLabel,
    Vector3 Center, bool HasWorldAnchor)
{
    internal static SculptOverlayFrame Hidden => default;
}

/// <summary>Builds bounded, terrain-following sculpt feedback into a caller-owned line buffer.</summary>
internal static class SculptBrushOverlay
{
    internal const int Segments = 64;
    internal const int MaxLines = Segments * 2 + 2;
    internal const float Lift = 0.1f;
    const float FalloffGuideScale = 0.5f;
    const float MinCenterHalfSize = 0.25f;
    const float MaxCenterHalfSize = 1f;
    const float MarkerHalfSizePerCameraDistance = 0.025f;
    const int MaxSegmentGridSteps = 4096;

    internal static float ScreenMarkerHalfSize(float cameraDistance) =>
        float.IsFinite(cameraDistance)
            ? MathF.Max(MinCenterHalfSize, cameraDistance * MarkerHalfSizePerCameraDistance)
            : MinCenterHalfSize;

    internal static int Build(Vector3 center, float radius, in SculptBounds bounds, float cellSize,
        Func<float, float, float> sampleHeight, Func<float, float, bool> isLoaded,
        Func<Vector2, Vector2, bool> isSegmentLoaded, Span<SculptOverlayLine> lines) => Build(center, radius,
        Math.Clamp(radius * 0.15f, MinCenterHalfSize, MaxCenterHalfSize),
        bounds, cellSize, sampleHeight, isLoaded, isSegmentLoaded, lines);

    internal static int Build(Vector3 center, float radius, float centerHalfSize,
        in SculptBounds bounds, float cellSize,
        Func<float, float, float> sampleHeight, Func<float, float, bool> isLoaded,
        Func<Vector2, Vector2, bool> isSegmentLoaded,
        Span<SculptOverlayLine> lines)
    {
        ArgumentNullException.ThrowIfNull(sampleHeight);
        ArgumentNullException.ThrowIfNull(isLoaded);
        ArgumentNullException.ThrowIfNull(isSegmentLoaded);
        if (lines.Length < MaxLines)
            throw new ArgumentException("The overlay buffer must hold the complete bounded geometry.", nameof(lines));
        if (!bounds.HasArea || !float.IsFinite(cellSize) || !(cellSize > 0f)
            || !float.IsFinite(radius) || !(radius > 0f)
            || !float.IsFinite(centerHalfSize) || !(centerHalfSize > 0f)
            || !float.IsFinite(center.X) || !float.IsFinite(center.Z)) return 0;

        float minX = bounds.MinCellX * cellSize;
        float minZ = bounds.MinCellZ * cellSize;
        float maxX = bounds.MaxCellX * cellSize;
        float maxZ = bounds.MaxCellZ * cellSize;
        if (center.X < minX || center.X > maxX || center.Z < minZ || center.Z > maxZ
            || !isLoaded(center.X, center.Z)) return 0;

        int count = 0;
        count += BuildRing(center, radius, minX, minZ, maxX, maxZ,
            sampleHeight, isLoaded, isSegmentLoaded, SculptOverlayPart.OuterFootprint, lines[count..]);
        count += BuildRing(center, radius * FalloffGuideScale, minX, minZ, maxX, maxZ,
            sampleHeight, isLoaded, isSegmentLoaded, SculptOverlayPart.FalloffGuide, lines[count..]);

        float half = MathF.Max(MinCenterHalfSize, centerHalfSize);
        TryAddLine(center.X - half, center.Z, center.X + half, center.Z,
            minX, minZ, maxX, maxZ, sampleHeight, isLoaded, isSegmentLoaded,
            SculptOverlayPart.CenterMarker, lines, ref count);
        TryAddLine(center.X, center.Z - half, center.X, center.Z + half,
            minX, minZ, maxX, maxZ, sampleHeight, isLoaded, isSegmentLoaded,
            SculptOverlayPart.CenterMarker, lines, ref count);
        return count;
    }

    /// <summary>True when a segment's exact 4-connected grid supercover contains only loaded cells. Pathological
    /// finite coordinates and segments spanning more than a bounded number of cells fail closed before traversal.</summary>
    internal static bool SegmentIsLoaded(
        Vector2 from, Vector2 to, float cellSize, Func<int, int, bool> isCellLoaded)
    {
        ArgumentNullException.ThrowIfNull(isCellLoaded);
        if (!float.IsFinite(from.X) || !float.IsFinite(from.Y)
            || !float.IsFinite(to.X) || !float.IsFinite(to.Y)
            || !float.IsFinite(cellSize) || !(cellSize > 0f)) return false;

        double startX = Math.Floor((double)from.X / cellSize);
        double startZ = Math.Floor((double)from.Y / cellSize);
        double endX = Math.Floor((double)to.X / cellSize);
        double endZ = Math.Floor((double)to.Y / cellSize);
        const double safeMin = int.MinValue + 1d;
        const double safeMax = int.MaxValue - 1d;
        if (startX < safeMin || startX > safeMax || startZ < safeMin || startZ > safeMax
            || endX < safeMin || endX > safeMax || endZ < safeMin || endZ > safeMax) return false;

        long steps = Math.Abs((long)endX - (long)startX) + Math.Abs((long)endZ - (long)startZ);
        if (steps > MaxSegmentGridSteps) return false;
        return GridRay.Trace(from, to, cellSize, isCellLoaded);
    }

    internal static void Draw(Scene3D scene, ReadOnlySpan<SculptOverlayLine> lines, int count, Color operationColor)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (count < 0 || count > lines.Length) throw new ArgumentOutOfRangeException(nameof(count));
        int falloffIndex = 0;
        for (int i = 0; i < count; i++)
        {
            SculptOverlayLine line = lines[i];
            if (line.Part == SculptOverlayPart.FalloffGuide && (falloffIndex++ & 1) != 0) continue;
            Color color = line.Part == SculptOverlayPart.CenterMarker ? Color.White : operationColor;
            scene.DebugLine(line.Start, line.End, color);
        }
    }

    static int BuildRing(Vector3 center, float radius, float minX, float minZ, float maxX, float maxZ,
        Func<float, float, float> sampleHeight, Func<float, float, bool> isLoaded,
        Func<Vector2, Vector2, bool> isSegmentLoaded,
        SculptOverlayPart part, Span<SculptOverlayLine> lines)
    {
        Span<Vector3> points = stackalloc Vector3[Segments];
        Span<byte> valid = stackalloc byte[Segments];
        for (int i = 0; i < Segments; i++)
        {
            float angle = i * (2f * MathF.PI / Segments);
            float x = Math.Clamp(center.X + MathF.Cos(angle) * radius, minX, maxX);
            float z = Math.Clamp(center.Z + MathF.Sin(angle) * radius, minZ, maxZ);
            valid[i] = TryPoint(x, z, sampleHeight, isLoaded, out points[i]) ? (byte)1 : (byte)0;
        }

        int count = 0;
        for (int i = 0; i < Segments; i++)
        {
            int next = (i + 1) % Segments;
            if (valid[i] == 0 || valid[next] == 0 || SamePosition(points[i], points[next])) continue;
            if (!isSegmentLoaded(
                    new Vector2(points[i].X, points[i].Z), new Vector2(points[next].X, points[next].Z))) continue;
            lines[count++] = new SculptOverlayLine(points[i], points[next], part);
        }
        return count;
    }

    static void TryAddLine(float ax, float az, float bx, float bz,
        float minX, float minZ, float maxX, float maxZ,
        Func<float, float, float> sampleHeight, Func<float, float, bool> isLoaded,
        Func<Vector2, Vector2, bool> isSegmentLoaded,
        SculptOverlayPart part, Span<SculptOverlayLine> lines, ref int count)
    {
        ax = Math.Clamp(ax, minX, maxX);
        az = Math.Clamp(az, minZ, maxZ);
        bx = Math.Clamp(bx, minX, maxX);
        bz = Math.Clamp(bz, minZ, maxZ);
        if (!TryPoint(ax, az, sampleHeight, isLoaded, out Vector3 a)
            || !TryPoint(bx, bz, sampleHeight, isLoaded, out Vector3 b)
            || SamePosition(a, b)
            || !isSegmentLoaded(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z))) return;
        lines[count++] = new SculptOverlayLine(a, b, part);
    }

    static bool TryPoint(float x, float z, Func<float, float, float> sampleHeight,
        Func<float, float, bool> isLoaded, out Vector3 point)
    {
        if (!isLoaded(x, z))
        {
            point = default;
            return false;
        }
        float y = sampleHeight(x, z) + Lift;
        point = new Vector3(x, y, z);
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }

    static bool SamePosition(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-12f;
}
