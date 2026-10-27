using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

public sealed record NativePropInstance(string Id, long? NumericId, string AssetId, Matrix4x4 LocalTransform, RenderAnchor Anchor);
public sealed record NativePropLod
{
    public int Index { get; }
    public IReadOnlyList<MeshHandle> Parts { get; }
    public NativePropLod(int index, IReadOnlyList<MeshHandle> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (index < 0 || parts.Count == 0) throw new ArgumentException("A native tier requires an index and every part.");
        Index = index; Parts = Array.AsReadOnly(parts.ToArray());
    }
}
