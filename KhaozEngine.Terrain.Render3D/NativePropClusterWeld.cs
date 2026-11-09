using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain;

/// <summary>Stores privately owned world-welded geometry without changing the released weld arithmetic.</summary>
internal static class NativePropClusterWeld
{
    internal static void Store(GltfMesh mesh, RenderAnchor anchor)
    {
        foreach (ref var vertex in mesh.Vertices.AsSpan())
        {
            var world = vertex.Position;
            vertex.Position = new((float)((double)world.X - anchor.X), (float)((double)world.Y - anchor.Y),
                (float)((double)world.Z - anchor.Z));
        }
    }
}
