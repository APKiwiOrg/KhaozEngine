using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageRefinementTests
{
    [Fact]
    public void Refinement_interpolates_posed_triangles()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var output = new ModelVertex[topology.VertexCount];
        topology.Interpolate(fixture.Posed, output);
        bool negativeControl = false;
        for (int i = 0; i < output.Length; i++)
        {
            var v = topology.Vertices[i];
            if (v.SourceVertex >= 0)
            {
                Assert.Equal(fixture.Posed[v.SourceVertex], output[i]);
                continue;
            }
            var t = fixture.Source.Triangles[v.SourceTriangle];
            var expected = (fixture.Posed[t.A].Position * v.A + fixture.Posed[t.B].Position * v.B + fixture.Posed[t.C].Position * v.C) / v.Denominator;
            Assert.True(Vector3.Distance(expected, output[i].Position) < 3e-7f);
            var a = fixture.Source.SkinVertices[t.A]; var b = fixture.Source.SkinVertices[t.B]; var c = fixture.Source.SkinVertices[t.C];
            var wrong = a;
            wrong.Position = (a.Position * v.A + b.Position * v.B + c.Position * v.C) / v.Denominator;
            wrong.BoneWeights = (a.BoneWeights * v.A + b.BoneWeights * v.B + c.BoneWeights * v.C) / v.Denominator;
            if (Vector3.Distance(SkinningMath.SkinVertex(wrong, CoverageFixture.Pose).Position, output[i].Position) > 1e-5f) negativeControl = true;
        }
        Assert.True(negativeControl);
        var oracle = CoverageGeometryOracle.Validate(output, output, fixture with
        {
            SurfaceIndices = topology.Indices.ToArray(),
            ProtectedVertices = topology.Vertices.Select(v => v.Strength == 0).ToArray(),
        });
        Assert.Equal(0, oracle.UnmatchedInteriorEdges);
        Assert.Equal(0, oracle.ProtectedPositionChanges);
        Assert.Equal(0, oracle.Folds);
    }

    [Fact]
    public void Material_seams_share_rational_splits()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var declarations = fixture.Map.Refinement.Select(r => r with { Level = r.Triangles.Primitive == 0 ? 3 : 4 });
        var map = new HostCoverageMap(CoverageFixture.Hash, fixture.Map.Topology, fixture.Map.Regions, declarations, fixture.Map.Authoring);
        var topology = CoverageRefinement.Build(fixture.Source, map);
        var output = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, output);
        var oracle = CoverageGeometryOracle.Validate(output, output, fixture, topology.Indices.ToArray(), topology.Vertices.Select(v => v.Strength == 0).ToArray());
        Assert.Equal(0, oracle.UnmatchedInteriorEdges);
        Assert.Equal(0, oracle.WeldedPositionChanges);
        Assert.Contains(topology.EdgeSplits, s => s.Numerator == 1 && s.Denominator == 3);
        Assert.Contains(topology.EdgeSplits, s => s.Numerator == 1 && s.Denominator == 4);
        Assert.Equal(2, topology.MaterialRanges.Count);
        Assert.True(topology.TriangleCount < fixture.Source.Triangles.Count * 16);
        Assert.All(topology.Indices.ToArray(), i => Assert.True(i < topology.VertexCount));
    }

    [Fact]
    public void Level_four_has_sixteen_children_and_only_propagates_edge_splits()
    {
        var fixture = CoverageFixture.ClosedEllipsoid();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        Assert.Equal(16, topology.Triangles.Count(t => t.SourceTriangle == 0));
        Assert.Contains(topology.Triangles.GroupBy(t => t.SourceTriangle), g => g.Count() == 1);
        var output = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, output);
        var oracle = CoverageGeometryOracle.Validate(output, output, fixture, topology.Indices.ToArray(), topology.Vertices.Select(v => v.Strength == 0).ToArray());
        Assert.Equal(0, oracle.UnmatchedInteriorEdges);
        Assert.Equal(0, oracle.NonadjacentIntersections);
    }

    [Fact]
    public void Invalid_programmatic_host_is_validated_before_refinement()
    {
        var fixture = CoverageFixture.CurvedPanel(false);
        var bad = new HostCoverageMap(CoverageFixture.Hash, fixture.Map.Topology,
            [new("r0", fixture.Map.Regions[0].Triangles, fixture.Map.Regions[0].Strengths.Select(s => s with { Strength = 1 }))], [], fixture.Map.Authoring);
        Assert.Throws<InvalidDataException>(() => CoverageRefinement.Build(fixture.Source, bad));
    }

    [Fact]
    public void Posed_alias_disagreement_refuses_before_writing()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var alias = topology.SourceAliases[0];
        fixture.Posed[alias.SourceVertex].Position.X = MathF.BitIncrement(fixture.Posed[alias.SourceVertex].Position.X);
        var output = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.One, Vector4.One), topology.VertexCount).ToArray();
        byte[] expected = MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
        Assert.False(topology.TryInterpolate(fixture.Posed, output, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(expected, MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
    }

    [Fact]
    public void Permuted_raw_influences_are_valid_at_load_but_can_refuse_the_actual_pose()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var initial = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var alias = initial.SourceAliases[0];
        var skin = fixture.Source.SkinVertices.ToArray(); var raw = fixture.Source.Influences.ToArray();
        raw[alias.Representative] = new(new(0, 1, 2, 3), new(1, 2e-8f, 2e-8f, 2e-8f));
        raw[alias.SourceVertex] = new(new(1, 2, 3, 0), new(2e-8f, 2e-8f, 2e-8f, 1));
        foreach (int i in new[] { alias.SourceVertex, alias.Representative })
        {
            skin[i].BoneIndices = raw[i].Joints; skin[i].BoneWeights = SkinningMath.NormalizeWeights(raw[i].Weights);
        }
        var source = new CoverageSourceTopology("permuted-host", CoverageFixture.Hash, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, skin, raw);
        var topology = CoverageRefinement.Build(source, fixture.Map);
        Matrix4x4[] pose = [Matrix4x4.CreateScale(2, 3, 5), Matrix4x4.CreateRotationX(.3f),
            Matrix4x4.CreateTranslation(1, 2, 3), Matrix4x4.CreateScale(.5f, .7f, .9f)];
        var posed = skin.Select(v => SkinningMath.SkinVertex(v, pose)).ToArray();
        Assert.NotEqual(posed[alias.SourceVertex].Position, posed[alias.Representative].Position);
        var destination = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.One, Vector4.One), topology.VertexCount).ToArray();
        byte[] sentinel = MemoryMarshal.AsBytes(destination.AsSpan()).ToArray();
        Assert.False(topology.TryInterpolate(posed, destination, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
        Assert.Equal(sentinel, MemoryMarshal.AsBytes(destination.AsSpan()).ToArray());
    }

    [Fact]
    public void Nonfinite_pose_refuses_before_destination_writes()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        fixture.Posed[^1].Normal.X = float.NaN;
        var output = new ModelVertex[topology.VertexCount];
        var sentinel = MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
        Assert.False(topology.TryInterpolate(fixture.Posed, output, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(sentinel, MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
    }

    [Fact]
    public void Interpolation_destination_is_the_exact_active_span()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var storage = new ModelVertex[topology.VertexCount + 1];
        Assert.Throws<ArgumentException>(() => { topology.TryInterpolate(fixture.Posed, storage, out _); });
        Assert.Throws<ArgumentException>(() => { topology.TryInterpolate(fixture.Posed, storage.AsSpan(0, topology.VertexCount - 1), out _); });
        Assert.Equal(new ModelVertex[storage.Length], storage);
        // Capacity-owned callers slice their active prefix.
        Assert.True(topology.TryInterpolate(fixture.Posed, storage.AsSpan(0, topology.VertexCount), out _));
    }

    [Fact]
    public void Tiny_generated_directions_normalize_finitely()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        // Squared lengths underflow float, so only exact-weight arithmetic keeps these nonzero directions.
        foreach (ref var v in fixture.Posed.AsSpan()) { v.Normal = new(1e-30f, 2e-30f, 0); v.Tangent = new(0, 3e-38f, 4e-38f, 1); }
        var output = new ModelVertex[topology.VertexCount];
        Assert.True(topology.TryInterpolate(fixture.Posed, output, out _));
        var normal = Vector3.Normalize(new(1, 2, 0)); var tangent = new Vector4(0, .6f, .8f, 1);
        foreach (int i in Generated(topology))
        {
            Assert.True(Vector3.Distance(normal, output[i].Normal) < 2e-7f, $"normal {i}: {output[i].Normal}");
            Assert.True(Vector4.Distance(tangent, output[i].Tangent) < 2e-7f, $"tangent {i}: {output[i].Tangent}");
        }
    }

    [Fact]
    public void Cancelled_generated_normal_or_tangent_refuses_untouched()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        // The two equally weighted corners of one generated edge midpoint.
        var midpoint = topology.Vertices.First(v => v.SourceVertex < 0 && new[] { v.A, v.B, v.C }.Count(w => w == 6) == 2);
        var triangle = fixture.Source.Triangles[midpoint.SourceTriangle];
        int[] ends = new[] { (triangle.A, midpoint.A), (triangle.B, midpoint.B), (triangle.C, midpoint.C) }.Where(p => p.Item2 == 6).Select(p => p.Item1).ToArray();
        var normals = fixture.Posed.ToArray();
        normals[ends[0]].Normal = Vector3.UnitY; normals[ends[1]].Normal = -Vector3.UnitY;
        var tangents = fixture.Posed.ToArray();
        tangents[ends[0]].Tangent = new(0, 0, 1, -1); tangents[ends[1]].Tangent = new(0, 0, -1, -1);
        foreach (var posed in new[] { normals, tangents })
        {
            var output = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.One, Vector4.One), topology.VertexCount).ToArray();
            var sentinel = MemoryMarshal.AsBytes(output.AsSpan()).ToArray();
            Assert.False(topology.TryInterpolate(posed, output, out var failure));
            Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
            Assert.Equal(sentinel, MemoryMarshal.AsBytes(output.AsSpan()).ToArray());
        }
    }

    [Fact]
    public void Absent_generated_normals_and_tangents_stay_zero()
    {
        var fixture = CoverageFixture.CurvedPanel(); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        foreach (ref var v in fixture.Posed.AsSpan()) { v.Normal = Vector3.Zero; v.Tangent = Vector4.Zero; }
        var output = new ModelVertex[topology.VertexCount];
        Assert.True(topology.TryInterpolate(fixture.Posed, output, out _));
        Assert.All(output, v => { Assert.Equal(Vector3.Zero, v.Normal); Assert.Equal(Vector4.Zero, v.Tangent); });
    }

    [Fact]
    public void Extreme_finite_attributes_interpolate_finitely()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 3); var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        float max = float.MaxValue;
        foreach (ref var v in fixture.Posed.AsSpan())
        {
            // Opposite extremes across one edge, and extreme same-sign attributes at every corner.
            v.Position.X = v.Position.X < -.1f ? -3e38f : 3e38f;
            v.Normal = new(max, max, 0); v.Tangent = new(0, max, max, -max);
            v.Color = new(max, -max, max, max); v.Uv = new(max, -max);
        }
        var output = new ModelVertex[topology.VertexCount];
        Assert.True(topology.TryInterpolate(fixture.Posed, output, out _));
        Assert.All(output, v => Assert.True(float.IsFinite(v.Position.X) && float.IsFinite(v.Position.Y) && float.IsFinite(v.Position.Z), $"{v.Position}"));
        var normal = Vector3.Normalize(new(1, 1, 0)); var tangent = new Vector4(Vector3.Normalize(new(0, 1, 1)), -1);
        foreach (int i in Generated(topology))
        {
            Assert.True(Vector3.Distance(normal, output[i].Normal) < 2e-7f, $"normal {i}: {output[i].Normal}");
            Assert.True(Vector4.Distance(tangent, output[i].Tangent) < 2e-7f, $"tangent {i}: {output[i].Tangent}");
            Assert.Equal(new Vector4(max, -max, max, max), output[i].Color); Assert.Equal(new Vector2(max, -max), output[i].Uv);
        }
    }

    static int[] Generated(CoverageRefinement topology) =>
        Enumerable.Range(0, topology.VertexCount).Where(i => topology.Vertices[i].SourceVertex < 0).ToArray();

    [Fact]
    public void Independent_oracle_checks_full_triangle_clearance_and_intersections()
    {
        var fixture = CoverageFixture.CurvedPanel(false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var indices = topology.Indices.ToArray(); var protect = topology.Vertices.Select(v => v.Strength == 0).ToArray();
        var baseline = CoverageGeometryOracle.Validate(before, before, fixture, indices, protect);
        Assert.True(baseline.ClearanceViolations > 0); Assert.True(baseline.MinimumClearance < 0);
        Assert.True(baseline.EquipmentIntersections > 0);
        var after = before.ToArray();
        foreach (ref var v in after.AsSpan()) v.Position.Z -= .4f;
        var cleared = CoverageGeometryOracle.Validate(before, after, fixture, indices, protect);
        Assert.Equal(0, cleared.ClearanceViolations); Assert.True(cleared.CoreRecession > 0);
        Assert.Equal(0, cleared.EquipmentIntersections); Assert.True(cleared.MinimumEquipmentDistance > fixture.Commands[0].Clearance);
        var escaped = after.ToArray();
        foreach (ref var v in escaped.AsSpan()) v.Position.X += 10;
        Assert.True(CoverageGeometryOracle.Validate(before, escaped, fixture, indices, protect).ClearanceViolations > 0);
        // Two nonadjacent triangles penetrate through their interiors.
        var triangles = new ModelVertex[] {
            new(new(-1, -1, 0), Vector3.UnitZ, Vector4.One), new(new(1, -1, 0), Vector3.UnitZ, Vector4.One), new(new(0, 1, 0), Vector3.UnitZ, Vector4.One),
            new(new(0, 0, -1), Vector3.UnitX, Vector4.One), new(new(0, -.5f, 1), Vector3.UnitX, Vector4.One), new(new(0, .5f, 1), Vector3.UnitX, Vector4.One) };
        Assert.Equal(1, CoverageGeometryOracle.Validate(triangles, triangles, fixture, [0, 1, 2, 3, 4, 5], new bool[6]).NonadjacentIntersections);
    }

    [Fact]
    public void Synthetic_variants_compile_and_keep_actor_buffers_independent()
    {
        var fixture = CoverageFixture.CurvedPanel(lowerBulge: true, crossingSegment: true, secondRegion: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var actor = fixture.SecondActor();
        var first = new ModelVertex[topology.VertexCount]; var second = new ModelVertex[topology.VertexCount];
        topology.Interpolate(fixture.Posed, first); topology.Interpolate(actor.Posed, second);
        Assert.Equal(2, fixture.Commands.Length);
        Assert.Contains(topology.Vertices, v => v.RegionIndex == 1 && v.Strength > 0);
        Assert.NotEqual(first[0].Position, second[0].Position);
        Assert.Contains(topology.Triangles, t => fixture.Source.Triangles[t.SourceTriangle].Mesh == 1 && t.RegionIndex == -1);
    }

    [Fact]
    public void Base_fixture_has_a_protected_retaining_clearance_candidate()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        Assert.Equal(116, topology.VertexCount); Assert.Equal(184, topology.TriangleCount);
        Assert.Contains(topology.Vertices, v => v.Strength > 0 && v.Strength < 1);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var candidate = before.ToArray();
        // Fixed analytic witness for this synthetic fixture, not the runtime conformation algorithm.
        foreach (ref var vertex in candidate.AsSpan())
            if (vertex.Position.Z > -.05f) vertex.Position.Z = -.05f;
        var result = CoverageGeometryOracle.Validate(before, candidate, fixture, topology.Indices.ToArray(), new bool[before.Length]);
        Assert.Equal(0, result.ProtectedPositionChanges);
        Assert.Equal(0, result.ClearanceViolations); Assert.Equal(0, result.EquipmentIntersections);
        Assert.Equal(0, result.NonadjacentIntersections); Assert.Equal(0, result.Folds);
        Assert.Equal(0, result.UnmatchedInteriorEdges); Assert.Equal(0, result.WeldedPositionChanges);
        Assert.True(result.MinimumEquipmentDistance >= .069999f); Assert.True(result.CoreRecession > 0);
        for (int i = 0; i < candidate.Length; i++)
        {
            Assert.True(Vector3.Distance(before[i].Position, candidate[i].Position) <= fixture.Commands[0].MaxWorldRecession);
            if (topology.Vertices[i].Strength < 1) Assert.Equal(before[i], candidate[i]);
        }
        foreach (var record in topology.Vertices.Where(v => v.SourceVertex >= 0 && v.Strength < 1))
            Assert.True(fixture.Posed[record.SourceVertex].Position.Z <= -.18f);
        var challenging = CoverageFixture.ChallengingCollar();
        var difficult = CoverageRefinement.Build(challenging.Source, challenging.Map);
        var shallow = new ModelVertex[difficult.VertexCount]; difficult.Interpolate(challenging.Posed, shallow);
        var shifted = shallow.ToArray();
        foreach (ref var v in shifted.AsSpan()) if (v.Position.Z > -.05f) v.Position.Z = -.05f;
        Assert.True(CoverageGeometryOracle.Validate(shallow, shifted, challenging, difficult.Indices.ToArray(), new bool[shallow.Length]).ProtectedPositionChanges > 0);
    }

    [Theory]
    [InlineData(false, 65536)]
    [InlineData(false, 65537)]
    [InlineData(true, 65536)]
    [InlineData(true, 65537)]
    public void Final_capacity_is_checked_for_each_total(bool vertices, int total)
    {
        var fixture = CoverageFixture.CurvedPanel(false, crossingSegment: true);
        var compiled = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var positions = fixture.Source.Positions.ToArray().ToList();
        var skin = fixture.Source.SkinVertices.ToArray().ToList();
        var provenance = fixture.Source.Vertices.ToList();
        var triangles = fixture.Source.Triangles.ToList();
        var entries = fixture.Map.Topology.ToList();
        if (vertices)
        {
            int extra = total - compiled.VertexCount;
            for (int i = 0; i < extra; i++)
            {
                positions.Add(Vector3.Zero); skin.Add(default);
                provenance.Add(new(2, 0, i, -1, 2, i)); entries.Add(new(2, 0, i, 1000 + i));
            }
        }
        else
        {
            var t = triangles[^1];
            int extra = total - compiled.TriangleCount;
            for (int i = 0; i < extra; i++) triangles.Add(t with { Triangle = i + 1 });
        }
        var source = new CoverageSourceTopology("capacity-host", CoverageFixture.Hash, positions.ToArray(), provenance, triangles, skin.ToArray());
        var map = new HostCoverageMap(CoverageFixture.Hash, entries, fixture.Map.Regions, fixture.Map.Refinement, fixture.Map.Authoring);
        if (total == 65536)
        {
            var result = CoverageRefinement.Build(source, map);
            Assert.Equal(total, vertices ? result.VertexCount : result.TriangleCount);
            Assert.All(result.Indices.ToArray(), i => Assert.True(i < result.VertexCount));
        }
        else Assert.Contains("65537", Assert.Throws<InvalidDataException>(() => CoverageRefinement.Build(source, map)).Message);
    }

    [Fact]
    public void Interpolation_rejects_overlapping_buffers_and_preserves_midpoint_exactly()
    {
        var fixture = CoverageFixture.CurvedPanel(false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var storage = new ModelVertex[topology.VertexCount]; fixture.Posed.CopyTo(storage, 0);
        Assert.Throws<ArgumentException>(() => { topology.Interpolate(storage.AsSpan(0, fixture.Posed.Length), storage); });
        topology.Interpolate(fixture.Posed, storage);
        int midpoint = Enumerable.Range(0, topology.VertexCount).First(i =>
            topology.Vertices[i].SourceVertex == -1 && new[] { topology.Vertices[i].A, topology.Vertices[i].B, topology.Vertices[i].C }.Count(n => n == 6) == 2);
        var record = topology.Vertices[midpoint]; var triangle = fixture.Source.Triangles[record.SourceTriangle];
        int a = record.A == 0 ? triangle.B : triangle.A, b = record.C == 0 ? triangle.B : triangle.C;
        Assert.Equal(Vector3.Lerp(fixture.Posed[a].Position, fixture.Posed[b].Position, .5f), storage[midpoint].Position);
    }

    [Fact]
    public void Opposite_handedness_midpoint_tie_resolves_to_positive_one()
    {
        var fixture = CoverageFixture.CurvedPanel(false);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        int midpoint = Enumerable.Range(0, topology.VertexCount).First(i =>
            topology.Vertices[i].SourceVertex == -1 && new[] { topology.Vertices[i].A, topology.Vertices[i].B, topology.Vertices[i].C }.Count(n => n == 6) == 2);
        var record = topology.Vertices[midpoint]; var triangle = fixture.Source.Triangles[record.SourceTriangle];
        int a = record.A == 0 ? triangle.B : triangle.A, b = record.C == 0 ? triangle.B : triangle.C;
        var posed = fixture.Posed.ToArray();
        posed[a].Tangent = new(1, 0, 0, 1); posed[b].Tangent = new(1, 0, 0, -1);
        var surface = new ModelVertex[topology.VertexCount];
        Assert.True(topology.TryInterpolate(posed, surface, out var failure), failure.ToString());
        Assert.Equal(new Vector4(1, 0, 0, 1), surface[midpoint].Tangent);
    }

    [Fact]
    public void Oracle_detects_crack_reversal_and_protected_movement()
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var before = new ModelVertex[topology.VertexCount]; topology.Interpolate(fixture.Posed, before);
        var indices = topology.Indices.ToArray(); var protect = topology.Vertices.Select(v => v.Strength == 0).ToArray();
        var after = before.ToArray();
        var weld = before.Select((v, i) => (v, i)).GroupBy(p => p.v.Position).First(g => g.Count() > 1).ToArray();
        after[weld[0].i].Position.Z += .01f;
        var cracked = CoverageGeometryOracle.Validate(before, after, fixture, indices, protect);
        Assert.True(cracked.WeldedPositionChanges > 0); Assert.True(cracked.UnmatchedInteriorEdges > 0);
        (indices[0], indices[1]) = (indices[1], indices[0]);
        Assert.True(CoverageGeometryOracle.Validate(before, before, fixture, indices, protect).Folds > 0);
        indices = topology.Indices.ToArray(); after = before.ToArray();
        int protectedIndex = Array.FindIndex(protect, p => p); after[protectedIndex].Position += Vector3.UnitZ;
        Assert.True(CoverageGeometryOracle.Validate(before, after, fixture, indices, protect).ProtectedPositionChanges > 0);
        Assert.True(CoverageGeometryOracle.Validate(before, after, fixture, indices, new bool[protect.Length]).ProtectedPositionChanges > 0);
    }
}
