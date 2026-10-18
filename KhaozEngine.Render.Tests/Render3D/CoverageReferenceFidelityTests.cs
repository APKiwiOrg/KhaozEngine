using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceFidelityTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases => CoverageExactCaptureTests.Cases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_reference_position_is_within_exact_quarter_clearance(string id)
    {
        var c = CoverageReferenceCases.Get(id);
        var capture = CoverageExactCaptureTests.Read(id);
        var scratch = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        double[] target = capture.ClearanceCoverage.Zip(capture.SlackCoverage, (a, b) => a + b).ToArray();
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, final, out var failure,
            target, capture.CorrectionClearance, capture.CorrectionConversion), $"{id}: {failure}");
        var reserve = new CoverageReferenceScratch(c.Topology);
        var reserved = new ModelVertex[final.Length];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, reserve, reserved, out failure), $"{id}: {failure}");
        double worst = -1, worstBound = 0, maxReserve = 0;
        int worstVertex = -1, protectedByteChanges = 0, positionWeldBitDifferences = 0;
        uint[] indices = c.Topology.Indices.ToArray();
        float worstNormal = -1, worstTangent = -1;
        int worstNormalVertex = -1, worstTangentVertex = -1, displacedVertices = 0;
        for (int v = 0; v < final.Length; v++)
        {
            var record = c.Topology.Vertices[v];
            int representative = scratch.ActiveGroupRepresentatives[record.PositionGroup];
            int slot = representative < 0 ? -1 : scratch.RegionCommands[c.Topology.Vertices[representative].RegionIndex];
            var command = c.Commands[slot < 0 ? 0 : scratch.Frames[slot].Command];
            double bound = .25 * command.Clearance;
            double distance = WorldDistance(final[v].Position, capture.Final[v].Position, c.Model);
            Assert.True(distance <= bound, $"{id}, vertex {v}, group {record.PositionGroup}, transition {command.TransitionWeight:R}, distance {distance:R}, bound {bound:R}, slack {(slot < 0 ? 0 : capture.SlackCoverage[slot]):R}, correction {scratch.Corrections[record.PositionGroup]:R}, exactCorrection {capture.Corrections[record.PositionGroup]:R}");
            if (distance > worst) { worst = distance; worstVertex = v; worstBound = bound; }
            if (representative >= 0 && !Bits(final[v].Position).SequenceEqual(Bits(final[representative].Position))) positionWeldBitDifferences++;
            if ((record.Strength == 0 || record.RegionIndex < 0 || slot < 0 || scratch.Before[v].Position == final[v].Position)
                && !Bytes(scratch.Before.AsSpan(v, 1)).SequenceEqual(Bytes(final.AsSpan(v, 1)))) protectedByteChanges++;
            if (scratch.Before[v].Position != final[v].Position)
            {
                displacedVertices++;
                // Join seam corners only for the independent normal oracle. Tangents stay indexed.
                uint[] shading = indices.Select(i => c.Topology.Vertices[(int)i].ShadingGroup == record.ShadingGroup ? (uint)v : i).ToArray();
                Vector3 expectedNormal = CoverageGeometryOracle.ExpectedNormal(shading, scratch.Before, scratch.After, v);
                Vector3 expectedTangent = CoverageGeometryOracle.ExpectedTangent(indices, scratch.Before, scratch.After, v);
                Vector3 actualTangent = new(final[v].Tangent.X, final[v].Tangent.Y, final[v].Tangent.Z);
                float normalDistance = Vector3.Distance(expectedNormal, final[v].Normal);
                float tangentDistance = Vector3.Distance(expectedTangent, actualTangent);
                Assert.True(normalDistance < 2e-5f, $"{id}, vertex {v}, normal distance {normalDistance:R}, expected {expectedNormal}, actual {final[v].Normal}");
                Assert.True(tangentDistance < 2e-5f, $"{id}, vertex {v}, tangent distance {tangentDistance:R}, expected {expectedTangent}, actual {actualTangent}");
                Assert.Equal(scratch.Before[v].Tangent.W, final[v].Tangent.W);
                if (normalDistance > worstNormal) { worstNormal = normalDistance; worstNormalVertex = v; }
                if (tangentDistance > worstTangent) { worstTangent = tangentDistance; worstTangentVertex = v; }
            }
            double reserveShift = WorldDistance(reserved[v].Position, final[v].Position, c.Model);
            maxReserve = Math.Max(maxReserve, reserveShift);
            if (slot < 0 || scratch.Corrections[record.PositionGroup] > 0 || reserve.Corrections[record.PositionGroup] > 0) continue;
            var frame = scratch.Frames[slot];
            Vector3 q = CoverageReferenceMath.Point(scratch.Before[v].Position, frame.HostToCoverage);
            float spatial = 1;
            var support = command.Support.ReferenceData;
            if (!support.TryHeight(new(q.X, q.Y), out _))
            {
                Assert.True(support.TryBoundary(new(q.X, q.Y), out _, out float boundaryDistance));
                float u = Math.Clamp(1 - (boundaryDistance / command.CollarWidth), 0, 1);
                spatial = (u * u) * (3 - (2 * u));
            }
            bool core = scratch.ReferenceTopology.CoreFaceOffsets[record.PositionGroup] != scratch.ReferenceTopology.CoreFaceOffsets[record.PositionGroup + 1];
            double alpha = (core ? 1 : c.Topology.Vertices[representative].Strength) * (double)spatial * command.TransitionWeight;
            double mapping = reserve.Frames[slot].ClearanceCoverage / (double)(command.Clearance + (command.Clearance * .25f));
            double worldScale = WorldDistance(frame.Recess, Vector3.Zero, c.Model);
            double mappedReserveBound = Math.BitIncrement(alpha * Math.Abs((command.Clearance * .25) * mapping - capture.SlackCoverage[slot]) * worldScale) + bound;
            Assert.True(reserveShift <= mappedReserveBound, $"{id}, group {record.PositionGroup}, reserve {reserveShift:R}, mappedBound {mappedReserveBound:R}");
        }
        Assert.Equal(0, protectedByteChanges);
        Assert.Equal(0, positionWeldBitDifferences);
        output.WriteLine($"FIDELITY {id} worstWorldDistance={worst:R} vertex={worstVertex} group={c.Topology.Vertices[worstVertex].PositionGroup} bound={worstBound:R} reserveDifference={maxReserve:R} correctedGroups={scratch.Corrections.Count(c => c > 0)}");
        output.WriteLine($"ATTRIBUTES {id} displacedVertices={displacedVertices} worstNormalDistance={worstNormal:R} normalVertex={worstNormalVertex} worstTangentDistance={worstTangent:R} tangentVertex={worstTangentVertex}");
    }

    [Fact]
    public void Reserve_can_consume_the_entire_fidelity_budget()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var points = fixture.RigidPositions.Select((p, i) => new Vector3(p.X, p.Y, i < 5 ? 0 : .2f)).ToArray();
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("reference-flat-budget", points, fixture.RigidIndices),
            [new(0, 0, 0, 4)], Matrix4x4.Identity);
        var commands = CoverageCommands.Poses(fixture, topology, support, Matrix4x4.Identity);
        commands[0] = commands[0] with { Clearance = .03125f };
        var posed = fixture.Posed.ToArray();
        for (int i = 0; i < posed.Length; i++) posed[i].Position = new(fixture.Source.Positions[i].X, fixture.Source.Positions[i].Y, 0);
        var reserved = new ModelVertex[topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(topology, posed, Matrix4x4.Identity, commands, new(topology), reserved, out var failure), failure.ToString());
        int core = Enumerable.Range(0, topology.VertexCount).First(topology.IsCoreVertex);
        // The independent flat unit-frame, zero-slack comparator is z = -clearance.
        Assert.Equal(.0078125, Math.Abs((double)reserved[core].Position.Z + .03125));
        Assert.Equal(.25 * .03125, Math.Abs((double)reserved[core].Position.Z + .03125));
    }

    static double WorldDistance(Vector3 a, Vector3 b, Matrix4x4 model)
    {
        double x = (double)a.X - b.X, y = (double)a.Y - b.Y, z = (double)a.Z - b.Z;
        double wx = ((x * model.M11) + (y * model.M21)) + (z * model.M31);
        double wy = ((x * model.M12) + (y * model.M22)) + (z * model.M32);
        double wz = ((x * model.M13) + (y * model.M23)) + (z * model.M33);
        return Math.Sqrt(((wx * wx) + (wy * wy)) + (wz * wz));
    }

    static int[] Bits(Vector3 p) => [BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z)];
    static byte[] Bytes(Span<ModelVertex> vertices) => MemoryMarshal.AsBytes(vertices).ToArray();
}
