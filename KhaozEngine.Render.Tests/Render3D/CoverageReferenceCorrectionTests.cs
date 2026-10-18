using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageReferenceCorrectionTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases => CoverageExactCaptureTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Gathered_correction_moves_idle_core_groups_once_and_matches_exact_capture_margin(string id)
    {
        var c = CoverageReferenceCases.Get(id);
        var capture = CoverageExactCaptureTests.Read(id);
        var scratch = Pointwise(c, capture);
        byte[] pointwise = Bytes(scratch.After);
        float[] initial = scratch.FullRecessions.ToArray();
        int faces = 0, groups = 0, correctionScatterWrites = 0;
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure,
            capture.CorrectionClearance, capture.CorrectionConversion, work =>
            {
                if (work.Phase == CoverageReferenceCorrectionPhase.Faces)
                {
                    faces++;
                    if (work.Group >= 0 || work.Vertex >= 0) correctionScatterWrites++;
                    Assert.True(MemoryMarshal.AsBytes(scratch.After.AsSpan()).SequenceEqual(pointwise));
                    Assert.True(initial.AsSpan().SequenceEqual(scratch.FullRecessions));
                    foreach (float value in scratch.Corrections) Assert.Equal(0, value);
                }
                if (work.Phase == CoverageReferenceCorrectionPhase.Groups) groups++;
            }), failure.ToString());
        Assert.Equal(c.Topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core), faces);
        Assert.Equal(scratch.ActiveGroupRepresentatives.Count(v => v >= 0), groups);
        Assert.Equal(0, correctionScatterWrites);
        int duplicateCoreFaceEntries = 0;
        for (int g = 0; g < c.Topology.PositionGroupCount; g++)
        {
            int[] row = scratch.ReferenceTopology.CoreFaces[scratch.ReferenceTopology.CoreFaceOffsets[g]..scratch.ReferenceTopology.CoreFaceOffsets[g + 1]];
            duplicateCoreFaceEntries += row.Length - row.Distinct().Count();
            Assert.Equal(row.Order(), row);
        }
        Assert.Equal(0, duplicateCoreFaceEntries);
        bool ordinary = id.StartsWith("curved-ordinary-", StringComparison.Ordinal);
        if (ordinary)
        {
            Assert.Contains(scratch.Corrections, r => r > 0);
            Assert.Contains(Enumerable.Range(0, initial.Length), g => initial[g] == 0 && scratch.Corrections[g] > 0
                && scratch.GroupPositions[g] != scratch.Before[scratch.ActiveGroupRepresentatives[g]].Position);
        }
        Vector3[] full = scratch.GroupPositions.ToArray();
        CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, scratch);
        int corrected = 0, worstGroup = -1, protectedByteChanges = 0;
        double maximum = 0;
        for (int g = 0; g < c.Topology.PositionGroupCount; g++)
        {
            int representative = scratch.ActiveGroupRepresentatives[g];
            if (representative < 0) continue;
            int slot = scratch.RegionCommands[c.Topology.Vertices[representative].RegionIndex];
            var command = c.Commands[scratch.Frames[slot].Command];
            double distance = WorldDistance(scratch.After[representative].Position, capture.Final[representative].Position, c.Model);
            Assert.True(distance <= .25 * command.Clearance,
                $"{id}, group {g}, weight {command.TransitionWeight:R}, distance {distance:R}, bound {.25 * command.Clearance:R}, slack {capture.SlackCoverage[slot]:R}, correction {scratch.Corrections[g]:R}, exact {capture.Corrections[g]:R}");
            if (scratch.Corrections[g] > 0)
            {
                corrected++;
                if (distance >= maximum) { maximum = distance; worstGroup = g; }
            }
            if (command.TransitionWeight == 1) Assert.Equal(Bits(full[g]), Bits(scratch.After[representative].Position));
        }
        for (int v = 0; v < c.Topology.VertexCount; v++)
        {
            var record = c.Topology.Vertices[v];
            int representative = scratch.ActiveGroupRepresentatives[record.PositionGroup];
            double bound = .25 * c.Commands.Min(command => command.Clearance);
            double distance = WorldDistance(scratch.After[v].Position, capture.Final[v].Position, c.Model);
            Assert.True(distance <= bound, $"{id}, vertex {v}, group {record.PositionGroup}, distance {distance:R}, bound {bound:R}");
            if (representative >= 0) Assert.Equal(Bits(scratch.After[representative].Position), Bits(scratch.After[v].Position));
            if (record.Strength == 0 || record.RegionIndex < 0)
                if (!Bytes(scratch.Before.AsSpan(v, 1)).SequenceEqual(Bytes(scratch.After.AsSpan(v, 1)))) protectedByteChanges++;
        }
        Assert.Equal(0, protectedByteChanges);
        if (CoverageReferenceCases.Spec(id).Weight == 0) Assert.Equal(Bytes(scratch.Before), Bytes(scratch.After));
        var reserved = Pointwise(c);
        Assert.True(CoverageReferenceConstructor.TryCorrectAndTransition(c.Topology, c.Commands, reserved, out failure), failure.ToString());
        double reserveDifference = Enumerable.Range(0, c.Topology.VertexCount)
            .Max(v => WorldDistance(reserved.After[v].Position, scratch.After[v].Position, c.Model));
        output.WriteLine($"FIDELITY {id} corrected={corrected} worstGroup={worstGroup} maxWorldDistance={maximum:R} reserveDifference={reserveDifference:R}");
    }

    [Theory]
    [InlineData(459)]
    [InlineData(463)]
    public void Lifted_group_224_faces_reject_false_contacts_on_exact_side_signs(int face)
    {
        var c = CoverageReferenceCases.Get("cost-limit-l2-lifted-w1");
        var capture = CoverageExactCaptureTests.Read(c.Id);
        var scratch = Pointwise(c, capture);
        var gathers = scratch.ReferenceTopology;
        Assert.Equal(new[] { 459, 463 }, gathers.CoreFaces[gathers.CoreFaceOffsets[224]..gathers.CoreFaceOffsets[225]]);
        Assert.Equal(CoverageTriangleRole.Core, c.Topology.Triangles[face].Role);
        Assert.Equal(1, c.Topology.Triangles[face].RegionIndex);
        var frame = scratch.Frames[scratch.RegionCommands[1]];
        var support = c.Commands[frame.Command].Support.ReferenceData;
        var host = new Vector3[3];
        for (int corner = 0; corner < 3; corner++)
        {
            int v = (int)c.Topology.Indices[face * 3 + corner];
            Assert.Equal(1f, c.Topology.Vertices[v].Strength);
            Vector3 p = scratch.After[v].Position;
            Vector3 actual = CoverageReferenceMath.Point(new CoverageReferenceArithmetic(), p, frame.HostToActualSheet);
            host[corner] = CoverageReferenceMath.Point(new CoverageReferenceArithmetic(), p, frame.HostToCoverage);
            Assert.True(support.TryHeight(new CoverageReferenceArithmetic(), new(actual.X, actual.Y), out _));
            Assert.True(support.TryHeight(new CoverageReferenceArithmetic(), new(host[corner].X, host[corner].Y), out _));
        }
        var triangle = support.Triangles[1];
        Vector3[] sheet = [support.Positions[triangle.A], support.Positions[triangle.B], support.Positions[triangle.C]];
        Assert.True(host.Max(p => p.X) < sheet.Min(p => p.X));
        int touch = face == 459 ? 2 : 1;
        Assert.Equal(-1, CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[touch]).Sign);
        // Both predicates on these same binary32 inputs are negative, not an endpoint touch.
        CoverageAffinePoint Exact(Vector3 p) => CoverageAffinePoint.FromDouble(p.X, p.Y, p.Z);
        Assert.Equal(-1, CoverageGeometry.Edge(Exact(sheet[1]), Exact(sheet[2]), Exact(host[touch])).Sign);
        Assert.True(CoverageFaceOverlay.TryMaximum(host.Select(Exact).ToArray(), sheet.Select(Exact).ToArray(), out double exactPair));
        Assert.Equal(double.NegativeInfinity, exactPair);
        Assert.True(Triangle(sheet).TryOverlayMaximum(new CoverageReferenceArithmetic(), host, out float pair));
        Assert.Equal(float.NegativeInfinity, pair);
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), host, out float maximum));
        Assert.True(maximum + (float)capture.CorrectionClearance[1] < 0);
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure,
            capture.CorrectionClearance, capture.CorrectionConversion), failure.ToString());
        Assert.True(scratch.FaceGuards[face]);
        Assert.Equal(0f, scratch.FaceCorrections[face]);
        Assert.Equal(0d, capture.Corrections[224]);
        Assert.Equal(0f, scratch.Corrections[224]);
        Assert.Equal(0f, scratch.FullRecessions[224]);
        CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, scratch);
        Assert.Equal(Bits(capture.Final[224].Position), Bits(scratch.After[224].Position));
    }

    [Fact]
    public void Float_overlay_rejects_false_endpoint_contact_on_overlapping_nearly_collinear_edges()
    {
        // Group 224's sheet triangle and host corner a. Host b lies near the line from a to (.2,.2), so host edge a-b and
        // sheet edge 1-2 have overlapping XY bounds while a lies outside the sheet edge's bounds.
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        Vector3[] host = [new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.10000000149011612f, 0.10000000894069672f, 0), new(-.2f, .2f, 0)];
        Assert.Equal(-1, CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[0]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(sheet[1], sheet[2], host[1]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(host[0], host[1], sheet[1]).Sign);
        Assert.Equal(-1, CoverageReferencePredicates.Edge(host[0], host[1], sheet[2]).Sign);
        Assert.True(MathF.Max(host[0].X, host[1].X) >= MathF.Min(sheet[1].X, sheet[2].X)
            && MathF.Max(host[0].Y, host[1].Y) >= MathF.Min(sheet[1].Y, sheet[2].Y));
        Assert.True(host[0].X < MathF.Min(sheet[1].X, sheet[2].X));
        CoverageAffinePoint Exact(Vector3 p) => CoverageAffinePoint.FromDouble(p.X, p.Y, p.Z);
        Assert.Equal(-1, CoverageGeometry.Edge(Exact(sheet[1]), Exact(sheet[2]), Exact(host[0])).Sign);
        Assert.Equal(-1, CoverageGeometry.Edge(Exact(host[0]), Exact(host[1]), Exact(sheet[1])).Sign);
        Assert.True(CoverageFaceOverlay.TryMaximum(host.Select(Exact).ToArray(), sheet.Select(Exact).ToArray(), out double exactPair));
        Assert.True(Triangle(sheet).TryOverlayMaximum(new CoverageReferenceArithmetic(), host, out float pair));
        Assert.Equal(exactPair, pair);
        Assert.Equal(float.NegativeInfinity, pair);
    }

    [Fact]
    public void Float_overlay_keeps_the_near_collinear_proper_crossing_at_half_parameter()
    {
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        Vector3[] host = [new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.20000001788139343f, 0.20000000298023224f, 0), new(-.2f, .2f, 0)];
        CoverageAffinePoint Exact(Vector3 p) => CoverageAffinePoint.FromDouble(p.X, p.Y, p.Z);
        var ca = CoverageGeometry.Edge(Exact(sheet[1]), Exact(sheet[2]), Exact(host[0]));
        var cb = CoverageGeometry.Edge(Exact(sheet[1]), Exact(sheet[2]), Exact(host[1]));
        Assert.Equal(-Math.ScaleB(1, -28), ca.Estimate);
        Assert.Equal(Math.ScaleB(1, -28), cb.Estimate);
        Assert.Equal(.5, ca.Estimate / (ca.Estimate - cb.Estimate));
        Assert.True(CoverageFaceOverlay.TryMaximum(host.Select(Exact).ToArray(), sheet.Select(Exact).ToArray(), out double exact));
        Assert.InRange(exact, -.026000001, -.025999998);
        Assert.True(Triangle(sheet).TryOverlayMaximum(new CoverageReferenceArithmetic(), host, out float reference));
        output.WriteLine($"N1 exact={exact:R} reference={reference:R}");
        Assert.InRange(Math.Abs(reference - exact), 0, 1e-8);
    }

    [Fact]
    public void Float_support_rejects_a_rounded_zero_side_with_exact_outside_sign_inside_the_aabb()
    {
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        Vector2 point = new(.0625f, MathF.BitIncrement(.0625f));
        var support = Triangle(sheet);
        Assert.False(support.Support.TryGetHeight(point, out _));
        Assert.False(support.TryHeight(new CoverageReferenceArithmetic(), point, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_strength_core_corner_refuses_folded_patch_only_for_a_positive_deficit(bool positive)
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, level: 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        // The importer refuses partial core strength and the fixture derives roles from strength, so the built topology's
        // records are lowered directly.
        // Surface 24 is the raised centre whose core faces need correction. Surface 18 is a corner of the flat cell (3,2)
        // whose only core face (3,2),(4,2),(4,3) lies far below the plate.
        int surface = positive ? 24 : 18;
        var entry = fixture.Map.Topology.Single(e => e.SurfaceId == surface);
        int sourceVertex = Enumerable.Range(0, fixture.Source.Vertices.Count).Single(i =>
            (fixture.Source.Vertices[i].Mesh, fixture.Source.Vertices[i].Primitive, fixture.Source.Vertices[i].Vertex) == (entry.Mesh, entry.Primitive, entry.Vertex));
        var records = (CoverageRefinedVertex[])typeof(System.Collections.ObjectModel.ReadOnlyCollection<CoverageRefinedVertex>)
            .GetField("list", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(topology.Vertices)!;
        int lowered = Array.FindIndex(records, r => r.SourceVertex == sourceVertex);
        Assert.True(lowered >= 0 && topology.IsCoreVertex(lowered));
        int group = records[lowered].PositionGroup;
        for (int v = 0; v < records.Length; v++)
            if (records[v].PositionGroup == group) records[v] = records[v] with { Strength = .5f };
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        int[] partial = Enumerable.Range(0, topology.TriangleCount).Where(t => topology.Triangles[t].Role == CoverageTriangleRole.Core
            && Enumerable.Range(0, 3).Any(k => topology.Vertices[(int)topology.Indices[t * 3 + k]].Strength != 1)).ToArray();
        Assert.NotEmpty(partial);
        var exactScratch = new CoverageScratch(topology);
        bool exact = CoverageEvaluator.TryConstructCandidate(topology, fixture.Posed, Matrix4x4.Identity, commands,
            exactScratch, new ModelVertex[topology.VertexCount], out var exactFailure);
        var frame = exactScratch.Frames[0];
        Assert.True(CoverageCoreCorrection.TryTerms(exactScratch.Before, Matrix4x4.Identity, commands[frame.Command], frame,
            out double clearance, out _, out double conversion));
        var c = new CoverageReferenceCase("strength", topology, [], [], fixture.Posed, Matrix4x4.Identity, commands);
        // Like-for-like conform and correction terms decide the same faces as the exact path. The reserve's larger
        // clearance may add faces, but never changes the refusal code.
        var scratch = Pointwise(c, [frame.ClearanceCoverage + frame.Slack]);
        bool reference = CoverageReferenceCorrection.TryApply(topology, commands, scratch, out var referenceFailure,
            new[] { clearance }, new[] { conversion });
        var reserved = Pointwise(c);
        bool reserve = CoverageReferenceCorrection.TryApply(topology, commands, reserved, out var reserveFailure);
        output.WriteLine($"STRENGTH positive={positive} partialFaces={partial.Length} exact={exactFailure} reference={referenceFailure} reserve={reserveFailure}");
        if (positive)
        {
            Assert.False(exact);
            Assert.False(reference);
            Assert.False(reserve);
            Assert.Equal(CoverageFailureCode.FoldedPatch, exactFailure.Code);
            Assert.Equal(exactFailure, referenceFailure);
            Assert.Equal(CoverageFailureCode.FoldedPatch, reserveFailure.Code);
        }
        else
        {
            Assert.True(exact, exactFailure.ToString());
            Assert.True(reference, referenceFailure.ToString());
            Assert.True(reserve, reserveFailure.ToString());
            foreach (var result in new[] { scratch, reserved })
            {
                Assert.All(partial, t => Assert.True(result.FaceGuards[t]));
                Assert.All(partial, t => Assert.Equal(0f, result.FaceCorrections[t]));
            }
        }
    }

    [Fact]
    public void Float_overlay_includes_edge_crossings_not_only_corners()
    {
        // The crossing pair from CoverageFaceOverlayTests. No corner is inside the other triangle.
        Vector3[] host = [new(0, 0, .2f), new(2, 0, .3f), new(1, 2, .1f)];
        Vector3[] sheet = [new(0, 1.25f, 0), new(2, 1.25f, .1f), new(1, -.75f, .05f)];
        var support = Triangle(sheet);
        Assert.All(host, p => Assert.False(Inside(sheet, p)));
        Assert.All(sheet, p => Assert.False(Inside(host, p)));
        float cornerOnly = float.NegativeInfinity;
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), host, out float maximum));
        // The sheet is z=.05*x, host z=.2+.05*x-.075*y. The overlap reaches y=0.
        Assert.Equal(.2f, maximum, 6);
        Assert.True(cornerOnly < maximum);
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [host[0], host[2], host[1]], out float reversed));
        Assert.Equal(maximum, reversed, 6);
    }

    [Fact]
    public void Float_overlay_handles_included_corners_collinear_touches_and_invalid_arithmetic()
    {
        var support = Triangle([new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)]);
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [new(0, 0, .25f), new(1, 0, .5f), new(0, 1, .125f)], out float maximum));
        Assert.Equal(.5f, maximum);
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [new(1, 0, .25f), new(3, 0, .75f), new(2, -1, 0)], out maximum));
        Assert.Equal(.5f, maximum);
        Assert.True(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [new(3, 3, 0), new(4, 3, 0), new(3, 4, 0)], out maximum));
        Assert.Equal(float.NegativeInfinity, maximum);
        Assert.False(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [Vector3.Zero, Vector3.One, Vector3.One * 2], out _));
        Assert.False(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [new(0, 0, float.NaN), new(1, 0, 0), new(0, 1, 0)], out _));
        Assert.False(support.TryOverlayMaximum(new CoverageReferenceArithmetic(), [new(-float.MaxValue, 0, 0), new(float.MaxValue, 0, 0), new(0, 1, 0)], out _));
    }

    [Fact]
    public void Correction_is_computed_from_immutable_pointwise_faces()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l3-w05");
        var forward = Pointwise(c); var reverse = Pointwise(c);
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, forward, out var failure), failure.ToString());
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, reverse, out failure,
            default, default, reverseGroupsForTests: true), failure.ToString());
        Assert.Equal(forward.FaceCorrections, reverse.FaceCorrections);
        Assert.Equal(forward.Corrections, reverse.Corrections);
        Assert.Equal(forward.FullRecessions, reverse.FullRecessions);
        Assert.Equal(Bytes(forward.After), Bytes(reverse.After));
        CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, forward);
        CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, reverse);
        Assert.Equal(Bytes(forward.After), Bytes(reverse.After));
    }

    [Theory]
    [InlineData(1f, .0625f)]
    [InlineData(.00000095367431640625f, 65536f)]
    public void Correction_uses_reserved_actual_clearance_without_extra_bias(float rate, float expectedCorrection)
    {
        var (c, scratch) = Flat();
        int vertex = Enumerable.Range(0, c.Topology.VertexCount).First(c.Topology.IsCoreVertex);
        int group = c.Topology.Vertices[vertex].PositionGroup;
        // Flat pointwise faces sit at -.0390625. Reserved actual clearance .1015625 leaves exactly .0625.
        scratch.Frames[0] = scratch.Frames[0] with { ActualClearanceCoverage = .1015625f, CorrectionRate = rate };
        c.Commands[0] = c.Commands[0] with { MinZ = -100000, MaxWorldRecession = 100000 };
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure), failure.ToString());
        Assert.Equal(Bits(expectedCorrection), Bits(scratch.Corrections[group]));
        Assert.Equal(Bits(.0390625f + expectedCorrection), Bits(scratch.FullRecessions[group]));
        Assert.Equal(Bits(-(.0390625f + expectedCorrection)), Bits(scratch.After[vertex].Position.Z));
    }

    [Fact]
    public void Like_for_like_correction_adds_captured_conversion_before_its_own_rate_division()
    {
        var (c, scratch) = Flat();
        scratch.Frames[0] = scratch.Frames[0] with { ActualClearanceCoverage = 1, CorrectionRate = .5f };
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure,
            new[] { .1015625 }, new[] { .03125 }), failure.ToString());
        Assert.Contains(scratch.Corrections, x => x == .1875f);
        Assert.DoesNotContain(scratch.Corrections, x => x == 0.125f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    [InlineData(1f)]
    public void Transition_uses_the_full_scalar_once_and_copies_zero_and_full_bits(float weight)
    {
        var (c, scratch) = Flat();
        c.Commands[0] = c.Commands[0] with { TransitionWeight = weight };
        int v = Enumerable.Range(0, c.Topology.VertexCount).First(c.Topology.IsCoreVertex);
        int g = c.Topology.Vertices[v].PositionGroup;
        scratch.Before[v].Position.Z = .10000001f;
        scratch.FullRecessions[g] = .10000002f;
        scratch.GroupPositions[g] = new(0, 0, -.000000007450581f);
        CoverageReferenceCorrection.ApplyTransition(c.Topology, c.Commands, scratch);
        float expected = weight == 0 ? .10000001f : weight == 1 ? -.000000007450581f : .05f;
        Assert.Equal(Bits(expected), Bits(scratch.After[v].Position.Z));
        Assert.Equal(Bits(weight == 0 ? .10000001f : weight == 1 ? -.000000007450581f : .05f), Bits(scratch.GroupPositions[g].Z));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Correction_full_target_guards_run_even_at_zero_weight(bool cap)
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l3-w0");
        var scratch = Pointwise(c);
        float point = scratch.FullRecessions.Max();
        Assert.True(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure), failure.ToString());
        float full = scratch.FullRecessions.Max();
        Assert.True(full > point);
        scratch = Pointwise(c);
        var commands = c.Commands.ToArray();
        commands[0] = cap ? commands[0] with { MinZ = 0, MaxZ = .1f } : commands[0] with { MaxWorldRecession = (point + full) * .5f };
        Assert.False(CoverageReferenceCorrection.TryApply(c.Topology, commands, scratch, out failure));
        Assert.Equal(cap ? CoverageFailureCode.CapContact : CoverageFailureCode.RecessionExceeded, failure.Code);
        Assert.All(scratch.Published, v => Assert.Equal(default, v));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Nonpositive_or_nonfinite_correction_rate_refuses(float rate)
    {
        var (c, scratch) = Flat();
        scratch.Frames[0] = scratch.Frames[0] with { CorrectionRate = rate };
        Assert.False(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_and_nominal_core_membership_are_required(bool actual)
    {
        var (c, scratch) = Flat();
        var frame = scratch.Frames[0];
        scratch.Frames[0] = actual ? frame with { HostToActualSheet = Matrix4x4.CreateTranslation(10, 0, 0) }
            : frame with { HostToCoverage = Matrix4x4.CreateTranslation(10, 0, 0) };
        Assert.False(CoverageReferenceCorrection.TryApply(c.Topology, c.Commands, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.CoreEscaped, failure.Code);
    }

    [Fact]
    public void Face_failures_are_command_id_ordered_then_full_groups_are_representative_ordered()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        commands[0] = commands[0] with { Id = 20 };
        commands[1] = commands[1] with { Id = 10 };
        var c = new CoverageReferenceCase("ordered", topology, [], [], fixture.Posed, Matrix4x4.Identity, commands);
        CoverageFailure Run(bool face)
        {
            var s = Pointwise(c);
            if (face)
                for (int slot = 0; slot < 2; slot++) s.Frames[slot] = s.Frames[slot] with { CorrectionRate = 0 };
            else
                for (int i = 0; i < commands.Length; i++) commands[i] = commands[i] with { MaxWorldRecession = 0 };
            Assert.False(CoverageReferenceCorrection.TryApply(topology, commands, s, out var failure));
            return failure;
        }
        var first = Run(true);
        Assert.Equal(10u, first.CommandId);
        Assert.Equal(topology.Triangles.Count(t => t.Role == CoverageTriangleRole.Core && t.RegionIndex == 1), first.Count);
        Array.Reverse(commands);
        Assert.Equal(first, Run(true));
        var groups = Run(false);
        Assert.Equal(20u, groups.CommandId);
        Assert.Equal(CoverageFailureCode.RecessionExceeded, groups.Code);
    }

    [Fact]
    public void Warm_correction_and_transition_allocate_no_managed_memory()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l3-w05");
        var scratch = Pointwise(c);
        Assert.True(CoverageReferenceConstructor.TryCorrectAndTransition(c.Topology, c.Commands, scratch, out _));
        scratch = Pointwise(c);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = CoverageReferenceConstructor.TryCorrectAndTransition(c.Topology, c.Commands, scratch, out _);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(accepted);
        Assert.Equal(0, bytes);
    }

    static CoverageReferenceScratch Pointwise(CoverageReferenceCase c, CoverageExactCapture? capture = null) =>
        Pointwise(c, capture is null ? [] : capture.ClearanceCoverage.Zip(capture.SlackCoverage, (a, b) => a + b).ToArray());

    static CoverageReferenceScratch Pointwise(CoverageReferenceCase c, double[] target)
    {
        var scratch = new CoverageReferenceScratch(c.Topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(c.Topology, c.Model, c.Commands, scratch, out var failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryInterpolate(c.Topology, c.Posed, scratch, out failure), failure.ToString());
        Assert.True(CoverageReferenceConstructor.TryConformGroups(c.Topology, c.Commands, scratch, out failure, target), failure.ToString());
        return scratch;
    }

    static (CoverageReferenceCase Case, CoverageReferenceScratch Scratch) Flat()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 2);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var points = fixture.RigidPositions.Select((p, i) => new Vector3(p.X, p.Y, i < 5 ? 0 : .2f)).ToArray();
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("correction-flat", points, fixture.RigidIndices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        commands[0] = commands[0] with { Clearance = .03125f };
        var posed = fixture.Posed.Select(v => { v.Position.Z = 0; return v; }).ToArray();
        var c = new CoverageReferenceCase("flat", topology, [], [], posed, Matrix4x4.Identity, commands);
        return (c, Pointwise(c));
    }

    static KhaozEngine.Render3D.CoverageReferenceSupport Triangle(Vector3[] p) =>
        CoverageSupportSurface.Compile(CoverageCommands.Solid("overlay-triangle", p, [0, 1, 2]), [new(0, 0, 0, 1)], Matrix4x4.Identity).ReferenceData;

    static bool Inside(Vector3[] t, Vector3 p)
    {
        double Edge(Vector3 a, Vector3 b) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);
        double a = Edge(t[0], t[1]), b = Edge(t[1], t[2]), c = Edge(t[2], t[0]);
        return a >= 0 && b >= 0 && c >= 0 || a <= 0 && b <= 0 && c <= 0;
    }

    static double WorldDistance(Vector3 a, Vector3 b, Matrix4x4 m)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        double wx = x * m.M11 + y * m.M21 + z * m.M31, wy = x * m.M12 + y * m.M22 + z * m.M32,
            wz = x * m.M13 + y * m.M23 + z * m.M33;
        return Math.Sqrt(wx * wx + wy * wy + wz * wz);
    }

    static int Bits(float f) => BitConverter.SingleToInt32Bits(f);
    static int[] Bits(Vector3 p) => [Bits(p.X), Bits(p.Y), Bits(p.Z)];
    static byte[] Bytes(Span<ModelVertex> v) => MemoryMarshal.AsBytes(v).ToArray();
}
