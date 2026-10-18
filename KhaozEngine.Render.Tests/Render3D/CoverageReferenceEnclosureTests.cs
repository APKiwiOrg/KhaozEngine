using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;
using I = KhaozEngine.Render3D.CoverageFloatInterval;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageReferenceEnclosureTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases => CoverageExactCaptureTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_fixture_float_vertex_is_inside_its_pose_enclosure(string id)
    {
        var c = CoverageReferenceCases.Get(id);
        var reference = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        // B9 requires the production reserve and an empty clearance override.
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands,
            reference, final, out var failure), $"{id}: {failure}");
        var palette = new Matrix4x4[c.Palette.Length];
        for (int i = 0; i < palette.Length; i++) palette[i] = SkinningMath.Compose(c.Palette[i], c.InverseBind[i]);
        var enclosure = new CoverageReferenceEnclosureScratch(c.Topology);
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, c.Model, c.Commands, reference, enclosure);
        output.WriteLine($"ENCLOSURE {id} epsilon={enclosure.DeviceTolerance:R} delta={.25 * c.Commands[0].Clearance:R} "
            + $"site={enclosure.DominantSite} vertex={enclosure.WorstVertex} firstUnbounded={enclosure.FirstUnboundedSite} "
            + $"index={enclosure.FirstUnboundedIndex} triangle={enclosure.FirstUnboundedTriangle}");
        Assert.Equal(c.Topology.VertexCount, enclosure.HostModelBounds.Length);
        Assert.Equal(c.Topology.VertexCount, enclosure.HostWorldBounds.Length);
        for (int i = 0; i < final.Length; i++)
        {
            Assert.True(enclosure.HostModelBounds[i].Contains(final[i].Position), $"{id} vertex={i} model containment");
            var model = enclosure.HostModelBounds[i];
            var world = enclosure.HostWorldBounds[i];
            Assert.True(model.X.IsFinite && model.Y.IsFinite && model.Z.IsFinite, $"{id} vertex={i} unbounded model");
            Assert.True(world.X.IsFinite && world.Y.IsFinite && world.Z.IsFinite, $"{id} vertex={i} unbounded world");
            var center = IndependentCenter(final[i].Position, c.Model);
            Assert.True(world.X.Contains(center.X.Min) && world.X.Contains(center.X.Max)
                && world.Y.Contains(center.Y.Min) && world.Y.Contains(center.Y.Max)
                && world.Z.Contains(center.Z.Min) && world.Z.Contains(center.Z.Max), $"{id} vertex={i} exact world center");
        }
        Assert.True(double.IsFinite(enclosure.DeviceTolerance), $"{id} unbounded epsilon");
    }

    [Fact]
    public void World_tolerance_sums_host_and_full_equipment_bounds()
    {
        var host = new CoverageVertexEnclosure(new(-3, 3), new(-4, 4), new(0, 0));
        var zero = new CoverageVertexEnclosure(new(0, 0), new(0, 0), new(0, 0));
        double radius = CoverageReferencePrecision.Radius(host, zero);
        Assert.InRange(radius, 5, Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(5))));
        Assert.InRange(CoverageReferencePrecision.ComposeTolerance(radius, 12), 17,
            Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(17))));
    }

    [Fact]
    public void Enclosures_propagate_conditioning_flushing_and_branch_alternatives()
    {
        var a = CoverageEnclosureMath.Point(Vector3.Zero);
        var b = CoverageEnclosureMath.Point(new(1, 1, 0));
        var c = CoverageEnclosureMath.Point(new(1, MathF.BitIncrement(1), 1));
        var query = new CoverageVertexEnclosure(new(.5, .5), new(.5, .5 + Math.ScaleB(1, -24)), new(0, 0));
        var height = CoverageEnclosureSupport.Height(a, b, c, query);
        Assert.True(height.Contains(0) && height.Contains(.5));
        var wide = CoverageEnclosureSupport.Height(a, b,
            new(new(1, 1), new(1, MathF.BitIncrement(1)), new(1, 1)), query);
        Assert.False(wide.IsFinite);
        var fast = CoverageEnclosureSupport.Correction(new(.9, 1.1), MathF.ScaleB(1, -10));
        var slow = CoverageEnclosureSupport.Correction(new(.9, 1.1), MathF.ScaleB(1, -20));
        Assert.True(slow.Max - slow.Min > fast.Max - fast.Min);
        var stored = CoverageEnclosureMath.Store(CoverageEnclosureMath.Point(Vector3.One));
        Assert.True(stored.X.Contains(MathF.BitDecrement(1)) && stored.X.Contains(MathF.BitIncrement(1)));
        var zeroSign = CoverageEnclosurePredicates.Edge(a, b, CoverageEnclosureMath.Point(new(.5f, .5f, 0)));
        Assert.Equal(0, zeroSign.MinSign);
        Assert.Equal(0, zeroSign.MaxSign);
        var tiny = CoverageEnclosurePredicates.Edge(a, CoverageEnclosureMath.Point(new(float.Epsilon, 0, 0)),
            CoverageEnclosureMath.Point(new(0, .25f, 0)));
        Assert.Equal(1, tiny.MinSign);
        Assert.Equal(1, tiny.MaxSign);
        Assert.True(tiny.Value.Contains(0));
    }

    [Fact]
    public void Reordered_commands_reset_and_invalid_arguments_preserve_the_workspace_contract()
    {
        var c = CoverageReferenceCases.Get("cost-limit-l2-lifted-w1");
        var reference = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands,
            reference, final, out var failure), failure.ToString());
        var palette = new Matrix4x4[c.Palette.Length];
        for (int i = 0; i < palette.Length; i++) palette[i] = SkinningMath.Compose(c.Palette[i], c.InverseBind[i]);
        var scratch = new CoverageReferenceEnclosureScratch(c.Topology);
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, c.Model, c.Commands, reference, scratch);
        var first = scratch.HostModelBounds.ToArray();
        double epsilon = scratch.DeviceTolerance;
        Assert.Throws<ArgumentException>(() => CoverageReferenceEnclosure.Evaluate(c.Topology, palette,
            final.AsSpan(1), c.Model, c.Commands, reference, scratch));
        Assert.Equal(first, scratch.HostModelBounds.ToArray());
        var invalid = c.Model; invalid.M41 = float.PositiveInfinity;
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, invalid, c.Commands, reference, scratch);
        Assert.Equal(double.PositiveInfinity, scratch.DeviceTolerance);
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, c.Model, c.Commands, reference, scratch);
        Assert.Equal(first, scratch.HostModelBounds.ToArray());
        Assert.Equal(epsilon, scratch.DeviceTolerance);
        var reversed = (CoverageCommandPose[])c.Commands.Clone();
        Array.Reverse(reversed);
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, reversed,
            reference, final, out failure), failure.ToString());
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, c.Model, reversed, reference, scratch);
        Assert.Equal(first, scratch.HostModelBounds.ToArray());
        Assert.Equal(epsilon, scratch.DeviceTolerance);
        long start = GC.GetAllocatedBytesForCurrentThread();
        CoverageReferenceEnclosure.Evaluate(c.Topology, palette, final, c.Model, reversed, reference, scratch);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(0, allocated);
    }

    static CoverageVertexEnclosure IndependentCenter(Vector3 p, Matrix4x4 m) => new(
        Coordinate(p, m.M11, m.M21, m.M31, m.M41), Coordinate(p, m.M12, m.M22, m.M32, m.M42),
        Coordinate(p, m.M13, m.M23, m.M33, m.M43));

    static I Coordinate(Vector3 p, float x, float y, float z, float t)
    {
        double lo = Math.BitDecrement(Math.BitDecrement(Math.BitDecrement((double)p.X * x)
            + Math.BitDecrement((double)p.Y * y)) + Math.BitDecrement((double)p.Z * z));
        double hi = Math.BitIncrement(Math.BitIncrement(Math.BitIncrement((double)p.X * x)
            + Math.BitIncrement((double)p.Y * y)) + Math.BitIncrement((double)p.Z * z));
        return new(Math.BitDecrement(lo + t), Math.BitIncrement(hi + t));
    }
}
