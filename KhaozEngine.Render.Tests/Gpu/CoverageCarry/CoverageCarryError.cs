using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Maximum vector error per actor. Zero-reference relative errors are explicitly undefined.</summary>
internal sealed class CoverageCarryError(int actor)
{
    public int Actor { get; } = actor;
    public VectorError Position { get; } = new();
    public VectorError Normal { get; } = new();
    public VectorError Tangent { get; } = new();
    public double TangentHandednessAbsolute { get; private set; }
    public int ComparedVertices { get; private set; }

    internal void Include(ModelVertex[] expected, uint[] actual)
    {
        if (actual.Length != expected.Length * 16) throw new ArgumentException("Surface length differs.");
        for (int vertex = 0; vertex < expected.Length; vertex++)
        {
            var want = expected[vertex];
            var words = actual.AsSpan(vertex * 16, 16);
            Position.Include(want.Position, words);
            Normal.Include(want.Normal, words[3..]);
            Tangent.Include(new(want.Tangent.X, want.Tangent.Y, want.Tangent.Z), words[12..]);
            float handedness = BitConverter.UInt32BitsToSingle(words[15]);
            if (!float.IsFinite(handedness) || !float.IsFinite(want.Tangent.W))
                throw new InvalidOperationException("Nonfinite tangent handedness.");
            TangentHandednessAbsolute = Math.Max(TangentHandednessAbsolute, Math.Abs((double)handedness - want.Tangent.W));
            ComparedVertices++;
        }
    }

    internal sealed class VectorError
    {
        public double MaximumAbsolute { get; private set; }
        public double MaximumRelative { get; private set; }
        public int ZeroReferenceNonzeroErrors { get; private set; }

        internal void Include(Vector3 expected, ReadOnlySpan<uint> actual)
        {
            double squared = 0, referenceSquared = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                double want = axis == 0 ? expected.X : axis == 1 ? expected.Y : expected.Z;
                double value = BitConverter.UInt32BitsToSingle(actual[axis]);
                if (!double.IsFinite(want) || !double.IsFinite(value))
                    throw new InvalidOperationException("Nonfinite carry comparison.");
                squared += (want - value) * (want - value);
                referenceSquared += want * want;
            }
            double absolute = Math.Sqrt(squared);
            MaximumAbsolute = Math.Max(MaximumAbsolute, absolute);
            if (referenceSquared > 0) MaximumRelative = Math.Max(MaximumRelative, absolute / Math.Sqrt(referenceSquared));
            else if (absolute > 0) ZeroReferenceNonzeroErrors++;
        }
    }
}
