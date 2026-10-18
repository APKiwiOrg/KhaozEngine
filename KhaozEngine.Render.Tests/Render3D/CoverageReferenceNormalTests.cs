using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class CoverageReferenceNormalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Vertex_gathers_preserve_shading_seams_tangent_handedness_and_untouched_bytes(bool smooth)
    {
        var fixture = CoverageFixture.CurvedPanel(degenerateFace: true);
        var skin = fixture.Source.SkinVertices.ToArray();
        if (smooth)
            for (int i = 0; i < skin.Length; i++)
            {
                skin[i].Normal = Vector3.UnitZ;
                if (fixture.Source.Vertices[i].MaterialSlice == 1) skin[i].Tangent = new(0, 1, 0, 1);
            }
        var source = new CoverageSourceTopology("reference-normal", CoverageFixture.Hash, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, skin);
        var topology = CoverageRefinement.Build(source, fixture.Map);
        var posed = smooth ? skin.Select(v => SkinningMath.SkinVertex(v, CoverageFixture.Pose)).ToArray() : fixture.Posed;
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out _));
        scratch.Before.CopyTo(scratch.After, 0);
        for (int i = 0; i < topology.VertexCount; i++)
            scratch.After[i].Position.Z -= topology.Vertices[i].Strength * .05f;
        byte[] positionsBeforeGather = Bytes(scratch.After);
        Assert.True(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure), failure.ToString());
        Assert.Equal(positionsBeforeGather, Bytes(scratch.After));
        uint[] indices = topology.Indices.ToArray();
        int changed = 0, protectedByteChanges = 0, positionWeldBitDifferences = 0;
        for (int i = 0; i < topology.VertexCount; i++)
        {
            var before = scratch.Before[i]; var actual = scratch.Published[i];
            if (before.Position == actual.Position)
            {
                if (!Bytes(scratch.Before.AsSpan(i, 1)).SequenceEqual(Bytes(scratch.Published.AsSpan(i, 1)))) protectedByteChanges++;
                continue;
            }
            changed++;
            // The oracle is independent and indexed. Map every corner in this shading group to this vertex for its normal gather.
            uint[] shading = indices.Select(v => topology.Vertices[(int)v].ShadingGroup == topology.Vertices[i].ShadingGroup ? (uint)i : v).ToArray();
            Vector3 expectedNormal = CoverageGeometryOracle.ExpectedNormal(shading, scratch.Before, scratch.After, i);
            Vector3 expectedTangent = CoverageGeometryOracle.ExpectedTangent(indices, scratch.Before, scratch.After, i);
            Assert.True(Vector3.Distance(expectedNormal, actual.Normal) < 2e-5f, $"normal {i}");
            Assert.True(Vector3.Distance(expectedTangent, new(actual.Tangent.X, actual.Tangent.Y, actual.Tangent.Z)) < 2e-5f, $"tangent {i}");
            Assert.Equal(before.Tangent.W, actual.Tangent.W);
            Assert.Equal(before.Color, actual.Color);
            Assert.Equal(before.Uv, actual.Uv);
        }
        var seams = Enumerable.Range(0, topology.VertexCount).GroupBy(i => topology.Vertices[i].PositionGroup).Where(g => g.Count() > 1);
        int seamChecks = 0;
        foreach (var seam in seams)
        {
            int[] pair = seam.ToArray();
            if (scratch.Before[pair[0]].Position == scratch.Published[pair[0]].Position) continue;
            seamChecks++;
            if (!Bits(scratch.Published[pair[0]].Position).SequenceEqual(Bits(scratch.Published[pair[1]].Position))) positionWeldBitDifferences++;
            if (smooth) Assert.Equal(scratch.Published[pair[0]].Normal, scratch.Published[pair[1]].Normal);
            else Assert.NotEqual(scratch.Published[pair[0]].Normal, scratch.Published[pair[1]].Normal);
        }
        Assert.True(changed > 0 && seamChecks > 0);
        Assert.Equal(0, protectedByteChanges);
        Assert.Equal(0, positionWeldBitDifferences);
        for (int i = 0; i < topology.VertexCount; i++)
        { scratch.Before[i].Normal = Vector3.Zero; scratch.Before[i].Tangent = Vector4.Zero; }
        Assert.True(CoverageReferenceNormals.TryUpdate(topology, scratch, out failure), failure.ToString());
        Assert.All(scratch.Published, v => { Assert.Equal(Vector3.Zero, v.Normal); Assert.Equal(Vector4.Zero, v.Tangent); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Collapsed_or_flipped_changed_patch_refuses_before_output_copy(bool flip)
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        scratch.Before.CopyTo(scratch.After, 0);
        foreach (ref var v in scratch.After.AsSpan()) v.Position = flip ? new(-v.Position.X, v.Position.Y, v.Position.Z) : Vector3.Zero;
        var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
        Array.Fill(scratch.Published, sentinel);
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure));
        Assert.Equal(new CoverageFailure(CoverageFailureCode.FoldedPatch, topology.HostIdentity, 0, 0, 0, 0), failure);
        Assert.All(scratch.Published, v => Assert.Equal(sentinel, v));
    }

    [Fact]
    public void Constructor_normal_refusal_leaves_caller_output_untouched()
    {
        var fixture = CoverageFixture.CurvedPanel(seams: false, challengingCollar: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var posed = fixture.Posed.ToArray();
        posed[15].Position = posed[8].Position;
        var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
        var final = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageReferenceConstructor.TryConstruct(topology, posed, Matrix4x4.Identity, commands, new(topology), final, out var failure));
        Assert.Equal(new CoverageFailure(CoverageFailureCode.FoldedPatch, topology.HostIdentity, 0, 0, 0, 0), failure);
        Assert.All(final, v => Assert.Equal(sentinel, v));
    }

    [Theory]
    [InlineData("unknown-region", CoverageFailureCode.UnknownRegion)]
    [InlineData("stale-identity", CoverageFailureCode.IdentityMismatch)]
    [InlineData("stale-frame", CoverageFailureCode.StaleFrame)]
    [InlineData("overlap", CoverageFailureCode.OverlappingConformers)]
    [InlineData("capacity", CoverageFailureCode.CapacityExceeded)]
    [InlineData("escaped-core", CoverageFailureCode.CoreEscaped)]
    [InlineData("cap-contact", CoverageFailureCode.CapContact)]
    [InlineData("budget", CoverageFailureCode.RecessionExceeded)]
    [InlineData("support-gap", CoverageFailureCode.UnsupportedSupport)]
    [InlineData("mirror", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("singular", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("projective", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("nonfinite", CoverageFailureCode.UnsupportedTransform)]
    public void All_existing_construction_negatives_keep_their_codes(string kind, CoverageFailureCode expected)
    {
        var (topology, posed, model, commands, _) = CoverageCertificationTests.Scenario(kind);
        var sentinel = new ModelVertex(new(7, 8, 9), Vector3.UnitY, Vector4.One);
        var final = Enumerable.Repeat(sentinel, topology.VertexCount).ToArray();
        Assert.False(CoverageReferenceConstructor.TryConstruct(topology, posed, model, commands,
            new(topology, kind == "capacity" ? 1 : CoverageScratch.MaxCommands), final, out var failure));
        Assert.Equal(expected, failure.Code);
        Assert.All(final, v => Assert.Equal(sentinel, v));
    }

    [Fact]
    public void Untouched_degenerate_face_and_signed_zero_copy_the_complete_before_record()
    {
        var fixture = CoverageFixture.CurvedPanel(degenerateFace: true);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        scratch.Before.CopyTo(scratch.After, 0);
        scratch.Before[0].Position.X = 0;
        scratch.After[0].Position.X = BitConverter.Int32BitsToSingle(int.MinValue);
        Assert.True(CoverageReferenceNormals.TryUpdate(topology, scratch, out _));
        Assert.Equal(Bytes(scratch.Before), Bytes(scratch.Published));
        scratch.After[^1].Position.X = .25f;
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    [Fact]
    public void Warm_constructor_allocates_nothing_and_checks_output_arguments()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        var scratch = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, final, out _));
        long start = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, final, out var failure);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(accepted, failure.ToString());
        Assert.Equal(0, allocated);
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, final.AsSpan(1), out _));
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, scratch.Before, out _));
        Assert.Throws<ArgumentException>(() => CoverageReferenceConstructor.TryConstruct(c.Topology, scratch.Before.AsSpan(0, c.Topology.SourceVertexCount), c.Model,
            c.Commands, scratch, final, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonfinite_normal_inputs_refuse_without_publishing(bool position)
    {
        var fixture = CoverageFixture.CurvedPanel();
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        scratch.Before.CopyTo(scratch.After, 0);
        if (position) scratch.After[^1].Position.X = float.PositiveInfinity;
        else scratch.Before[^1].Normal.Y = float.NaN;
        var saved = Bytes(scratch.Published);
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure));
        Assert.Equal(new CoverageFailure(CoverageFailureCode.UnsupportedTransform, topology.HostIdentity, 0, 0, 0, 0), failure);
        Assert.Equal(saved, Bytes(scratch.Published));
    }

    [Fact]
    public void Commanded_faces_require_one_orientation_even_without_displacement()
    {
        var fixture = CoverageFixture.CurvedPanel(level: 1);
        var topology = CoverageRefinement.Build(fixture.Source, fixture.Map);
        var commands = CoverageCommands.Poses(fixture, topology, CoverageCommands.Plate(fixture), Matrix4x4.Identity);
        var scratch = new CoverageReferenceScratch(topology);
        Assert.True(CoverageReferenceFrames.TryPrepare(topology, Matrix4x4.Identity, commands, scratch, out _));
        Assert.True(CoverageReferenceConstructor.TryInterpolate(topology, fixture.Posed, scratch, out _));
        // Reflect one indexed material slice. All faces stay unchanged relative to Before, but the command has both signs.
        for (int i = 0; i < topology.VertexCount; i++)
            if (topology.Source.Triangles[topology.Vertices[i].SourceTriangle].MaterialSlice == 1) scratch.Before[i].Position.X *= -1;
        scratch.Before.CopyTo(scratch.After, 0);
        Assert.False(CoverageReferenceNormals.TryUpdate(topology, scratch, out var failure));
        Assert.Equal(CoverageFailureCode.FoldedPatch, failure.Code);
    }

    static int[] Bits(Vector3 p) => [BitConverter.SingleToInt32Bits(p.X), BitConverter.SingleToInt32Bits(p.Y), BitConverter.SingleToInt32Bits(p.Z)];
    static byte[] Bytes(Span<ModelVertex> vertices) => MemoryMarshal.AsBytes(vertices).ToArray();
}
