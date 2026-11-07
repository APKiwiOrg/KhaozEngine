using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageOutputDomainTests
{
    [Fact]
    public void Unchanged_host_pair_claims_follow_bit_copies_and_cannot_hide_a_changed_triangle()
    {
        var fixture = new CoverageCarryFixture(zeroWords: true);
        var palette = CoverageCarryFixture.Palette(0);
        var command = fixture.Commands(0, palette[0])[0];
        var anchor = new CoverageCarryAnchor(fixture.Topology, palette, [command]);
        var mask = CoverageOutputIdentity.Copies(anchor, command);
        var scratch = new CoverageReferenceScratch(fixture.Topology);
        var output = new ModelVertex[fixture.Topology.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(fixture.Topology, palette, [command], anchor, scratch, output, out _));
        Assert.Contains(mask, v => v);
        Assert.Contains(mask, v => !v);
        for (int v = 0; v < mask.Length; v++)
            if (mask[v])
                Assert.True(MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in scratch.Before[v]))
                    .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<ModelVertex>(in output[v]))));
        Assert.True(CoverageOutputIdentity.Check(anchor, command, mask));
        int moved = Array.FindIndex(mask, copy => !copy);
        var mutation = (bool[])mask.Clone();
        mutation[moved] = true;
        Assert.False(CoverageOutputIdentity.Check(anchor, command, mutation));
    }

    internal static (CoverageOutputCell Cell, CoverageOutputFrame Frame, Matrix4x4[] Palette) Positive()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var command = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity)[0];
        var palette = CoverageFixture.Pose;
        var inverse = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        var exact = new CoverageCarryAnchor(topology, palette, [command]);
        var anchor = CoverageCarryCertificateBuilder.Reserve(exact, palette, [command], .001f);
        var frame = CoverageOutputCapture.Evaluate(anchor, inverse, palette, command);
        var cell = CoverageOutputCapture.Point(anchor, inverse, frame, .00001);
        return (CoverageOutputBuilder.Propose(cell), frame, palette);
    }

    [Fact]
    public void A_continuous_output_domain_admits_actual_rotations_with_correlated_translation()
    {
        var (cell, frame, palette) = Positive();
        var check = CoverageOutputChecker.Check(cell, cell.Binding);
        Assert.True(check.Accepted, check.ToString());
        var certified = CoverageOutputChecker.Certify(cell, cell.Binding);
        Assert.NotNull(certified);
        Assert.True(certified.Admits(frame, out _));
        var common = Matrix4x4.CreateRotationY(.2f) * Matrix4x4.CreateTranslation(.1f, .2f, -.1f);
        var command = cell.Command with { EquipmentWorld = common, CoverageWorld = cell.Command.Support.CoverageLocal * common };
        var rotated = CoverageOutputCapture.Evaluate(cell.Anchor, cell.InverseBind, palette.Select(p => p * common).ToArray(), command);
        Assert.True(certified.Admits(rotated, out _));
        Assert.NotEqual(palette[0].M11, (palette[0] * common).M11);
    }
}
