using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The opaque material shortcut requires default maps and no tangent basis on every vertex.</summary>
internal static class FoliageMaterial
{
    internal static bool IsOpaque(bool hasMaterialMaps, float alphaCutoff, ReadOnlySpan<ModelVertex> vertices)
    {
        if (hasMaterialMaps || alphaCutoff != 0f) return false;
        foreach (ref readonly ModelVertex vertex in vertices)
            if (vertex.Tangent != Vector4.Zero) return false;
        return true;
    }
}
