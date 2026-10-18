using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceConstructionTests
{
    [Theory]
    [InlineData("escaped-core", CoverageFailureCode.CoreEscaped)]
    [InlineData("cap-contact", CoverageFailureCode.CapContact)]
    [InlineData("budget", CoverageFailureCode.RecessionExceeded)]
    [InlineData("support-gap", CoverageFailureCode.UnsupportedSupport)]
    [InlineData("nonfinite", CoverageFailureCode.UnsupportedTransform)]
    public void Float_conformation_obeys_guard_codes_and_never_publishes_partial_output(string kind, CoverageFailureCode expected)
    {
        var (topology, posed, model, commands, _) = CoverageCertificationTests.Scenario(kind);
        var scratch = new CoverageReferenceScratch(topology);
        var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
        Array.Fill(scratch.Published, sentinel);
        bool accepted = CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out var failure)
            && CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure)
            && CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out failure);
        Assert.False(accepted);
        Assert.Equal(expected, failure.Code);
        Assert.Equal(topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(1u, failure.CommandId);
        Assert.Equal(1ul, failure.EquipmentInstanceId);
        Assert.All(scratch.Published, v => Assert.Equal(sentinel, v));
    }

    [Fact]
    public void Point_target_reserves_quarter_clearance_in_the_coverage_mapping()
    {
        var (topology, posed, commands, scratch) = Flat();
        Prepare(topology, posed, commands, scratch);
        Assert.Equal(.0078125f, commands[0].Clearance * .25f);
        Assert.Equal(.0390625f, scratch.Frames[0].ClearanceCoverage);
        Assert.Equal(.03125f, scratch.Frames[0].CapMargin);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out var failure), failure.ToString());
        int vertex = Core(topology);
        Assert.Equal(-.0390625f, scratch.After[vertex].Position.Z);
        Assert.Equal(.0390625f, scratch.FullRecessions[topology.Vertices[vertex].PositionGroup]);

        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var shell = CoverageCommands.Gabled(0, .5f, 1, 1, 1, false);
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-sloped", shell.Positions, shell.Indices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        Matrix4x4 model = Matrix4x4.CreateScale(2, 3, 4);
        commands = CoverageCommands.Poses(fixture, topology, support, model);
        commands[0] = commands[0] with { Clearance = .03125f, MaxWorldRecession = 20, MinZ = -20, MaxZ = 20 };
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure), failure.ToString());
        float mapped = (.0390625f * MathF.Sqrt(1.25f)) * .5f;
        Assert.Equal(mapped, scratch.Frames[0].ClearanceCoverage);
        Assert.Equal(.0078125f, scratch.Frames[0].CapMargin);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out failure), failure.ToString());
        vertex = Enumerable.Range(0, topology.VertexCount).First(v => topology.IsCoreVertex(v) && scratch.Before[v].Position.X == 0);
        Assert.Equal(-mapped, scratch.After[vertex].Position.Z);
    }

    [Fact]
    public void Point_conformation_checks_the_full_target_at_zero_transition()
    {
        var (topology, posed, model, commands, _) = CoverageCertificationTests.Scenario("budget");
        commands[0] = commands[0] with { TransitionWeight = 0 };
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, model, commands, scratch, out _));
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out _));
        Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, failure.Code);

        var flat = Flat();
        flat.Commands[0] = flat.Commands[0] with { TransitionWeight = 0 };
        Prepare(flat.Topology, flat.Posed, flat.Commands, flat.Scratch);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(flat.Topology, flat.Commands, flat.Scratch, out _));
        int vertex = Core(flat.Topology), group = flat.Topology.Vertices[vertex].PositionGroup;
        Assert.Equal(.0390625f, flat.Scratch.FullRecessions[group]);
        Assert.Equal(-.0390625f, flat.Scratch.After[vertex].Position.Z);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Reference_interpolation_welds_reversed_edges_at_levels_2_3_4(int level)
    {
        var fixture = CoverageFixture.CurvedPanel(level: level);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure), failure.ToString());
        int seams = 0, edges = 0, interiors = 0;
        foreach (var group in Enumerable.Range(0, topology.VertexCount).GroupBy(v => topology.Vertices[v].PositionGroup))
        {
            var bits = PositionBits(scratch.Before[group.First()].Position);
            Assert.All(group, v => Assert.Equal(bits, PositionBits(scratch.Before[v].Position)));
            if (group.Count() > 1) seams++;
            var recipe = scratch.ReferenceTopology.Recipes[group.Key];
            Assert.Equal(12, recipe.Denominator);
            Assert.Equal(12, recipe.WeightA + recipe.WeightB + recipe.WeightC);
            if (topology.Vertices[group.First()].SourceVertex >= 0) continue;
            Vector3 a = fixture.Posed[recipe.A].Position, b = fixture.Posed[recipe.B].Position, c = fixture.Posed[recipe.C].Position;
            Vector3 expected;
            if (recipe.WeightA == 0 || recipe.WeightB == 0 || recipe.WeightC == 0)
            {
                int first = recipe.WeightA == 0 ? recipe.B : recipe.A;
                int second = recipe.WeightC == 0 ? recipe.B : recipe.C;
                int numerator = recipe.WeightC == 0 ? recipe.WeightB : recipe.WeightC;
                if (first > second) { (first, second) = (second, first); numerator = 12 - numerator; }
                a = fixture.Posed[first].Position; b = fixture.Posed[second].Position;
                float t = numerator / 12f;
                expected = new((a.X * (1 - t)) + (b.X * t), (a.Y * (1 - t)) + (b.Y * t), (a.Z * (1 - t)) + (b.Z * t));
                edges++;
            }
            else
            {
                expected = new((((a.X * recipe.WeightA) + (b.X * recipe.WeightB)) + (c.X * recipe.WeightC)) / 12f,
                    (((a.Y * recipe.WeightA) + (b.Y * recipe.WeightB)) + (c.Y * recipe.WeightC)) / 12f,
                    (((a.Z * recipe.WeightA) + (b.Z * recipe.WeightB)) + (c.Z * recipe.WeightC)) / 12f);
                interiors++;
            }
            Assert.Equal(PositionBits(expected), bits);
        }
        Assert.True(seams > 0 && edges > 0);
        if (level > 2) Assert.True(interiors > 0);
    }

    [Fact]
    public void Original_protected_records_are_byte_exact()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        foreach (ref var v in fixture.Posed.AsSpan())
        {
            v.Normal = new(-0f, .5f, -2); v.Color = new(.25f, -0f, 2, .75f);
            v.Uv = new(-0f, 7); v.Tangent = new(1, -0f, 0, -1);
        }
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        int count = 0;
        for (int v = 0; v < topology.VertexCount; v++)
        {
            var record = topology.Vertices[v];
            if (record.SourceVertex < 0 || record.Strength != 0) continue;
            Assert.Equal(Bytes(fixture.Posed.AsSpan(record.SourceVertex, 1)), Bytes(scratch.Before.AsSpan(v, 1)));
            count++;
        }
        Assert.True(count > 0);
    }

    [Fact]
    public void Signed_zero_aliases_match_exact_interpolation_policy()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var alias = topology.SourceAliases[0];
        fixture.Posed[alias.Representative].Position.X = +0f;
        fixture.Posed[alias.SourceVertex].Position = fixture.Posed[alias.Representative].Position;
        fixture.Posed[alias.SourceVertex].Position.X = -0f;
        var exact = new ModelVertex[topology.VertexCount];
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(topology.TryInterpolate(fixture.Posed, exact, out var exactFailure), exactFailure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure), failure.ToString());

        int representative = Enumerable.Range(0, topology.VertexCount).First(v => topology.Vertices[v].SourceVertex == alias.Representative);
        int originalAlias = Enumerable.Range(0, topology.VertexCount).First(v => topology.Vertices[v].SourceVertex == alias.SourceVertex);
        Assert.Equal(0f, topology.Vertices[representative].Strength);
        Assert.Equal(0f, topology.Vertices[originalAlias].Strength);
        Assert.Equal(topology.Vertices[representative].PositionGroup, topology.Vertices[originalAlias].PositionGroup);
        foreach (var output in new[] { exact, scratch.Before })
        {
            Assert.Equal(0, BitConverter.SingleToInt32Bits(output[representative].Position.X));
            Assert.Equal(int.MinValue, BitConverter.SingleToInt32Bits(output[originalAlias].Position.X));
            Assert.Equal(Bytes(fixture.Posed.AsSpan(alias.Representative, 1)), Bytes(output.AsSpan(representative, 1)));
            Assert.Equal(Bytes(fixture.Posed.AsSpan(alias.SourceVertex, 1)), Bytes(output.AsSpan(originalAlias, 1)));
        }
        int seams = 0;
        foreach (var group in Enumerable.Range(0, topology.VertexCount).Where(v => topology.Vertices[v].SourceVertex < 0)
            .GroupBy(v => topology.Vertices[v].PositionGroup))
        {
            var bits = PositionBits(scratch.GroupPositions[group.Key]);
            Assert.All(group, v => Assert.Equal(bits, PositionBits(scratch.Before[v].Position)));
            Assert.All(group, v => Assert.Equal(PositionBits(exact[group.First()].Position), PositionBits(exact[v].Position)));
            if (group.Count() > 1) seams++;
        }
        Assert.True(seams > 0);
        for (int v = 0; v < topology.VertexCount; v++)
        {
            if (topology.Vertices[v].SourceVertex < 0) continue;
            byte[] expected = Bytes(exact.AsSpan(v, 1)), actual = Bytes(scratch.Before.AsSpan(v, 1));
            Assert.True(expected.AsSpan().SequenceEqual(actual),
                $"Vertex {v} differs: exact {Convert.ToHexString(expected)}, reference {Convert.ToHexString(actual)}.");
        }
    }

    [Fact]
    public void Value_mismatched_aliases_match_exact_folded_patch_refusal()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var alias = topology.SourceAliases[0];
        fixture.Posed[alias.SourceVertex].Position.X = MathF.BitIncrement(fixture.Posed[alias.Representative].Position.X);
        var exact = new ModelVertex[topology.VertexCount];
        var scratch = new CoverageReferenceScratch(topology);
        Assert.False(topology.TryInterpolate(fixture.Posed, exact, out var exactFailure));
        Assert.False(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, exactFailure.Code);
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    [Fact]
    public void Interior_float_sums_keep_left_associated_cancellation()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        int group = Enumerable.Range(0, topology.PositionGroupCount).First(g =>
        {
            var r = topology.GetPositionRecipe(g);
            return r.WeightA == 4 && r.WeightB == 4 && r.WeightC == 4;
        });
        var recipe = topology.GetPositionRecipe(group);
        fixture.Posed[recipe.A].Position.Z = MathF.ScaleB(1, 25);
        fixture.Posed[recipe.B].Position.Z = -MathF.ScaleB(1, 25);
        fixture.Posed[recipe.C].Position.Z = 1;
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        int vertex = scratch.ReferenceTopology.GroupRepresentatives[group];
        Assert.Equal(1f / 3f, scratch.Before[vertex].Position.Z);
    }

    [Theory]
    [InlineData("alias", CoverageFailureCode.FoldedPatch)]
    [InlineData("position", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("normal", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("color", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("uv", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("tangent", CoverageFailureCode.UnsupportedTransform)]
    public void Invalid_interpolation_refuses_before_scratch_vertices_change(string attribute, CoverageFailureCode code)
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        switch (attribute)
        {
            case "alias": fixture.Posed[topology.SourceAliases[0].SourceVertex].Position.X += .1f; break;
            case "position": fixture.Posed[^1].Position.X = float.PositiveInfinity; break;
            case "normal": fixture.Posed[^1].Normal.X = float.NaN; break;
            case "color": fixture.Posed[^1].Color.W = float.NaN; break;
            case "uv": fixture.Posed[^1].Uv.Y = float.NaN; break;
            case "tangent": fixture.Posed[^1].Tangent.W = float.NaN; break;
        }
        var scratch = new CoverageReferenceScratch(topology);
        byte[] before = Bytes(scratch.Before);
        Assert.False(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure));
        Assert.Equal(code, failure.Code);
        Assert.Equal(before, Bytes(scratch.Before));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_directions_reject_cancellation_and_preserve_absent_data(bool tangent)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var midpoint = topology.Vertices.First(v => v.SourceVertex < 0 && new[] { v.A, v.B, v.C }.Count(w => w == 6) == 2);
        var triangle = fixture.Source.Triangles[midpoint.SourceTriangle];
        int[] ends = new[] { (triangle.A, midpoint.A), (triangle.B, midpoint.B), (triangle.C, midpoint.C) }
            .Where(p => p.Item2 == 6).Select(p => p.Item1).ToArray();
        if (tangent) { fixture.Posed[ends[0]].Tangent = new(0, 1, 0, 1); fixture.Posed[ends[1]].Tangent = new(0, -1, 0, -1); }
        else { fixture.Posed[ends[0]].Normal = Vector3.UnitY; fixture.Posed[ends[1]].Normal = -Vector3.UnitY; }
        var scratch = new CoverageReferenceScratch(topology);
        Assert.False(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.All(scratch.Before, v => Assert.Equal(default, v));
        foreach (ref var v in fixture.Posed.AsSpan()) { v.Normal = Vector3.Zero; v.Tangent = Vector4.Zero; }
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        Assert.All(scratch.Before, v => { Assert.Equal(Vector3.Zero, v.Normal); Assert.Equal(Vector4.Zero, v.Tangent); });
    }

    [Fact]
    public void Generated_attributes_use_ordered_numerators_and_zero_handedness_is_positive()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var midpoint = topology.Vertices.First(v => v.SourceVertex < 0 && new[] { v.A, v.B, v.C }.Count(w => w == 6) == 2);
        var triangle = fixture.Source.Triangles[midpoint.SourceTriangle];
        int[] ends = new[] { (triangle.A, midpoint.A), (triangle.B, midpoint.B), (triangle.C, midpoint.C) }
            .Where(p => p.Item2 == 6).Select(p => p.Item1).ToArray();
        foreach (ref var v in fixture.Posed.AsSpan()) { v.Normal = new(3, 4, 0); v.Tangent = new(0, 0, 2, 0); }
        fixture.Posed[ends[0]].Color = new(2, 4, 6, 8); fixture.Posed[ends[1]].Color = new(4, 8, 12, 16);
        fixture.Posed[ends[0]].Uv = new(2, 6); fixture.Posed[ends[1]].Uv = new(4, 10);
        fixture.Posed[ends[0]].Tangent.W = -1; fixture.Posed[ends[1]].Tangent.W = 1;
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        int vertex = Enumerable.Range(0, topology.VertexCount).First(v => topology.Vertices[v] == midpoint);
        Assert.Equal(new Vector4(3, 6, 9, 12), scratch.Before[vertex].Color);
        Assert.Equal(new Vector2(3, 8), scratch.Before[vertex].Uv);
        Assert.Equal(new Vector3(.6f, .8f, 0), scratch.Before[vertex].Normal);
        Assert.Equal(new Vector4(0, 0, 1, 1), scratch.Before[vertex].Tangent);
    }

    [Fact]
    public void Absent_tangent_does_not_evaluate_unused_handedness()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        foreach (ref var v in fixture.Posed.AsSpan()) v.Tangent = new(0, 0, 0, float.MaxValue);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure), failure.ToString());
        for (int v = 0; v < topology.VertexCount; v++)
            Assert.Equal(topology.Vertices[v].SourceVertex < 0 ? Vector4.Zero : new(0, 0, 0, float.MaxValue), scratch.Before[v].Tangent);
    }

    [Fact]
    public void Clearance_override_is_slot_ordered_rounded_once_and_changes_only_target_clearance()
    {
        var (topology, posed, commands, scratch) = Flat();
        Prepare(topology, posed, commands, scratch);
        var frame = scratch.Frames[0];
        double clearance = .0625 + Math.ScaleB(1, -28) + Math.ScaleB(1, -40);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _, new[] { clearance }));
        Assert.Equal(frame with { ClearanceCoverage = MathF.BitIncrement(.0625f) }, scratch.Frames[0]);
        Assert.Equal(-MathF.BitIncrement(.0625f), scratch.After[Core(topology)].Position.Z);
        Prepare(topology, posed, commands, scratch);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _));
        Assert.Equal(-.0390625f, scratch.After[Core(topology)].Position.Z);
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _, new double[2]));
    }

    [Fact]
    public void Clearance_overrides_follow_sorted_slots_when_commands_are_reversed()
    {
        var (topology, posed, commands, scratch) = Flat(secondRegion: true);
        Array.Reverse(commands);
        Prepare(topology, posed, commands, scratch);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _, new[] { .0625, .125 }));
        for (int region = 0; region < 2; region++)
        {
            int vertex = Enumerable.Range(0, topology.VertexCount).First(v => topology.IsCoreVertex(v) && topology.Vertices[v].RegionIndex == region);
            Assert.Equal(region == 0 ? -.0625f : -.125f, scratch.After[vertex].Position.Z);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonzero_direction_sums_below_the_surviving_fraction_are_refused(bool tangent)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, level: 3);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var record = topology.Vertices.First(v => v.A == 4 && v.B == 4 && v.C == 4);
        var triangle = fixture.Source.Triangles[record.SourceTriangle];
        foreach (var (vertex, value) in new[] { (triangle.A, 1f), (triangle.B, -1f), (triangle.C, 1e-8f) })
        {
            if (tangent) fixture.Posed[vertex].Tangent = new(value, 0, 0, 1);
            else fixture.Posed[vertex].Normal = new(value, 0, 0);
        }
        var scratch = new CoverageReferenceScratch(topology);
        Assert.False(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.All(scratch.Before, v => Assert.Equal(default, v));
    }

    [Fact]
    public void Point_guards_check_full_caps_finite_arithmetic_and_zero_requests()
    {
        var (topology, posed, commands, scratch) = Flat();
        commands[0] = commands[0] with { MinZ = -.0625f };
        Prepare(topology, posed, commands, scratch);
        Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.CapContact, failure.Code);
        commands[0] = commands[0] with { MinZ = -2 };
        Prepare(topology, posed, commands, scratch);
        scratch.Frames[0] = scratch.Frames[0] with { HostToCoverage = Matrix4x4.CreateScale(1, 1, 2) };
        scratch.Before[Core(topology)].Position.Z = float.MaxValue;
        Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        foreach (ref var v in posed.AsSpan()) v.Position.Z = -.5f;
        Prepare(topology, posed, commands, scratch);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _));
        Assert.Equal(Bytes(scratch.Before), Bytes(scratch.After));
        Assert.All(scratch.FullRecessions, value => Assert.Equal(0, value));
    }

    [Fact]
    public void Refusals_keep_first_representative_command_and_count_only_its_repeats()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        commands[0] = commands[0] with { Id = 20, EquipmentInstanceId = 200, MaxWorldRecession = 0 };
        commands[1] = commands[1] with { Id = 10, EquipmentInstanceId = 100, MaxWorldRecession = 0 };
        var posed = fixture.Posed.ToArray();
        for (int i = 0; i < posed.Length; i++) posed[i].Position = new(fixture.Source.Positions[i].X, fixture.Source.Positions[i].Y, .5f);
        var scratch = new CoverageReferenceScratch(topology);
        Prepare(topology, posed, commands, scratch);
        Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out var first));
        Assert.Equal(CoverageFailureCode.RecessionExceeded, first.Code);
        Assert.Equal(20u, first.CommandId);
        Assert.Equal(200ul, first.EquipmentInstanceId);
        Assert.Equal(topology.Vertices.Where(v => v.RegionIndex == 0 && v.Strength > 0).Select(v => v.PositionGroup).Distinct().Count(), first.Count);
        Array.Reverse(commands);
        Prepare(topology, posed, commands, scratch);
        Assert.False(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out var second));
        Assert.Equal(first, second);
    }

    [Fact]
    public void Phase_arguments_are_checked_before_writes()
    {
        var (topology, posed, commands, scratch) = Flat();
        byte[] before = Bytes(scratch.Before);
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryInterpolate(topology, posed.AsSpan(1), scratch, out _));
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryInterpolate(topology, scratch.Before.AsSpan(0, posed.Length), scratch, out _));
        var foreign = new CoverageReferenceScratch(Flat().Topology);
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryInterpolate(topology, posed, foreign, out _));
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryConformGroups(topology, commands, foreign, out _));
        Assert.Equal(before, Bytes(scratch.Before));
    }

    [Fact]
    public void Collar_uses_actual_boundary_smoothstep_and_preserves_points_at_its_width()
    {
        var (topology, posed, commands, scratch) = Flat();
        Prepare(topology, posed, commands, scratch);
        int vertex = Enumerable.Range(0, topology.VertexCount).First(v => topology.Vertices[v].Strength == .5f && !topology.IsCoreVertex(v));
        int group = topology.Vertices[vertex].PositionGroup;
        foreach (int member in Enumerable.Range(0, topology.VertexCount).Where(v => topology.Vertices[v].PositionGroup == group))
            scratch.Before[member].Position = new(1.125f, 0, .25f);
        commands[0] = commands[0] with { CollarWidth = .25f };
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _));
        // Distance .125 gives u=.5, smoothstep=.5, then authored strength .5.
        Assert.Equal(.072265625f, scratch.FullRecessions[group]);
        Assert.Equal(.177734375f, scratch.After[vertex].Position.Z);
        foreach (int member in Enumerable.Range(0, topology.VertexCount).Where(v => topology.Vertices[v].PositionGroup == group))
            scratch.Before[member].Position = new(1.25f, 0, .25f);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _));
        Assert.Equal(scratch.Before[vertex], scratch.After[vertex]);
        Assert.Equal(0, scratch.FullRecessions[group]);
    }

    [Fact]
    public void Warm_phase_calls_allocate_no_managed_memory()
    {
        var (topology, posed, commands, scratch) = Flat();
        Prepare(topology, posed, commands, scratch);
        Assert.True(CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _));
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool interpolated = CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out _);
        bool conformed = CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(interpolated && conformed);
        Assert.Equal(0, allocated);
    }

    static (CoverageRefinement Topology, ModelVertex[] Posed, CoverageCommandPose[] Commands, CoverageReferenceScratch Scratch) Flat(bool secondRegion = false)
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1, secondRegion: secondRegion);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var points = fixture.RigidPositions.Select((p, i) => new Vector3(p.X, p.Y, i < 5 ? 0 : .2f)).ToArray();
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-flat", points, fixture.RigidIndices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        commands[0] = commands[0] with { Clearance = .03125f };
        var posed = fixture.Posed.ToArray();
        for (int i = 0; i < posed.Length; i++) posed[i].Position = new(fixture.Source.Positions[i].X, fixture.Source.Positions[i].Y, 0);
        return (topology, posed, commands, new(topology));
    }

    static void Prepare(CoverageRefinement topology, ModelVertex[] posed, CoverageCommandPose[] commands, CoverageReferenceScratch scratch)
    {
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out var failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure), failure.ToString());
    }

    static int Core(CoverageRefinement topology) => Enumerable.Range(0, topology.VertexCount).First(topology.IsCoreVertex);
    static int[] PositionBits(Vector3 p) => [BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z)];
    static byte[] Bytes(Span<ModelVertex> vertices) => MemoryMarshal.AsBytes(vertices).ToArray();
}
