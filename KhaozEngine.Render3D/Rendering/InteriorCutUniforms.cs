using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D.Rendering;

internal readonly record struct InteriorCutUniforms(Vector4 Window, Vector4 Heights)
{
    internal static InteriorCutUniforms From(InteriorHeightField? field, Vector3 renderOrigin)
        => From(field, new InteriorCutFrame(renderOrigin));

    internal static InteriorCutUniforms From(InteriorHeightField? field, InteriorCutFrame frame)
    {
        if (field is null) return default;
        var (window, heights) = frame.Uniforms(field);
        return new InteriorCutUniforms(window, heights);
    }
}
