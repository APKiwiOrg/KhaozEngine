using System;
using System.Numerics;

namespace KhaozEngine.MapEditor;

/// <summary>The sculpt brush preview, picked and sampled from the same live field as a stroke.</summary>
internal static class SculptCursor
{
    internal const int Segments = 64;
    internal const float Lift = 0.1f;

    internal static int Build(EditorToolController controller, in EditorFrameInput input,
        bool pointerInViewport, Span<Vector3> points)
    {
        if (controller.Mode != EditorToolMode.SculptTerrain || !pointerInViewport
            || controller.Field is not { } field) return 0;
        if (!EditorPicking.PickTerrain(field, input.RayOrigin, input.RayDirection,
                EditorToolController.PickDistance, out Vector3 center)) return 0;
        if (points.Length < Segments)
            throw new ArgumentException("The cursor buffer must hold every ring segment.", nameof(points));

        for (int i = 0; i < Segments; i++)
        {
            float angle = i * (2f * MathF.PI / Segments);
            float x = center.X + MathF.Cos(angle) * controller.BrushRadius;
            float z = center.Z + MathF.Sin(angle) * controller.BrushRadius;
            points[i] = new Vector3(x, field.SampleHeight(x, z) + Lift, z);
        }
        return Segments;
    }
}
