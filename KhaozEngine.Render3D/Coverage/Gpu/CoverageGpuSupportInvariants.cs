using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>Cold exact signs and F-rounded words for immutable support geometry.</summary>
internal static class CoverageGpuSupportInvariants
{
    internal static void Write(CoverageReferenceSupport support, Span<uint> words)
    {
        var f = new CoverageReferenceArithmetic();
        int cursor = 0;
        foreach (var t in support.Triangles)
            Store(f.Edge(support.Positions[t.A], support.Positions[t.B], support.Positions[t.C]), words, ref cursor);
        for (int edge = 0; edge < support.BoundaryCount; edge++)
        {
            Vector3 a = support.Positions[support.BoundarySourceVertices[edge]];
            Vector3 b = support.Positions[support.BoundarySourceVertices[(edge + 1) % support.BoundaryCount]];
            Store(f.Dot(a, b, a, b), words, ref cursor);
        }
    }

    static void Store(CoverageReferenceScalar value, Span<uint> words, ref int cursor)
    {
        words[cursor++] = unchecked((uint)value.Sign);
        words[cursor++] = BitConverter.SingleToUInt32Bits(value.Value);
    }
}
