using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageOutputMutationData
{
    internal (CoverageOutputCell Cell, CoverageOutputFrame Frame, Matrix4x4[] Palette) Positive { get; } = CoverageOutputDomainTests.Positive();
}

public sealed class CoverageOutputMutationTests(CoverageOutputMutationData data) : IClassFixture<CoverageOutputMutationData>
{
    [Theory]
    [InlineData("widened digest")]
    [InlineData("rehashed widened bounds")]
    [InlineData("missing witnesses")]
    [InlineData("wrong scalar")]
    [InlineData("wrong influence")]
    [InlineData("wrong support")]
    [InlineData("wrong hash")]
    [InlineData("nonfinite bound")]
    [InlineData("false copy")]
    public void Certificate_mutations_are_refused(string name)
    {
        var original = data.Positive.Cell;
        var c = CoverageOutputCapture.Clone(original);
        if (name.Contains("widened"))
        {
            for (int g = 0; g < c.Before.Length; g++)
            { c.Before[g] = CoverageOutputCapture.Grow(c.Before[g], .5); c.After[g] = CoverageOutputCapture.Grow(c.After[g], .5); }
        }
        if (name == "missing witnesses") c = c with { Planes = [] };
        if (name == "wrong scalar")
        {
            var scalars = c.Anchor.Recession.ToArray(); scalars[Array.FindIndex(scalars, s => s > 0)] += .01f;
            c = c with { Anchor = new(c.Anchor, scalars) };
        }
        if (name == "wrong influence")
        {
            var t = c.Anchor.Topology; var s = t.Source; var skin = s.SkinVertices.ToArray();
            skin[0].BoneWeights = new(.999f, .001f, 0, 0);
            var source = new CoverageSourceTopology(s.Identity, s.SourceSha256, s.Positions, s.Vertices, s.Triangles,
                skin, occurrences: s.Occurrences, materials: s.Materials);
            var changed = CoverageRefinement.Build(source, t.Map);
            var command = c.Command with { Topology = changed };
            c = c with { Anchor = new(changed, data.Positive.Palette, [command]), Command = command };
        }
        if (name == "wrong support")
        {
            var fixture = CoverageFixture.CurvedPanel();
            var points = fixture.RigidPositions.Select(p => p + new Vector3(0, 0, .1f)).ToArray();
            var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("altered-synthetic-support", points, fixture.RigidIndices),
                [new(0, 0, 0, fixture.SupportIndices.Length / 3)], Matrix4x4.Identity);
            c = c with { Command = c.Command with { Support = support } };
        }
        if (name == "wrong hash") c = c with { Binding = new string('0', 64) };
        if (name == "nonfinite bound") c.Before[0] = c.Before[0] with { X = new(double.NaN, double.NaN) };
        if (name == "false copy") c.Copies[Array.FindIndex(c.Copies, copy => !copy)] = true;
        if (name != "widened digest" && name != "nonfinite bound") c = c with { Digest = CoverageOutputBinding.Domain(c) };
        var verdict = CoverageOutputChecker.Check(c, original.Binding);
        Assert.False(verdict.Accepted, name);
        if (name == "rehashed widened bounds") Assert.NotEqual("domain digest", verdict.Cause);
        if (name == "false copy") Assert.Equal("input/copy binding", verdict.Cause);
    }

    [Theory]
    [InlineData("changed transform")]
    [InlineData("unsupported layer")]
    [InlineData("nonfinite frame")]
    [InlineData("changed protected output")]
    public void Runtime_mutations_do_not_enter_a_checked_cell(string name)
    {
        var (c, frame, palette) = data.Positive;
        var certified = CoverageOutputChecker.Certify(c, c.Binding)!;
        Assert.True(certified.Admits(frame, out _));
        var mutation = frame;
        if (name == "changed transform")
        {
            var m = Matrix4x4.CreateScale(2);
            var command = c.Command with { EquipmentWorld = m, CoverageWorld = m };
            mutation = CoverageOutputCapture.Evaluate(c.Anchor, c.InverseBind, palette.Select(p => p * m).ToArray(), command);
        }
        if (name == "nonfinite frame")
        {
            var m = c.Command.EquipmentWorld; m.M11 = float.NaN;
            mutation = frame with { Command = c.Command with { EquipmentWorld = m, CoverageWorld = m } };
        }
        if (name == "changed protected output")
        {
            var after = frame.After.ToArray(); int v = Array.FindIndex(c.Copies, copy => copy);
            after[v].Position.X = MathF.BitIncrement(after[v].Position.X);
            mutation = frame with { After = after };
        }
        Assert.False(certified.Admits(mutation, out _, name == "unsupported layer" ? 1 : 0));
    }

    [Fact]
    public void An_actual_pose_one_outward_neighbour_beyond_a_domain_is_refused()
    {
        var (original, frame, _) = data.Positive;
        var c = CoverageOutputCapture.Clone(original);
        Assert.True(CoverageOutputMembership.Transform(frame.Command.EquipmentWorld, c.GramDefect, out var inverse, out _));
        int v = Array.FindIndex(c.Copies, copy => !copy), g = c.Anchor.Topology.Vertices[v].PositionGroup;
        var exactEnclosure = inverse.Apply(frame.After[v].Position);
        c.After[g] = c.After[g] with { X = new(c.After[g].X.Min, Math.BitDecrement(exactEnclosure.X.Min)) };
        c = c with { Digest = CoverageOutputBinding.Domain(c) };
        Assert.True(CoverageOutputChecker.Check(c, original.Binding).Accepted);
        Assert.False(CoverageOutputChecker.Certify(c, original.Binding)!.Admits(frame, out _));
        Assert.True(CoverageOutputChecker.Certify(original, original.Binding)!.Admits(frame, out _));
    }

    [Fact]
    public void A_rear_contact_reachable_only_through_rotation_is_not_hidden_by_safe_endpoints()
    {
        var (positive, originalFrame, palette) = data.Positive;
        var old = positive.Anchor.Topology;
        var face = Enumerable.Range(0, old.TriangleCount)
            .Where(f => old.Triangles[f].Role == CoverageTriangleRole.Core
                && Enumerable.Range(0, 3).Any(i => !positive.Copies[(int)old.Indices[f * 3 + i]]))
            .Select(f => new
            {
                Index = f,
                Center = Enumerable.Range(0, 3)
                .Select(i => originalFrame.After[(int)old.Indices[f * 3 + i]].Position).Aggregate(Vector3.Add) / 3
            })
            .Where(f => MathF.Abs(f.Center.X) > .1f).MaxBy(f => f.Center.Z)!;
        var f = CoverageFixture.CurvedPanel(crossingSegment: true); var source = f.Source;
        var positions = source.Positions.ToArray(); var skin = source.SkinVertices.ToArray();
        var rear = source.Triangles.Last(); int[] indices = [rear.A, rear.B, rear.C];
        Vector3[] offsets = [new(-.001f, -.001f, -.001f), new(.001f, -.001f, -.001f), new(0, .001f, .001f)];
        for (int i = 0; i < 3; i++) { positions[indices[i]] = face.Center + offsets[i]; skin[indices[i]].Position = positions[indices[i]]; }
        var changedSource = new CoverageSourceTopology(source.Identity, source.SourceSha256, positions, source.Vertices,
            source.Triangles, skin, occurrences: source.Occurrences, materials: source.Materials);
        var t = CoverageRefinement.Build(changedSource, f.Map);
        var command = positive.Command with { Topology = t };
        var exact = new CoverageCarryAnchor(t, palette, [command]);
        var anchor = CoverageCarryCertificateBuilder.Reserve(exact, palette, [command], .001f);
        CoverageOutputFrame Sample(float angle)
        {
            var matrix = Matrix4x4.CreateRotationY(angle);
            var current = command with { EquipmentWorld = matrix, CoverageWorld = matrix };
            return CoverageOutputCapture.Evaluate(anchor, positive.InverseBind, palette.Select(p => p * matrix).ToArray(), current);
        }
        var frames = new[] { Sample(-.03f), Sample(.03f), Sample(0) };
        var scratch = new CoverageScratch(t);
        for (int i = 0; i < frames.Length; i++)
        {
            bool safe = CoverageCertification.ValidateGeometry(t, frames[i].Before, frames[i].After, Matrix4x4.Identity,
                [frames[i].Command], scratch, out var failure);
            Assert.Equal(i < 2, safe);
            if (i == 2) Assert.Equal(CoverageFailureCode.HostIntersection, failure.Code);
        }
        var point = CoverageOutputCapture.Point(anchor, positive.InverseBind, frames[2], .00000001);
        point = CoverageOutputBuilder.Propose(point);
        var check = CoverageOutputChecker.Check(point, point.Binding);
        Assert.False(check.Accepted);
        Assert.Equal("H1 changed host pair", check.Cause);
        var domain = CoverageOutputCapture.Point(anchor, positive.InverseBind, frames[0], .000001);
        foreach (var frame in frames.Skip(1))
        {
            var p = CoverageOutputCapture.Point(anchor, positive.InverseBind, frame, .000001);
            for (int g = 0; g < domain.Before.Length; g++)
            { domain.Before[g] = CoverageOutputCapture.Hull(domain.Before[g], p.Before[g]); domain.After[g] = CoverageOutputCapture.Hull(domain.After[g], p.After[g]); }
        }
        domain = CoverageOutputBuilder.Propose(domain);
        Assert.False(CoverageOutputChecker.Check(domain, domain.Binding).Accepted);
    }
}
