using System;
using System.Collections.Generic;
using System.Numerics;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.TileWorld.Physics;

internal sealed record TileWaterPrismFace(int Prism, int Plane, bool Wet, bool Dry, TileWaterConvexShape Shape);

/// <summary>Boundary patches of triangle unions. Shared lattice edges never become artificial dry walls.</summary>
internal static class TileWaterPrismFaces
{
    readonly record struct Grid(long X, long Z) : IComparable<Grid>
    {
        public int CompareTo(Grid other) => X != other.X ? X.CompareTo(other.X) : Z.CompareTo(other.Z);
    }
    readonly record struct Edge(Grid A, Grid B);
    readonly record struct Owner(int Prism, int Plane, float Start, float End);

    internal static TileWaterPrismFace[] Build(TileWaterPrism[] prisms)
    {
        var edges = new Dictionary<Edge, List<Owner>>();
        for (int i = 0; i < prisms.Length; i++)
        {
            TileWaterPrism prism = prisms[i];
            TileLatticePoint[] vertices = [prism.Triangle.PlanA, prism.Triangle.PlanB, prism.Triangle.PlanC];
            for (int edge = 0; edge < 3; edge++)
            {
                Grid a = At(prism, vertices[edge]), b = At(prism, vertices[(edge + 1) % 3]);
                bool split = a.X == b.X && Math.Abs(a.Z - b.Z) == 2 || a.Z == b.Z && Math.Abs(a.X - b.X) == 2;
                if (split)
                {
                    Grid middle = new((a.X + b.X) / 2, (a.Z + b.Z) / 2);
                    Add(a, middle, new(i, edge, 0, 0.5f));
                    Add(middle, b, new(i, edge, 0.5f, 1));
                }
                else Add(a, b, new(i, edge, 0, 1));
            }
        }
        var output = new List<TileWaterPrismFace>();
        for (int i = 0; i < prisms.Length; i++)
        {
            output.Add(new(i, 3, true, true, Face(prisms[i], 3, 0, 1, float.NegativeInfinity)));
            output.Add(new(i, 4, true, true, Face(prisms[i], 4, 0, 1, float.NegativeInfinity)));
        }
        foreach (var row in edges.Values)
            foreach (Owner owner in row)
            {
                TileWaterPrism prism = prisms[owner.Prism];
                float wetLow = float.NegativeInfinity, dryLow = float.NegativeInfinity;
                foreach (Owner other in row)
                {
                    if (other.Prism == owner.Prism) continue;
                    TileWaterPrism neighbor = prisms[other.Prism];
                    dryLow = Math.Max(dryLow, neighbor.Column.Maximum.Y);
                    if (neighbor.Column.Domain == prism.Column.Domain) wetLow = Math.Max(wetLow, neighbor.Column.Maximum.Y);
                }
                bool wet = wetLow < prism.Column.Maximum.Y, dry = dryLow < prism.Column.Maximum.Y;
                if (wet && dry && wetLow == dryLow)
                    output.Add(new(owner.Prism, owner.Plane, true, true, Face(prism, owner.Plane, owner.Start, owner.End, wetLow)));
                else
                {
                    if (wet) output.Add(new(owner.Prism, owner.Plane, true, false, Face(prism, owner.Plane, owner.Start, owner.End, wetLow)));
                    if (dry) output.Add(new(owner.Prism, owner.Plane, false, true, Face(prism, owner.Plane, owner.Start, owner.End, dryLow)));
                }
            }
        return output.ToArray();

        void Add(Grid a, Grid b, Owner owner)
        {
            Edge key = a.CompareTo(b) < 0 ? new(a, b) : new(b, a);
            if (!edges.TryGetValue(key, out var list)) edges.Add(key, list = []);
            list.Add(owner);
        }
    }

    static Grid At(TileWaterPrism prism, TileLatticePoint point)
    {
        Vector2 local = TileTriangulation.Local(point);
        return new(2L * prism.TileX + (long)(2 * local.X), 2L * prism.TileZ + (long)(2 * local.Y));
    }

    static TileWaterConvexShape Face(TileWaterPrism prism, int face, float start, float end, float lower)
    {
        var planes = new List<P>(prism.Planes) { prism.Planes[face].Opposite };
        if (face < 3 && (start != 0 || end != 1))
        {
            (Vector3 a, Vector3 b) = face switch { 0 => (prism.Triangle.A, prism.Triangle.B),
                1 => (prism.Triangle.B, prism.Triangle.C), _ => (prism.Triangle.C, prism.Triangle.A) };
            V direction = V.Point((double)b.X - a.X, 0, (double)b.Z - a.Z);
            I origin = V.Dot(direction, V.Point(a.X, 0, a.Z)), length = direction.SquaredLength;
            planes.Add(new(direction, origin + length * I.Point(end)));
            planes.Add(new(default(V) - direction, I.Point(0) - origin - length * I.Point(start)));
        }
        if (float.IsFinite(lower)) planes.Add(P.Point(0, -1, 0, -lower));
        return new(planes.ToArray());
    }
}
