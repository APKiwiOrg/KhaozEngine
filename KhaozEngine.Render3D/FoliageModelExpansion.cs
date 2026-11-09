using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Shared multipart expansion for retained ground cover and admitted native foliage.</summary>
public static class FoliageModelExpansion
{
    public static int WriteParts(IReadOnlyList<MeshHandle> parts, in Matrix4x4 transform, float rank,
        FoliageMotionProfile motion, bool castsShadows, Span<FoliageInstance> destination)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (destination.Length < parts.Count) throw new ArgumentException("Destination must cover every model part.", nameof(destination));
        for (int part = 0; part < parts.Count; part++) destination[part] = new(parts[part], transform, rank, motion, castsShadows);
        return parts.Count;
    }
}
