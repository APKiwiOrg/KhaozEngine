using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageCarryCertificateTestData
{
    internal CoverageCarryFixture Fixture { get; } = new();
    internal CoverageCarryBuild[] Builds { get; }
    public CoverageCarryCertificateTestData() => Builds = [CoverageCarryCertificateBuilder.Build(Fixture, 0),
        CoverageCarryCertificateBuilder.Build(Fixture, 1)];
}

public sealed class CoverageCarryCertificateTests(CoverageCarryCertificateTestData data, ITestOutputHelper output)
    : IClassFixture<CoverageCarryCertificateTestData>
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void F0_gets_a_positive_translation_domain_without_changing_the_frozen_answer(int pose)
    {
        var fixture = data.Fixture;
        var result = data.Builds[pose];
        Assert.True(result.Cell != null, string.Join(Environment.NewLine, result.Attempts.Select(a =>
            $"reserve={a.ReserveWorld:R} radius={a.Radius:R} {a.Check}")));
        Assert.NotNull(result.Cell);
        Assert.True(result.Cell.Radius > 0);
        Assert.Equal(1092, fixture.Topology.SourceVertexCount);
        Assert.Equal(1258, fixture.Topology.VertexCount);
        Assert.Equal(2332, fixture.Topology.TriangleCount);
        Assert.True(CoverageCarryCertificateChecker.Check(result.Cell).Accepted);
        Assert.Equal(0, result.Attempts[0].ReserveWorld);
        output.WriteLine($"{result.Cell.Motion} radius={result.Cell.Radius:R} reserve={result.Cell.ReserveWorld:R}");
    }

    [Fact]
    public void Every_single_field_mutation_is_refused_and_rehashed_bad_proofs_still_fail()
    {
        var c = data.Builds[0].Cell!;
        var scalars = c.Anchor.Recession.ToArray(); scalars[Array.FindIndex(scalars, x => x > 0)] += .1f;
        var commands = (CoverageCommandPose[])c.Commands.Clone();
        commands[0] = commands[0] with { TransitionWeight = .5f };
        var support = (CoverageCommandPose[])c.Commands.Clone();
        support[0] = support[0] with { Support = support[1].Support, CoverageWorld = support[1].Support.CoverageLocal * support[0].EquipmentWorld };
        var transformed = (CoverageCommandPose[])c.Commands.Clone();
        var matrix = Matrix4x4.CreateScale(1.01f) * transformed[0].EquipmentWorld;
        transformed[0] = transformed[0] with { EquipmentWorld = matrix, CoverageWorld = transformed[0].Support.CoverageLocal * matrix };
        var nonfinite = (Matrix4x4[])c.Center.Clone(); nonfinite[0].M43 = float.NaN;
        var cases = new (string Name, CoverageCarryCell Cell)[]
        {
            ("widened bound", c with { Radius = c.Radius * 2 }),
            ("altered support", c with { Commands = support }),
            ("altered hash", c with { Binding = new string('0', 64) }),
            ("wrong scalar", c with { Anchor = new(c.Anchor, scalars) }),
            ("wrong transition scalar", c with { Commands = commands }),
            ("changed transform", c with { Commands = transformed }),
            ("nonfinite input", c with { Center = nonfinite }),
            ("nonfinite bound", c with { Radius = float.NaN }),
        };
        foreach (var test in cases)
        {
            var verdict = CoverageCarryCertificateChecker.Check(test.Cell);
            Assert.False(verdict.Accepted, test.Name);
            output.WriteLine($"{test.Name}: refused {verdict.Cause}");
        }
        Assert.False(CoverageCarryCellBinding.Admits(c, c.Center, c.Commands, layer: 1));
        var widened = c with { Radius = .1f };
        widened = widened with { DomainDigest = CoverageCarryCellBinding.Domain(widened) };
        var rejected = CoverageCarryCertificateChecker.Check(widened);
        Assert.False(rejected.Accepted); Assert.NotEqual("input/domain binding", rejected.Cause);
        var missing = c with { Planes = [] };
        missing = missing with { DomainDigest = CoverageCarryCellBinding.Domain(missing) };
        Assert.False(CoverageCarryCertificateChecker.Check(missing).Accepted);
    }

    [Fact]
    public void Actual_skin_influences_and_every_command_topology_participate_in_admission()
    {
        var c = data.Builds[0].Cell!; var s = data.Fixture.Topology.Source;
        var skin = s.SkinVertices.ToArray(); skin[0].BoneWeights = new(0, 1, 0, 0);
        var source = new CoverageSourceTopology(s.Identity, s.SourceSha256, s.Positions, s.Vertices, s.Triangles,
            skin, occurrences: s.Occurrences, materials: s.Materials);
        var changed = CoverageRefinement.Build(source, data.Fixture.Topology.Map);
        Assert.NotEqual(c.Binding, CoverageCarryCellBinding.Inputs(changed, c.Anchor, c.Commands));
        var commands = (CoverageCommandPose[])c.Commands.Clone();
        commands[1] = commands[1] with { Topology = changed };
        Assert.False(CoverageCarryCellBinding.Admits(c, c.Center, commands));
    }

    [Fact]
    public void Inclusive_cell_boundaries_reject_the_next_binary32_neighbour_on_all_six_axes()
    {
        var c = data.Builds[0].Cell!;
        for (int bone = 0; bone < 2; bone++)
            for (int component = 0; component < 3; component++)
                foreach (bool upper in new[] { false, true })
                {
                    var p = (Matrix4x4[])c.Center.Clone();
                    float origin = component == 0 ? p[bone].M41 : component == 1 ? p[bone].M42 : p[bone].M43;
                    var range = CoverageCarryFloat.Around(origin, c.Radius);
                    float edge = upper ? range.Max : range.Min;
                    SetTranslation(p, bone, component, edge);
                    Assert.True(CoverageCarryCellBinding.Admits(c, p, c.Commands));
                    SetTranslation(p, bone, component, upper ? MathF.BitIncrement(edge) : MathF.BitDecrement(edge));
                    Assert.False(CoverageCarryCellBinding.Admits(c, p, c.Commands));
                }
    }

    [Fact]
    public void Tilted_frames_are_outside_the_declared_command_projection_domain_even_when_rehashed()
    {
        var c = data.Builds[0].Cell!;
        var commands = (CoverageCommandPose[])c.Commands.Clone();
        var transform = Matrix4x4.CreateRotationY(.1f) * commands[0].EquipmentWorld;
        commands[0] = commands[0] with { EquipmentWorld = transform, CoverageWorld = commands[0].Support.CoverageLocal * transform };
        var candidate = c with { Commands = commands, Binding = CoverageCarryCellBinding.Inputs(c.Anchor.Topology, c.Anchor, commands) };
        candidate = candidate with { DomainDigest = CoverageCarryCellBinding.Domain(candidate) };
        Assert.False(CoverageCarryCellBinding.Admits(candidate, candidate.Center, commands));
        Assert.False(CoverageCarryCertificateChecker.Check(candidate).Accepted);
    }

    [Fact]
    public void Safe_endpoints_do_not_admit_an_unsafe_nonlinear_interior()
    {
        var c = data.Builds[0].Cell!;
        foreach (float phase in new[] { 0f, 1f, .5f })
        {
            var p = (Matrix4x4[])c.Center.Clone();
            float z = -.005f + .105f * (4 * phase * (1 - phase));
            for (int k = 0; k < p.Length; k++) { p[k].M43 += z; p[k].M41 += (phase - .5f) * .00001f; }
            bool safe = Sample(c, p, out var failure);
            Assert.Equal(phase != .5f, safe);
            output.WriteLine($"nonlinear phase={phase}: safe={safe}, failure={failure.Code}");
        }
        var envelope = CoverageCarryCertificateBuilder.Attempt(c with { Radius = .11f });
        Assert.False(envelope.Check.Accepted);
    }

    [Fact]
    public void Full_triangle_witnesses_refuse_collar_and_rear_host_contacts_despite_safe_core()
    {
        var c = data.Builds[0].Cell!; var t = c.Anchor.Topology;
        var surface = new CoverageCarryEnclosure(c with { Radius = 0 });
        int f = Enumerable.Range(0, t.TriangleCount).First(i => t.Triangles[i].Role == CoverageTriangleRole.Core && t.Triangles[i].RegionIndex == 0);
        var core = CoverageCarryProofGeometry.Triangle(t, surface.After, f);
        var solid = CoverageCarryProofGeometry.Equipment(c.Commands[0]);
        Assert.True(solid.All(b => CoverageCarryProofGeometry.Axes(core, b, false).Any(axis => CoverageCarryProofGeometry.Gap(core, b, axis) > .02)));
        // Deliberate witness mutations use an actual F0 equipment face and actual F0 host face. They are not
        // alternative fixtures. A core-only implementation would miss both contacts.
        var collarContact = solid[0];
        Assert.All(CoverageCarryProofGeometry.Axes(collarContact, solid[0], false),
            axis => Assert.False(CoverageCarryProofGeometry.Gap(collarContact, solid[0], axis) > 0));
        Assert.All(CoverageCarryProofGeometry.Axes(core, core, true),
            axis => Assert.False(CoverageCarryProofGeometry.HostMargin(core, core, [1, 2, 3], [4, 5, 6], axis) > 0));
    }

    [Fact]
    public void A_rehashed_F0_rear_host_collision_is_refused_while_its_core_stays_clear()
    {
        var c = data.Builds[0].Cell!; var t = c.Anchor.Topology;
        var scratch = new CoverageReferenceScratch(t); var normal = new ModelVertex[t.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(t, c.Center, c.Commands, c.Anchor, scratch, normal, out _));
        int coreFace = Enumerable.Range(0, t.TriangleCount).First(i => t.Triangles[i].RegionIndex == 0
            && t.Triangles[i].Role == CoverageTriangleRole.Core);
        var core = Enumerable.Range(0, 3).Select(k => normal[(int)t.Indices[coreFace * 3 + k]].Position).ToArray();
        // Three protected F0 source vertices become a crossing rear face. No core recipe or scalar changes.
        var rear = t.Source.Triangles[2]; int[] vertices = [rear.A, rear.B, rear.C];
        var positions = t.Source.Positions.ToArray(); var skin = t.Source.SkinVertices.ToArray();
        for (int k = 0; k < 3; k++)
        {
            positions[vertices[k]] = core[k] - c.Center[0].Translation + new Vector3(0, 0, k == 0 ? -.01f : .01f);
            skin[vertices[k]].Position = positions[vertices[k]];
        }
        var s = t.Source;
        var changed = CoverageRefinement.Build(new(s.Identity, s.SourceSha256, positions, s.Vertices, s.Triangles, skin,
            occurrences: s.Occurrences, materials: s.Materials), t.Map);
        var commands = c.Commands.Select(x => x with { Topology = changed }).ToArray();
        var exact = new CoverageCarryAnchor(changed, c.Center, commands);
        var anchor = new CoverageCarryAnchor(exact, c.Anchor.Recession);
        var candidate = c with
        {
            Anchor = anchor,
            Commands = commands,
            Radius = 0,
            Binding = CoverageCarryCellBinding.Inputs(changed, anchor, commands)
        };
        var mutated = new CoverageReferenceScratch(changed); var surface = new ModelVertex[changed.VertexCount];
        Assert.True(CoverageCarryReference.TryCarry(changed, c.Center, commands, anchor, mutated, surface, out _));
        for (int k = 0; k < 3; k++) Assert.Equal(core[k], surface[(int)changed.Indices[coreFace * 3 + k]].Position);
        var actualRear = vertices.Select(v => CoverageVector.From(surface[v].Position)).ToArray();
        Assert.False(CoverageContact.OnlyCommonTopology(core.Select(CoverageVector.From).ToArray(), actualRear,
            [1, 2, 3], [4, 5, 6]));
        var verdict = CoverageCarryCertificateBuilder.Attempt(candidate).Check;
        Assert.False(verdict.Accepted);
        Assert.NotEqual("input/domain binding", verdict.Cause);
        output.WriteLine($"rear collision, unchanged safe core: refused {verdict.Cause}, triangle {verdict.Triangle}");
    }

    [Fact]
    public void Fixed_nonuniform_scale_shear_and_attachment_are_in_the_proof_and_changes_expire_it()
    {
        var transform = Matrix4x4.CreateScale(1.01f, .99f, 1.02f);
        transform.M21 = .005f; transform.Translation = new(0, .1f, 0);
        var build = CoverageCarryCertificateBuilder.Build(data.Fixture, 0, transform);
        Assert.NotNull(build.Cell);
        Assert.True(CoverageCarryCertificateChecker.Check(build.Cell).Accepted);
        Assert.True(Sample(build.Cell, build.Cell.Center, out _));
        Assert.Equal(321, CoverageCarryFalsifier.Run(build.Cell));
        output.WriteLine($"scale/shear radius={build.Cell.Radius:R} reserve={build.Cell.ReserveWorld:R}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Dense_exact_sampling_is_a_separate_falsifier_for_the_continuous_proof(int pose)
    {
        foreach (var cell in data.Builds[pose].Candidates)
        {
            int samples = CoverageCarryFalsifier.Run(cell);
            Assert.Equal(321, samples);
            output.WriteLine($"{cell.Motion} reserve={cell.ReserveWorld:R}: {samples} independent exact carry/geometry samples, zero clipping");
        }
    }

    internal static bool Sample(CoverageCarryCell c, Matrix4x4[] palette, out CoverageFailure failure)
    {
        var t = c.Anchor.Topology; var scratch = new CoverageReferenceScratch(t); var result = new ModelVertex[t.VertexCount];
        return CoverageCarryReference.TryCarry(t, palette, c.Commands, c.Anchor, scratch, result, out failure)
            && CoverageCertification.ValidateGeometry(t, scratch.Before, result, Matrix4x4.Identity, c.Commands, new(t), out failure);
    }

    internal static void SetTranslation(Matrix4x4[] p, int bone, int component, float value)
    {
        if (component == 0) p[bone].M41 = value;
        else if (component == 1) p[bone].M42 = value;
        else p[bone].M43 = value;
    }
}
