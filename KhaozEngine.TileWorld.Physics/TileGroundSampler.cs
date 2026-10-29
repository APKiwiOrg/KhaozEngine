using System;
using System.Numerics;

namespace KhaozEngine.TileWorld.Physics;

/// <summary>
/// The floor of a tile world on plane 0, read analytically from the same drawn ground triangles
/// <see cref="TileWorldColliders"/> registers, so a height or normal from here agrees with a ray cast against the
/// ground mesh. Only the drawn ground is the floor. Walk surfaces and other object tops hold bodies up as physics
/// statics and never appear here.
/// <para>Three cases, with no search and no allocation per call:</para>
/// <list type="bullet">
/// <item>A point on a drawable tile takes the height and unit up normal of the ground triangle under it.</item>
/// <item>A point outside the rectangle bounding the regions loaded at build is first moved just inside that
/// rectangle, so the floor carries on level from the world's edge.</item>
/// <item>A point on a tile with no ground (no underlay, <see cref="TileSettings.NoDraw"/>, or a region missing inside
/// the rectangle) takes the document's lattice height, <see cref="TileWorldDocument.HeightAt"/>, with a straight up
/// normal.</item>
/// </list>
/// It never answers NaN. A point infinitely far out clamps like any other, and a NaN point, or any point in a world
/// with no loaded regions, answers height 0 with a straight up normal. The sampler reads the document it was built
/// from on every call, but its rectangle was captured at build and the colliders it has to agree with were built
/// then too, so a document edited after <see cref="TileWorldColliders.Build(TileWorldDocument, TileWorldCatalogs, TileColliderOptions?)"/> needs a rebuild.
/// <para>It is read-only after construction, with no cache and no shared scratch buffer, so several threads may call
/// it at once as long as nothing edits the document meanwhile. <see cref="HeightAt"/>, <see cref="NormalAt"/>,
/// <see cref="HeightDelegate"/> and <see cref="NormalDelegate"/> are endorsed providers for a parallel contact
/// navigation bake under that condition.</para>
/// </summary>
public sealed class TileGroundSampler
{
    // The one plane this round describes.
    const int Plane = 0;

    readonly TileWorldDocument _document;
    readonly CapturedTileGround? _captured;
    readonly float _tileSize;
    readonly TileGroundBounds _bounds;

    internal TileGroundSampler(TileWorldDocument document, RegionCoord[] regions, CapturedTileGround? captured = null)
    {
        _document = document;
        _captured = captured;
        _tileSize = document.TileSize;
        _bounds = new(regions);
        HeightDelegate = HeightAt;
        NormalDelegate = NormalAt;
    }

    /// <summary><see cref="HeightAt"/> as the ground-height delegate the movement step takes, created once.</summary>
    public Func<float, float, float> HeightDelegate { get; }

    /// <summary><see cref="NormalAt"/> as the ground-normal delegate the movement step takes, created once.</summary>
    public Func<float, float, Vector3> NormalDelegate { get; }

    /// <summary>The floor's height in metres at a world point.</summary>
    /// <param name="worldX">World x in metres.</param>
    /// <param name="worldZ">World z in metres, which runs against tile z.</param>
    public float HeightAt(float worldX, float worldZ)
    {
        if (!TryLoadedTile(worldX, worldZ, out float tileX, out float tileZ)) return 0f;
        return TryTriangle(tileX, tileZ, out DrawnGroundTriangle triangle, out Vector3 weights)
            ? triangle.Height(weights)
            : _document.HeightAt(TileWorldSpace.WorldX(tileX, _tileSize), TileWorldSpace.WorldZ(tileZ, _tileSize), Plane);
    }

    /// <summary>The floor's unit normal at a world point, always pointing up: the normal of the ground triangle under
    /// it, or <see cref="Vector3.UnitY"/> where no ground is drawn.</summary>
    /// <param name="worldX">World x in metres.</param>
    /// <param name="worldZ">World z in metres, which runs against tile z.</param>
    public Vector3 NormalAt(float worldX, float worldZ)
    {
        if (!TryLoadedTile(worldX, worldZ, out float tileX, out float tileZ)) return Vector3.UnitY;
        return TryTriangle(tileX, tileZ, out DrawnGroundTriangle triangle, out _) ? triangle.Normal : Vector3.UnitY;
    }

    // The point in tile coordinates, clamped into the loaded rectangle. False, before any document read, when there
    // is no rectangle or the point is NaN, which the clamp passes through.
    internal bool TryLoadedTile(float worldX, float worldZ, out float tileX, out float tileZ) =>
        _bounds.TryMap(worldX, worldZ, _tileSize, out tileX, out tileZ);

    // The ground triangle under a tile-space point, chosen among its tile's triangles as the one whose smallest
    // barycentric weight is largest, so a point on a shared edge takes either neighbour and float error never leaves
    // it with none. False when the tile draws no ground.
    internal bool TryDrawnHeight(float worldX, float worldZ, out float height)
    {
        height = 0;
        if (!TryLoadedTile(worldX, worldZ, out float tileX, out float tileZ) ||
            !TryTriangle(tileX, tileZ, out var triangle, out var weights)) return false;
        height = triangle.Height(weights);
        return true;
    }

    bool TryTriangle(float tileX, float tileZ, out DrawnGroundTriangle triangle, out Vector3 weights)
    {
        int x = (int)MathF.Floor(tileX), z = (int)MathF.Floor(tileZ);
        Span<DrawnGroundTriangle> scratch = stackalloc DrawnGroundTriangle[TileTriangulation.MaxTriangles];
        ReadOnlySpan<DrawnGroundTriangle> triangles = _captured is { } captured ? captured.Triangles(x, z)
            : scratch[..DrawnGroundTriangle.Describe(_document, x, z, scratch)];
        int selected = DrawnGroundTriangle.Select(triangles, new(tileX - x, tileZ - z), out weights);
        triangle = selected >= 0 ? triangles[selected] : default;
        return selected >= 0;
    }
}
