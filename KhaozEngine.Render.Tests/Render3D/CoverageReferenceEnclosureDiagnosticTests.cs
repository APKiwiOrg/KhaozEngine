using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;
using M = KhaozEngine.Render3D.CoverageEnclosureMath;
using F = KhaozEngine.Render3D.CoverageFloatIntervals;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Continuation evidence only. This diagnostic is not a replacement for the failing fixture gate.</summary>
public sealed class CoverageReferenceEnclosureDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void Trace_the_first_unbounded_normal_basis_without_changing_the_enclosure()
    {
        var c = CoverageReferenceCases.Get("cost-limit-l2-lifted-w05");
        var reference = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands,
            reference, final, out var failure), failure.ToString());
        var palette = new Matrix4x4[c.Palette.Length];
        for (int i = 0; i < palette.Length; i++) palette[i] = SkinningMath.Compose(c.Palette[i], c.InverseBind[i]);
        var scratch = new CoverageReferenceEnclosureScratch(c.Topology);
        scratch.Reset(c.Commands.Length);
        Assert.True(CoverageEnclosurePose.Prepare(c.Topology, palette, scratch));
        Assert.True(CoverageEnclosurePose.Conform(c.Topology, c.Commands, reference, scratch));
        Assert.True(CoverageEnclosureCorrection.Apply(c.Topology, c.Commands, reference, scratch));
        const int face = 360;
        int a = (int)c.Topology.Indices[face * 3], b = (int)c.Topology.Indices[face * 3 + 1], d = (int)c.Topology.Indices[face * 3 + 2];
        foreach (int vertex in new[] { a, b, d })
        {
            output.WriteLine($"VERTEX {vertex} before={scratch.Before[vertex]} after={scratch.After[vertex]} "
                + $"referenceBefore={reference.Before[vertex].Position} referenceAfter={reference.After[vertex].Position} "
                + $"referenceMoved={reference.Before[vertex].Position != reference.After[vertex].Position}");
        }
        var u = M.Subtract(scratch.Before[b], scratch.Before[a]);
        var v = M.Subtract(scratch.Before[d], scratch.Before[a]);
        var s = M.Subtract(scratch.After[b], scratch.After[a]);
        var t = M.Subtract(scratch.After[d], scratch.After[a]);
        var normal = M.Cross(u, v); var afterNormal = M.Cross(s, t);
        var beforeLength = M.Length(normal); var afterLength = M.Length(afterNormal);
        var n = M.Divide(normal, beforeLength); var m = M.Divide(afterNormal, afterLength);
        output.WriteLine($"NORMAL before={normal} after={afterNormal}");
        output.WriteLine($"LENGTH before={beforeLength} after={afterLength} beforeUnit={n} afterUnit={m}");
        var beforeDet = M.Dot(u, M.Cross(v, n)); var afterDet = M.Dot(s, M.Cross(t, m));
        var beforeScale = M.Root(F.Multiply(M.Dot(u, u), M.Dot(v, v)));
        var afterScale = M.Root(F.Multiply(M.Dot(s, s), M.Dot(t, t)));
        output.WriteLine($"BASIS alignment={M.Dot(normal, afterNormal)} beforeDet={beforeDet} afterDet={afterDet} "
            + $"beforeScale={beforeScale} afterScale={afterScale} "
            + $"beforeThreshold={F.Multiply(beforeScale, F.Point(1e-12f))} afterThreshold={F.Multiply(afterScale, F.Point(1e-12f))}");
        output.WriteLine($"RECIPROCALS before={F.Divide(F.Point(1), beforeDet)} after={F.Divide(F.Point(1), afterDet)}");
        output.WriteLine($"FINAL_BASIS bu={M.Divide(M.Cross(v, n), beforeDet)} au={M.Divide(M.Cross(t, m), afterDet)}");
        Assert.False(CoverageEnclosureNormals.Evaluate(c.Topology, c.Commands, reference, scratch));
        Assert.Equal(CoverageEnclosureSite.Normals, scratch.FirstUnboundedSite);
        Assert.Equal(face, scratch.FirstUnboundedIndex);
    }
}
