using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using static KhaozEngine.Render3D.CoverageIntervals;

namespace KhaozEngine.Tests.Gpu;

// A continuous domain of correlated post-sampler, post-carry positions in equipment coordinates.
// Proposal samples choose the box. They do not prove it. Every admitted actual output must fit
// outward bounds at runtime, including outputs from nonlinear blends and action layering.
internal sealed record CoverageOutputCell(CoverageCarryAnchor Anchor, Matrix4x4[] InverseBind,
    CoverageCommandPose Command, CoverageBoundedVector[] Before, CoverageBoundedVector[] After,
    bool[] Copies, CoverageOutputPlane[] Planes, string Binding, string Digest,
    double GramDefect = .0001, int Layer = 0);

internal readonly record struct CoverageOutputPlane(int Kind, int Triangle, int Other, Vector3 Axis);
internal readonly record struct CoverageOutputCheck(bool Accepted, string Cause, int Triangle, int Other,
    double Margin, long Operations, double Lower = 0, double Upper = 0);
internal sealed record CoverageOutputFrame(ModelVertex[] Before, ModelVertex[] After, CoverageCommandPose Command);

internal static class CoverageOutputCapture
{
    internal static CoverageOutputFrame Evaluate(CoverageCarryAnchor anchor, Matrix4x4[] inverseBind,
        Matrix4x4[] palette, CoverageCommandPose command)
    {
        if (palette.Length != inverseBind.Length) throw new ArgumentException("Incomplete palette.");
        var composed = palette.Select((p, i) => CoverageReferenceSkinning.Compose(p, inverseBind[i])).ToArray();
        var scratch = new CoverageReferenceScratch(anchor.Topology);
        var after = new ModelVertex[anchor.Topology.VertexCount];
        if (!CoverageCarryReference.TryCarry(anchor.Topology, composed, [command], anchor, scratch, after, out var failure))
            throw new InvalidOperationException(failure.ToString());
        return new(scratch.Before.ToArray(), after, command);
    }

    internal static CoverageOutputCell Point(CoverageCarryAnchor anchor, Matrix4x4[] inverseBind,
        CoverageOutputFrame frame, double radius)
    {
        var t = anchor.Topology;
        if (!CoverageOutputMembership.Transform(frame.Command.EquipmentWorld, .0001, out var inverse, out _))
            throw new InvalidOperationException("Frame outside the declared affine range.");
        var before = Enumerable.Repeat(default(CoverageBoundedVector), t.PositionGroupCount).ToArray();
        var after = (CoverageBoundedVector[])before.Clone();
        var seen = new bool[before.Length];
        var copies = CoverageOutputIdentity.Copies(anchor, frame.Command);
        for (int v = 0; v < t.VertexCount; v++)
        {
            int g = t.Vertices[v].PositionGroup;
            var b = inverse.Apply(frame.Before[v].Position); var a = inverse.Apply(frame.After[v].Position);
            before[g] = seen[g] ? Hull(before[g], b) : b;
            after[g] = seen[g] ? Hull(after[g], a) : a;
            seen[g] = true;
        }
        for (int g = 0; g < before.Length; g++)
        {
            before[g] = Grow(before[g], radius);
            after[g] = Grow(after[g], radius);
        }
        for (int v = 0; v < t.VertexCount; v++)
            if (copies[v]) after[t.Vertices[v].PositionGroup] = before[t.Vertices[v].PositionGroup];
        var result = new CoverageOutputCell(anchor, inverseBind.ToArray(), frame.Command, before, after, copies,
            [], "", "");
        result = result with { Binding = CoverageOutputBinding.Inputs(result) };
        return result with { Digest = CoverageOutputBinding.Domain(result) };
    }

    internal static CoverageBoundedVector Hull(CoverageBoundedVector a, CoverageBoundedVector b) => new(
        new(Math.Min(a.X.Min, b.X.Min), Math.Max(a.X.Max, b.X.Max)),
        new(Math.Min(a.Y.Min, b.Y.Min), Math.Max(a.Y.Max, b.Y.Max)),
        new(Math.Min(a.Z.Min, b.Z.Min), Math.Max(a.Z.Max, b.Z.Max)));

    internal static CoverageBoundedVector Grow(CoverageBoundedVector p, double radius)
    {
        if (!double.IsFinite(radius) || radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        var r = new CoverageScalarBounds(-radius, radius);
        return new(Add(p.X, r), Add(p.Y, r), Add(p.Z, r));
    }

    internal static CoverageOutputCell Clone(CoverageOutputCell c) => c with
    {
        Before = c.Before.ToArray(),
        After = c.After.ToArray(),
        Copies = c.Copies.ToArray(),
        Planes = c.Planes.ToArray(),
        InverseBind = c.InverseBind.ToArray()
    };
}
