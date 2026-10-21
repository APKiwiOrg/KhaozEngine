using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Endpoint evaluation of each monotone, inclusive-flush RN32 operation encloses the actual discrete graph.
// Products take all four endpoints. This retains source-skin-then-refine and canonical edge ordering.
internal readonly record struct CoverageCarryFloat(float Min, float Max)
{
    [ThreadStatic] static CoverageReferenceArithmetic? arithmetic;
    static CoverageReferenceArithmetic Arithmetic => arithmetic ??= new();
    internal static CoverageCarryFloat Point(float x) => new(Arithmetic.Read(x), Arithmetic.Read(x));
    internal static CoverageCarryFloat Around(float x, float radius)
    {
        double low = (double)x - radius, high = (double)x + radius;
        float a = (float)low, b = (float)high;
        if (a < low) a = MathF.BitIncrement(a);
        if (b > high) b = MathF.BitDecrement(b);
        return new(Arithmetic.Read(a), Arithmetic.Read(b));
    }
    internal CoverageScalarBounds Real => new(Min, Max);
    public static CoverageCarryFloat operator +(CoverageCarryFloat a, CoverageCarryFloat b) =>
        new(Arithmetic.Add(a.Min, b.Min), Arithmetic.Add(a.Max, b.Max));
    public static CoverageCarryFloat operator -(CoverageCarryFloat a, CoverageCarryFloat b) =>
        new(Arithmetic.Sub(a.Min, b.Max), Arithmetic.Sub(a.Max, b.Min));
    public static CoverageCarryFloat operator *(CoverageCarryFloat a, CoverageCarryFloat b)
    {
        float p = Arithmetic.Mul(a.Min, b.Min), q = Arithmetic.Mul(a.Min, b.Max),
            r = Arithmetic.Mul(a.Max, b.Min), s = Arithmetic.Mul(a.Max, b.Max);
        return new(MathF.Min(MathF.Min(p, q), MathF.Min(r, s)), MathF.Max(MathF.Max(p, q), MathF.Max(r, s)));
    }
    internal CoverageCarryFloat Divide(float positive) => new(Arithmetic.Div(Min, positive), Arithmetic.Div(Max, positive));
}

internal readonly record struct CoverageCarryBox(CoverageCarryFloat X, CoverageCarryFloat Y, CoverageCarryFloat Z)
{
    internal CoverageBoundedVector Real => new(X.Real, Y.Real, Z.Real);
    internal static CoverageCarryBox Point(Vector3 p) => new(CoverageCarryFloat.Point(p.X),
        CoverageCarryFloat.Point(p.Y), CoverageCarryFloat.Point(p.Z));
    public static CoverageCarryBox operator +(CoverageCarryBox a, CoverageCarryBox b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static CoverageCarryBox operator -(CoverageCarryBox a, CoverageCarryBox b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    internal CoverageCarryBox Times(float w)
    {
        var b = CoverageCarryFloat.Point(w);
        return new(X * b, Y * b, Z * b);
    }
    internal CoverageCarryBox Divide(float w) => new(X.Divide(w), Y.Divide(w), Z.Divide(w));
}

internal sealed class CoverageCarryEnclosure
{
    internal CoverageCarryBox[] Before { get; }
    internal CoverageCarryBox[] After { get; }
    internal CoverageCarryBox[] Displacement { get; }
    internal long Operations { get; private set; }

    internal CoverageCarryEnclosure(CoverageCarryCell cell)
    {
        var t = cell.Anchor.Topology;
        var source = new CoverageCarryBox[t.SourceVertexCount];
        for (int v = 0; v < source.Length; v++)
        {
            var s = t.Source.SkinVertices[v];
            float[] weights = [s.BoneWeights.X, s.BoneWeights.Y, s.BoneWeights.Z, s.BoneWeights.W];
            int[] bones = [(int)s.BoneIndices.X, (int)s.BoneIndices.Y, (int)s.BoneIndices.Z, (int)s.BoneIndices.W];
            var m = new CoverageCarryFloat[12];
            for (int j = 0; j < m.Length; j++)
            {
                var sum = CoverageCarryFloat.Point(0);
                for (int k = 0; k < 4; k++)
                {
                    var matrix = cell.Center[bones[k]];
                    float coefficient = Coefficients(matrix)[j];
                    var value = j >= 9 ? CoverageCarryFloat.Around(coefficient, cell.Radius) : CoverageCarryFloat.Point(coefficient);
                    var term = value * CoverageCarryFloat.Point(weights[k]);
                    sum = k == 0 ? term : sum + term;
                }
                m[j] = sum;
                Operations += 7;
            }
            CoverageCarryFloat Component(int j) => ((CoverageCarryFloat.Point(s.Position.X) * m[j]
                + CoverageCarryFloat.Point(s.Position.Y) * m[j + 3]) + CoverageCarryFloat.Point(s.Position.Z) * m[j + 6]) + m[j + 9];
            source[v] = new(Component(0), Component(1), Component(2));
            Operations += 18;
        }
        var data = new CoverageReferenceTopology(t);
        var groups = new CoverageCarryBox[t.PositionGroupCount];
        for (int g = 0; g < groups.Length; g++)
        {
            var r = data.Recipes[g];
            if (r.WeightA == 12) groups[g] = source[r.A];
            else if (r.WeightB == 12) groups[g] = source[r.B];
            else if (r.WeightC == 12) groups[g] = source[r.C];
            else if (r.WeightA != 0 && r.WeightB != 0 && r.WeightC != 0)
            {
                groups[g] = (source[r.A].Times(r.WeightA) + source[r.B].Times(r.WeightB) + source[r.C].Times(r.WeightC)).Divide(12);
                Operations += 18;
            }
            else
            {
                int a = r.WeightC == 0 || r.WeightB == 0 ? r.A : r.B;
                int b = r.WeightC == 0 ? r.B : r.C;
                if (a > b) (a, b) = (b, a);
                float w = data.EdgeParameters[g];
                var f = new CoverageReferenceArithmetic();
                groups[g] = source[a].Times(f.Sub(1, w)) + source[b].Times(w);
                Operations += 10;
            }
        }
        Before = new CoverageCarryBox[t.VertexCount]; After = new CoverageCarryBox[t.VertexCount];
        Displacement = new CoverageCarryBox[t.VertexCount];
        var scratch = new CoverageReferenceScratch(t);
        if (!CoverageReferenceFrames.TryPrepare(t, Matrix4x4.Identity, cell.Commands, scratch, out var failure))
            throw new InvalidOperationException(failure.ToString());
        for (int v = 0; v < t.VertexCount; v++)
        {
            var r = t.Vertices[v];
            Before[v] = r.SourceVertex >= 0 ? source[r.SourceVertex] : groups[r.PositionGroup];
            After[v] = Before[v];
            int representative = cell.Anchor.Representatives[r.PositionGroup];
            float full = cell.Anchor.Recession[r.PositionGroup];
            if (representative < 0 || full == 0) continue;
            int region = t.Vertices[representative].RegionIndex;
            var frame = scratch.Frames[scratch.RegionCommands[region]];
            float weight = cell.Commands[frame.Command].TransitionWeight;
            if (weight == 0) continue;
            var f = new CoverageReferenceArithmetic();
            float applied = weight == 1 ? full : f.Mul(weight, full);
            var d = new Vector3(f.Mul(applied, frame.Recess.X), f.Mul(applied, frame.Recess.Y), f.Mul(applied, frame.Recess.Z));
            var origin = t.Vertices[representative];
            var p = origin.SourceVertex >= 0 ? source[origin.SourceVertex] : groups[origin.PositionGroup];
            After[v] = p - CoverageCarryBox.Point(d);
            // Use an independent real subtraction. Dependency loss here is conservative for world recession.
            var difference = After[v].Real - Before[v].Real;
            Displacement[v] = FromReal(difference);
            Operations += 6;
        }
    }

    internal static float[] Coefficients(Matrix4x4 m) => [m.M11, m.M12, m.M13, m.M21, m.M22, m.M23,
        m.M31, m.M32, m.M33, m.M41, m.M42, m.M43];

    static CoverageCarryBox FromReal(CoverageBoundedVector v)
    {
        static CoverageCarryFloat Out(CoverageScalarBounds b) => new(MathF.BitDecrement((float)b.Min), MathF.BitIncrement((float)b.Max));
        return new(Out(v.X), Out(v.Y), Out(v.Z));
    }
}
