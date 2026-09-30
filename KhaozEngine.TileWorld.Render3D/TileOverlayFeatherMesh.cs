using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>Samples an inward feather on the original surface triangles. No displacement or new draw pass.</summary>
internal static class TileOverlayFeatherMesh
{
    internal static void Add(TileGroundMesher.MeshAccumulator mesh, in TileGroundMesher.TileMeshContext context,
        TileOverlayBoundary boundary, int overlaySlot, in TileGroundMesher.LatticePoint a,
        in TileGroundMesher.LatticePoint b, in TileGroundMesher.LatticePoint c)
    {
        float width = MathF.Min(context.Options.OverlayFeatherWidthMetres, context.TileSize * 0.5f);
        Vector3 center = (a.Position + b.Position + c.Position) / 3;
        float radius = MathF.Max(Vector3.Distance(center, a.Position),
            MathF.Max(Vector3.Distance(center, b.Position), Vector3.Distance(center, c.Position)));
        if (Distance(boundary, context, center) > width + radius)
        {
            TileGroundMesher.AddTriangle(mesh, context, a.ToVertex(overlaySlot), b.ToVertex(overlaySlot), c.ToVertex(overlaySlot));
            return;
        }
        int divisions = 1;
        while (divisions < 64 && divisions * width < context.TileSize * 2) divisions *= 2;
        var vertices = new ModelVertex[(divisions + 1) * (divisions + 1)];
        for (int y = 0; y <= divisions; y++)
            for (int x = 0; x <= divisions - y; x++)
            {
                float wb = (float)x / divisions, wc = (float)y / divisions, wa = 1 - wb - wc;
                Vector3 position = a.Position * wa + b.Position * wb + c.Position * wc;
                float blend = Math.Clamp(Distance(boundary, context, position) / width, 0f, 1f);
                blend = blend * blend * (3 - 2 * blend);
                Vector4 underlay = (a.Weights * wa + b.Weights * wb + c.Weights * wc) * (1 - blend);
                vertices[y * (divisions + 1) + x] = new ModelVertex(position,
                    a.Normal * wa + b.Normal * wb + c.Normal * wc, underlay,
                    new Vector2(a.Slots.Sw, a.Slots.Se), new Vector4(a.Slots.Nw, a.Slots.Ne,
                        a.Jitter * wa + b.Jitter * wb + c.Jitter * wc, overlaySlot + 1));
            }
        for (int y = 0; y < divisions; y++)
            for (int x = 0; x < divisions - y; x++)
            {
                int i = y * (divisions + 1) + x, next = i + divisions + 1;
                TileGroundMesher.AddTriangle(mesh, context, vertices[i], vertices[i + 1], vertices[next]);
                if (x + y < divisions - 1)
                    TileGroundMesher.AddTriangle(mesh, context, vertices[i + 1], vertices[next + 1], vertices[next]);
            }
    }

    static float Distance(TileOverlayBoundary boundary, in TileGroundMesher.TileMeshContext c, Vector3 point) =>
        boundary.Distance(new Vector2(point.X / c.TileSize, -point.Z / c.TileSize), c.OriginX, c.OriginZ) * c.TileSize;
}
