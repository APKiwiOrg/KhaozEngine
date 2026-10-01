using System.Numerics;

namespace KhaozEngine.TileWorld;

/// <summary>The full-detail ground triangles of one region-plane as <see cref="TileGroundTriangles.Build"/> lays them
/// out: three fresh positions per triangle, in region-local space, and indices that run 0, 1, 2 and on in step with
/// them. A region-plane with no drawable tile has both arrays empty.</summary>
public sealed class TileGroundMesh
{
    internal TileGroundMesh(RegionCoord region, int plane, Vector3[] positions, int[] indices)
    {
        Region = region;
        Plane = plane;
        Positions = positions;
        Indices = indices;
    }

    /// <summary>The region the triangles cover.</summary>
    public RegionCoord Region { get; }

    /// <summary>The plane the triangles cover.</summary>
    public int Plane { get; }

    /// <summary>Region-local positions, x from 0 to 64 tiles and z from 0 to minus 64 tiles (see
    /// <see cref="TileWorldSpace"/>), with absolute heights in metres. Never shared between triangles. Callers must
    /// not mutate the array.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Three per triangle, each triangle counter-clockwise on tile x and z. Callers must not mutate the
    /// array.</summary>
    public int[] Indices { get; }
}
