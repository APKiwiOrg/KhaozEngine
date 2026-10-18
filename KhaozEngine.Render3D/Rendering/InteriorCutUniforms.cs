using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct InteriorCutUniforms(Vector4 Window, Vector4 Heights)
{
    internal static InteriorCutUniforms From(InteriorHeightField? field, Vector3 renderOrigin)
    {
        if (field is null) return default;
        InteriorWindow window = field.Window;
        return new InteriorCutUniforms(
            new Vector4((float)((double)window.Min.X - renderOrigin.X),
                (float)((double)window.Min.Y - renderOrigin.Z), 1f / window.CellSize, window.Cells),
            new Vector4((float)((double)field.BaseY - renderOrigin.Y), 1f / field.StepY, 0f, 0f));
    }
}
