using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

// The vertex half of the tile triangulation. Which triangles a tile is cut into is TileTriangulation's business,
// shared with the raycast so a click lands on the triangle that is drawn. What each of their lattice points
// becomes, its position, normal, slots, weights and jitter, is this file's. Both parts of a cut tile are built
// from the same points, so the cut edge carries one position and one normal on each side and the parts meet
// without a crack.
public static partial class TileGroundMesher
{
    /// <summary>Emits one tile the way the shared triangulation cuts it, painting each triangle with the
    /// overlay's slot or leaving it to the tile's own four corner materials.</summary>
    static void AddCutTile(
        MeshAccumulator mesh,
        in TileMeshContext c,
        int lx,
        int lz,
        TileOverlayShape shape,
        int rotation,
        int? overlaySlot,
        bool splitSwNe)
    {
        Span<TileLatticeTriangle> triangles = stackalloc TileLatticeTriangle[TileTriangulation.MaxTriangles];
        int count = TileTriangulation.Triangulate(shape, rotation, splitSwNe, triangles);

        // A full overlay paints every triangle, so the tile's own corner materials are never read there and the
        // four walks that find them are skipped. A cut tile always needs them, because it always keeps some
        // ground.
        TileCornerSlots slots = shape == TileOverlayShape.Full && overlaySlot.HasValue
            ? TileCornerSlots.Uniform(overlaySlot.Value)
            : TileSlots(c, lx, lz);

        LatticePoint sw = Corner(c, lx, lz, 0, 0, slots);
        LatticePoint se = Corner(c, lx, lz, 1, 0, slots);
        LatticePoint nw = Corner(c, lx, lz, 0, 1, slots);
        LatticePoint ne = Corner(c, lx, lz, 1, 1, slots);

        for (int i = 0; i < count; i++)
        {
            TileLatticeTriangle t = triangles[i];
            int? paint = t.Overlay ? overlaySlot : null;
            AddTriangle(
                mesh,
                c,
                At(t.A, sw, se, nw, ne).ToVertex(paint),
                At(t.B, sw, se, nw, ne).ToVertex(paint),
                At(t.C, sw, se, nw, ne).ToVertex(paint));
        }
    }

    /// <summary>The vertex data at one lattice point of the tile: a corner as it stands, a mid-edge point as the
    /// average of the two corners it lies between.</summary>
    static LatticePoint At(TileLatticePoint point, in LatticePoint sw, in LatticePoint se, in LatticePoint nw, in LatticePoint ne)
    {
        TileTriangulation.Ends(point, out TileLatticePoint first, out TileLatticePoint second);
        LatticePoint a = Pick(first, sw, se, nw, ne);
        // A corner is its own pair, and taking it as it stands rather than averaging it with itself keeps every
        // copy of that corner bit-identical across the tiles and regions that share it.
        return first == second ? a : Midpoint(a, Pick(second, sw, se, nw, ne));
    }

    static LatticePoint Pick(TileLatticePoint corner, in LatticePoint sw, in LatticePoint se, in LatticePoint nw, in LatticePoint ne) =>
        corner switch
        {
            TileLatticePoint.Se => se,
            TileLatticePoint.Nw => nw,
            TileLatticePoint.Ne => ne,
            _ => sw,
        };

    /// <summary>One corner of the tile at region-local (lx, lz), offset by a 0 or 1 corner step on each axis, over
    /// the tile's four <paramref name="slots"/>. Its weights are one-hot on its own corner and its jitter is the
    /// corner's own, both of which every tile touching that corner computes identically.</summary>
    static LatticePoint Corner(in TileMeshContext c, int lx, int lz, int dx, int dz, in TileCornerSlots slots)
        => CellCorner(c, lx, lz, dx, dz, dx, dz, slots);

    static void AddCoarseCell(
        MeshAccumulator mesh, in TileMeshContext c, int lx, int lz, int size, Coarse4Transition transitions)
    {
        if (transitions != Coarse4Transition.None)
        {
            AddCoarseTransitionCell(mesh, c, lx, lz, size, transitions);
            return;
        }

        TileCornerSlots slots = CoarseCellSlots(c, lx, lz, size);
        LatticePoint sw = CellCorner(c, lx, lz, 0, 0, 0, 0, slots);
        LatticePoint se = CellCorner(c, lx, lz, size, 0, 1, 0, slots);
        LatticePoint nw = CellCorner(c, lx, lz, 0, size, 0, 1, slots);
        LatticePoint ne = CellCorner(c, lx, lz, size, size, 1, 1, slots);
        short h00 = c.Doc.CornerHeightCm(c.OriginX + lx, c.OriginZ + lz, c.Plane);
        short h10 = c.Doc.CornerHeightCm(c.OriginX + lx + size, c.OriginZ + lz, c.Plane);
        short h01 = c.Doc.CornerHeightCm(c.OriginX + lx, c.OriginZ + lz + size, c.Plane);
        short h11 = c.Doc.CornerHeightCm(c.OriginX + lx + size, c.OriginZ + lz + size, c.Plane);
        if (TileTriangulation.SplitSwNe(h00, h10, h01, h11, TileOverlayShape.Full, 0))
        {
            AddTriangle(mesh, c, sw.ToVertex(null), se.ToVertex(null), ne.ToVertex(null));
            AddTriangle(mesh, c, sw.ToVertex(null), ne.ToVertex(null), nw.ToVertex(null));
        }
        else
        {
            AddTriangle(mesh, c, sw.ToVertex(null), se.ToVertex(null), nw.ToVertex(null));
            AddTriangle(mesh, c, se.ToVertex(null), ne.ToVertex(null), nw.ToVertex(null));
        }
    }

    static void AddCoarseTransitionCell(
        MeshAccumulator mesh, in TileMeshContext c, int lx, int lz, int size, Coarse4Transition transitions)
    {
        var perimeter = new List<LatticePoint>(16)
        {
            CanonicalPoint(c, lx, lz, 0, 0),
        };
        AddEdgePoints(perimeter, c, lx, lz, size, transitions, Coarse4Transition.South);
        perimeter.Add(CanonicalPoint(c, lx, lz, size, 0));
        AddEdgePoints(perimeter, c, lx, lz, size, transitions, Coarse4Transition.East);
        perimeter.Add(CanonicalPoint(c, lx, lz, size, size));
        AddEdgePoints(perimeter, c, lx, lz, size, transitions, Coarse4Transition.North);
        perimeter.Add(CanonicalPoint(c, lx, lz, 0, size));
        AddEdgePoints(perimeter, c, lx, lz, size, transitions, Coarse4Transition.West);

        LatticePoint centre = CanonicalPoint(c, lx, lz, size / 2, size / 2);
        for (int i = 0; i < perimeter.Count; i++)
            AddTriangle(mesh, c, centre.ToVertex(null), perimeter[i].ToVertex(null),
                perimeter[(i + 1) % perimeter.Count].ToVertex(null));
    }

    static void AddEdgePoints(
        List<LatticePoint> points, in TileMeshContext c, int lx, int lz, int size,
        Coarse4Transition transitions, Coarse4Transition edge)
    {
        if ((transitions & edge) == 0) return;
        for (int step = 1; step < size; step++)
        {
            (int dx, int dz) = edge switch
            {
                Coarse4Transition.South => (step, 0),
                Coarse4Transition.East => (size, step),
                Coarse4Transition.North => (size - step, size),
                _ => (0, size - step),
            };
            points.Add(CanonicalPoint(c, lx, lz, dx, dz));
        }
    }

    static TileCornerSlots CoarseCellSlots(in TileMeshContext c, int lx, int lz, int size) =>
        new(
            SlotAt(c, c.OriginX + lx, c.OriginZ + lz),
            SlotAt(c, c.OriginX + lx + size, c.OriginZ + lz),
            SlotAt(c, c.OriginX + lx, c.OriginZ + lz + size),
            SlotAt(c, c.OriginX + lx + size, c.OriginZ + lz + size));

    static LatticePoint CanonicalPoint(in TileMeshContext c, int lx, int lz, int dx, int dz)
    {
        int cx = c.OriginX + lx + dx;
        int cz = c.OriginZ + lz + dz;
        TileCornerSlots slots = TileCornerSlots.Uniform(SlotAt(c, cx, cz));
        Vector3 position = TileWorldSpace.ToWorld(
            lx + dx,
            c.Doc.CornerHeightCm(cx, cz, c.Plane) * 0.01f,
            lz + dz,
            c.TileSize);
        Vector3 normal = c.Options.SmoothNormals ? CornerNormal(c.Doc, cx, cz, c.Plane) : Vector3.UnitY;
        return new LatticePoint(
            position,
            normal,
            slots,
            OverlayWeights,
            CornerJitter(c.Doc, cx, cz, c.Plane, c.Options.JitterAmplitude));
    }

    static LatticePoint CellCorner(
        in TileMeshContext c, int lx, int lz, int dx, int dz, int weightX, int weightZ,
        in TileCornerSlots slots)
    {
        int cx = c.OriginX + lx + dx;
        int cz = c.OriginZ + lz + dz;
        Vector3 position = TileWorldSpace.ToWorld(
            lx + dx,
            c.Doc.CornerHeightCm(cx, cz, c.Plane) * 0.01f,
            lz + dz,
            c.TileSize);
        Vector3 normal = c.Options.SmoothNormals ? CornerNormal(c.Doc, cx, cz, c.Plane) : Vector3.UnitY;
        return new LatticePoint(
            position,
            normal,
            slots,
            CornerWeights(weightZ * 2 + weightX),
            CornerJitter(c.Doc, cx, cz, c.Plane, c.Options.JitterAmplitude));
    }

    /// <summary>The mid-edge point between two corners: position, weights and jitter averaged, the normal
    /// averaged then renormalised so a cut edge lights as the whole tile's surface does there, and the tile's
    /// slots carried through unchanged (both ends already hold the same four). Averaging two one-hot corners is
    /// what puts 0.5 on each of the two materials the edge runs between.</summary>
    static LatticePoint Midpoint(in LatticePoint a, in LatticePoint b)
    {
        Vector3 normal = (a.Normal + b.Normal) * 0.5f;
        return new LatticePoint(
            (a.Position + b.Position) * 0.5f,
            normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : Vector3.UnitY,
            a.Slots,
            (a.Weights + b.Weights) * 0.5f,
            (a.Jitter + b.Jitter) * 0.5f);
    }

    /// <summary>What one lattice point of a tile carries: one of its corners, or a mid-edge point averaged from
    /// two of them.</summary>
    internal readonly struct LatticePoint
    {
        internal LatticePoint(Vector3 position, Vector3 normal, TileCornerSlots slots, Vector4 weights, float jitter)
        {
            Position = position;
            Normal = normal;
            Slots = slots;
            Weights = weights;
            Jitter = jitter;
        }

        /// <summary>Region-local position (z negative, see <see cref="TileWorldSpace"/>), absolute Y in metres.</summary>
        public Vector3 Position { get; }
        /// <summary>The lattice normal here.</summary>
        public Vector3 Normal { get; }
        /// <summary>The tile's four corner material slots, identical on every point of the tile.</summary>
        public TileCornerSlots Slots { get; }
        /// <summary>This point's weights over those four slots: one-hot at a corner, 0.5 and 0.5 at a mid-edge
        /// point, a quarter each at the tile centre.</summary>
        public Vector4 Weights { get; }
        /// <summary>The brightness multiplier here, which the shader applies to the blended albedo.</summary>
        public float Jitter { get; }

        /// <summary>This point as a vertex for the tile-ground pipeline, painted with
        /// <paramref name="overlaySlot"/> when the triangle using it is an overlay one: colour carries the four
        /// weights, <c>Uv</c> the first two slots, <c>Tangent</c> the other two then the jitter then 0. An
        /// overlay point puts the overlay's material in all four slots and all of its weight on the first, so the
        /// painted triangle reads one material flat however the tile underneath it blends.</summary>
        public ModelVertex ToVertex(int? overlaySlot)
        {
            TileCornerSlots slots = overlaySlot is int slot ? TileCornerSlots.Uniform(slot) : Slots;
            return new ModelVertex(
                Position,
                Normal,
                overlaySlot.HasValue ? OverlayWeights : Weights,
                new Vector2(slots.Sw, slots.Se),
                new Vector4(slots.Nw, slots.Ne, Jitter, 0f));
        }
    }
}
