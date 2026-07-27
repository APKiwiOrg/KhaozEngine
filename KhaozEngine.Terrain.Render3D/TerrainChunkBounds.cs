using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Terrain
{
    /// <summary>Axis-aligned bounding box for a chunk mesh, for frustum/distance culling by the (later) streaming
    /// layer. Built from the final vertex set, so it already includes the dropped skirt.
    /// <para>CHUNK-LOCAL in X and Z, absolute in Y, because <see cref="TerrainChunkBuilder"/> bakes chunk-local
    /// vertices and carries the placement in the draw transform / collision pose. Offset by the chunk's
    /// <see cref="TerrainChunkRegion"/> origin to get a world-space box. This changed with the chunk-local bake:
    /// before it, the box was world-space, and any caller that still treats it as world-space places every chunk
    /// at the origin.</para></summary>
    public readonly struct TerrainChunkBounds
    {
        public Vector3 Min { get; }
        public Vector3 Max { get; }
        public Vector3 Center => (Min + Max) * 0.5f;
        public Vector3 Size => Max - Min;

        public TerrainChunkBounds(Vector3 min, Vector3 max) { Min = min; Max = max; }

        public static TerrainChunkBounds FromPositions(ReadOnlySpan<ModelVertex> verts)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in verts)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
            }
            return new TerrainChunkBounds(min, max);
        }

        public bool Contains(Vector3 p) =>
            p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
    }
}
