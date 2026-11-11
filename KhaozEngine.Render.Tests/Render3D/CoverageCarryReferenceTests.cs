using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageCarryReferenceTests
{
    [Theory]
    [InlineData(0x3f800000u)]
    [InlineData(0x3ec00000u)]
    [InlineData(0x00000000u)]
    [InlineData(0x80000000u)]
    public void Carry_uses_current_direction_and_selected_scalar(uint weightWord)
    {
        float weight = BitConverter.UInt32BitsToSingle(weightWord);
        var rig = new Fixture();
        var topology = rig.Topology;
        var scratch = new CoverageCarryScratch(topology, 2);
        var output = new ModelVertex[topology.VertexCount];
        CoverageCarryCommand[] commands = [new(0, new(.5f, 0, 1), weight)];
        Assert.True(CoverageCarryReference.TryCarry(topology, rig.Palette, rig.Table, 0, commands, scratch, output, out var failure));
        Assert.True(failure.IsSuccess);
        var before = scratch.Before.ToArray();
        var protectedIndices = Enumerable.Range(0, output.Length).Where(v => topology.Vertices[v].Strength == 0).ToArray();
        Assert.NotEmpty(protectedIndices);
        byte[] protectedBeforeBytes = Bytes(protectedIndices.Select(v => before[v]).ToArray());
        byte[] protectedAfterBytes = Bytes(protectedIndices.Select(v => output[v]).ToArray());
        Assert.Equal(protectedBeforeBytes, protectedAfterBytes);

        int moved = 0, changedNormals = 0, changedTangents = 0, aliases = 0, unchanged = 0;
        for (int v = 0; v < output.Length; v++)
        {
            var record = topology.Vertices[v];
            int representative = rig.Table.Representatives[record.PositionGroup];
            float scalar = rig.Table.Entry(0)[record.PositionGroup];
            if (record.RegionIndex != 0 || representative < 0 || scalar == 0 || weight == 0)
            {
                Assert.Equal(Bytes([before[v]]), Bytes([output[v]]));
                unchanged++;
                continue;
            }
            var p = before[representative].Position;
            float applied = scalar * weight;
            var expectedCarriedRepresentativePosition = new Vector3(p.X - applied * .5f, p.Y, p.Z - applied);
            var carriedAliasPosition = output[v].Position;
            Assert.Equal(expectedCarriedRepresentativePosition, carriedAliasPosition);
            int beforeTangentWBits = BitConverter.SingleToInt32Bits(before[v].Tangent.W);
            int afterTangentWBits = BitConverter.SingleToInt32Bits(output[v].Tangent.W);
            Assert.Equal(beforeTangentWBits, afterTangentWBits);
            Assert.Equal(before[v].Color, output[v].Color);
            Assert.Equal(before[v].Uv, output[v].Uv);
            if (before[v].Normal == Vector3.Zero) Assert.Equal(Bytes([before[v].Normal]), Bytes([output[v].Normal]));
            if (new Vector3(before[v].Tangent.X, before[v].Tangent.Y, before[v].Tangent.Z) == Vector3.Zero)
                Assert.Equal(before[v].Tangent, output[v].Tangent);
            if (before[v].Normal != output[v].Normal) changedNormals++;
            if (before[v].Tangent != output[v].Tangent) changedTangents++;
            if (v != representative) aliases++;
            moved++;
        }
        Assert.True(unchanged > 0);
        Assert.Equal(Vector3.Zero, before[rig.ZeroDirectionVertex].Normal);
        Assert.Equal(new Vector4(0, 0, 0, -.75f), output[rig.ZeroDirectionVertex].Tangent);
        Assert.Contains(0x80000000u, MemoryMarshal.Cast<ModelVertex, uint>(output).ToArray());
        Assert.DoesNotContain(0x00800000u, MemoryMarshal.Cast<ModelVertex, uint>(output).ToArray());
        Assert.DoesNotContain(0x80800000u, MemoryMarshal.Cast<ModelVertex, uint>(output).ToArray());
        if (weight == 0)
        {
            Assert.Equal(0, moved);
            Assert.Equal(Bytes(before), Bytes(output));
        }
        else
        {
            Assert.True(moved > 0);
            Assert.True(aliases > 0);
            Assert.True(changedNormals > 0);
            Assert.True(changedTangents > 0);
            var firstEntry = Bytes(output);
            Assert.True(CoverageCarryReference.TryCarry(topology, rig.Palette, rig.Table, 1, commands, scratch, output, out _));
            Assert.NotEqual(firstEntry, Bytes(output));
            int v = rig.ZeroDirectionVertex, g = topology.Vertices[v].PositionGroup;
            Vector3 p = before[rig.Table.Representatives[g]].Position;
            float applied = rig.Table.Entry(1)[g] * weight;
            Assert.Equal(new Vector3(p.X - applied * .5f, p.Y, p.Z - applied), output[v].Position);
        }
    }

    [Fact]
    public void Arithmetic_retains_inclusive_B38_and_separate_scalar_rounding()
    {
        var f = new CoverageReferenceArithmetic();
        float smallest = BitConverter.UInt32BitsToSingle(0x00800000u);
        float next = BitConverter.UInt32BitsToSingle(0x00800001u);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(f.Mul(smallest, 2)));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Sqrt(-smallest)));
        Assert.Equal(0x00800001u, BitConverter.SingleToUInt32Bits(f.Mul(next, 1)));
        Assert.Equal(0x80000000u, BitConverter.SingleToUInt32Bits(f.Mul(-MathF.ScaleB(1, -63), MathF.ScaleB(1, -63))));
        // 4097 squared rounds from 16785409 to 16785408 before subtraction. A fused result would be -8193.
        Assert.Equal(-8192f, f.Sub(16777216f, f.Mul(4097f, 4097f)));
        Assert.Equal(0x3eaaaaabu, BitConverter.SingleToUInt32Bits(f.Div(1, 3)));
        Assert.Equal(0x3fb504f3u, BitConverter.SingleToUInt32Bits(f.Sqrt(2)));
    }

    [Fact]
    public void Source_skinning_precedes_the_frozen_refinement()
    {
        var rig = new Fixture();
        var topology = rig.Topology;
        var scratch = new CoverageCarryScratch(topology, 2);
        var output = new ModelVertex[topology.VertexCount];
        Matrix4x4[] palette = [Matrix4x4.Identity, Matrix4x4.CreateScale(2, 1, 1)];
        Assert.True(CoverageCarryReference.TryCarry(topology, palette, rig.Table, -1, [], scratch, output, out _));
        int witnesses = 0;
        var skin = topology.Source.SkinVertices;
        for (int v = 0; v < output.Length; v++)
        {
            var record = topology.Vertices[v];
            if (record.SourceVertex >= 0) continue;
            var recipe = topology.GetPositionRecipe(record.PositionGroup);
            if (recipe.WeightA != 0 && recipe.WeightB != 0 && recipe.WeightC != 0) continue;
            var a = skin[recipe.WeightA == 0 ? recipe.B : recipe.A];
            var b = skin[recipe.WeightC == 0 ? recipe.B : recipe.C];
            // Each source X is transformed before the dyadic midpoint is evaluated.
            float ax = a.Position.X * (a.BoneWeights.X + 2 * a.BoneWeights.Y);
            float bx = b.Position.X * (b.BoneWeights.X + 2 * b.BoneWeights.Y);
            Assert.Equal(ax * .5f + bx * .5f, output[v].Position.X);
            float wrong = (a.Position.X + b.Position.X) * .5f * (1 + (a.BoneWeights.Y + b.BoneWeights.Y) * .5f);
            if (MathF.Abs(output[v].Position.X - wrong) > 1e-6f) witnesses++;
        }
        Assert.True(witnesses > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skinning_retains_zero_weight_and_degenerate_direction_fallbacks(bool zeroWeights)
    {
        var fixture = CoverageFixture.CurvedPanel(false);
        var vertices = fixture.Source.SkinVertices.ToArray();
        foreach (ref var v in vertices.AsSpan())
        {
            v.BoneIndices = Vector4.Zero;
            v.BoneWeights = zeroWeights ? Vector4.Zero : new(1, 0, 0, 0);
            v.Normal = Vector3.UnitZ;
            v.Tangent = new(MathF.ScaleB(1, -30), 0, 0, -1);
        }
        var source = new CoverageSourceTopology("fallback-host", CoverageFixture.Hash, fixture.Source.Positions,
            fixture.Source.Vertices, fixture.Source.Triangles, vertices);
        var output = new ModelVertex[vertices.Length];
        Matrix4x4[] palette = [Matrix4x4.CreateScale(1e-10f) * Matrix4x4.CreateTranslation(10, 20, 30)];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(source, 1), source, palette, output, out var code));
        Assert.Equal(CoverageFailureCode.None, code);
        Assert.NotEmpty(output);
        for (int v = 0; v < output.Length; v++)
        {
            Assert.Equal(zeroWeights ? vertices[v].Position : new Vector3(10, 20, 30), output[v].Position);
            Assert.Equal(Vector3.UnitZ, output[v].Normal);
            Assert.Equal(Vector4.Zero, output[v].Tangent);
        }
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void Holes_and_inactive_regions_publish_the_current_uncovered_pose(int entry, bool inactive)
    {
        var rig = new Fixture();
        var scratch = new CoverageCarryScratch(rig.Topology, 2);
        var output = new ModelVertex[rig.Topology.VertexCount];
        CoverageCarryCommand[] active = [new(0, Vector3.UnitZ, 1)];
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, rig.Palette, rig.Table, 0, active, scratch, output, out _));
        byte[] oldPose = Bytes(scratch.Before.ToArray());
        var nextPose = new[] { Matrix4x4.CreateTranslation(4, 1, 2), Matrix4x4.CreateTranslation(4, 1, 2) };
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, nextPose, rig.Table, entry,
            inactive ? [] : active, scratch, output, out var failure));
        Assert.True(failure.IsSuccess);
        Assert.Equal(Bytes(scratch.Before.ToArray()), Bytes(output));
        Assert.NotEqual(oldPose, Bytes(output));
    }

    [Theory]
    [InlineData("group-count")]
    [InlineData("index")]
    [InlineData("owner")]
    public void Foreign_table_layout_refuses_without_publishing(string fault)
    {
        var rig = new Fixture();
        int[] representatives = rig.Table.Representatives.ToArray();
        if (fault == "group-count") representatives = representatives[..^1];
        else if (fault == "index") representatives[0] = rig.Topology.VertexCount;
        else
        {
            int group = Array.FindIndex(representatives, v => v >= 0);
            representatives[(group + 1) % representatives.Length] = representatives[group];
        }
        var foreign = CoverageRecessionTable.Create(rig.Table.Identity, representatives, new float[representatives.Length], []);
        var output = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.UnitX, Vector4.One), rig.Topology.VertexCount).ToArray();
        byte[] sentinel = Bytes(output);
        Assert.False(CoverageCarryReference.TryCarry(rig.Topology, rig.Palette, foreign, 0, [],
            new(rig.Topology, 2), output, out var failure));
        Assert.Equal(CoverageFailureCode.TableIdentityMismatch, failure.Code);
        Assert.Equal(rig.Topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(sentinel, Bytes(output));
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, rig.Palette, rig.Table, 0, [],
            new(rig.Topology, 2), output, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Nonfinite_skin_or_carry_refuses_without_publishing(bool badDirection)
    {
        var rig = new Fixture();
        var palette = (Matrix4x4[])rig.Palette.Clone();
        if (!badDirection) palette[0].M11 = float.NaN;
        CoverageCarryCommand[] commands = [new(0, badDirection ? new(float.NaN, 0, 1) : Vector3.UnitZ, 1)];
        var output = Enumerable.Repeat(new ModelVertex(Vector3.One, Vector3.UnitX, Vector4.One), rig.Topology.VertexCount).ToArray();
        byte[] sentinel = Bytes(output);
        var scratch = new CoverageCarryScratch(rig.Topology, 2);
        Assert.False(CoverageCarryReference.TryCarry(rig.Topology, palette, rig.Table, 0, commands, scratch, output, out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Code);
        Assert.Equal(sentinel, Bytes(output));
        Assert.True(CoverageCarryReference.TryCarry(rig.Topology, rig.Palette, rig.Table, 0,
            [new(0, Vector3.UnitZ, 1)], scratch, output, out _));
    }

    [Fact]
    public void Uniforms_compose_palette_and_current_direction_without_posing_geometry()
    {
        var rig = new Fixture();
        var uniforms = new CoverageCarryUniforms(rig.Topology, 2);
        Matrix4x4 model = Matrix4x4.CreateScale(2, 4, 8) * Matrix4x4.CreateTranslation(9, 7, 5);
        Matrix4x4 equipment = Matrix4x4.Identity;
        equipment.M31 = .5f;
        var pose = rig.PoseCommand(equipment) with { TransitionWeight = .375f };
        Matrix4x4[] inverseBind = [Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(-1, 2, -3), Matrix4x4.Identity];
        Assert.True(uniforms.TryPrepare(rig.Palette, inverseBind, model, [pose], out var failure));
        Assert.True(failure.IsSuccess);
        Assert.Equal(inverseBind[0] * rig.Palette[0], uniforms.ComposedPalette[0]);
        var command = Assert.Single(uniforms.Commands.ToArray());
        Assert.Equal(new CoverageCarryCommand(0, new(.25f, 0, .125f), .375f), command);
        var changed = pose with { CoverageWorld = Matrix4x4.Identity };
        Assert.False(uniforms.TryPrepare(rig.Palette, inverseBind, model, [changed], out failure));
        Assert.Equal(CoverageFailureCode.StaleFrame, failure.Code);
        Assert.Equal(rig.Topology.HostIdentity, failure.HostIdentity);
        Assert.Equal(17ul, failure.EquipmentInstanceId);
        Assert.Equal(23u, failure.CommandId);
        Assert.True(uniforms.Commands.IsEmpty);
        Assert.True(uniforms.ComposedPalette.IsEmpty);
        Assert.True(uniforms.TryPrepare(rig.Palette, inverseBind, model, [pose], out _));
    }

    [Theory]
    [InlineData("region", CoverageFailureCode.UnknownRegion)]
    [InlineData("identity", CoverageFailureCode.IdentityMismatch)]
    [InlineData("overlap", CoverageFailureCode.OverlappingConformers)]
    [InlineData("weight", CoverageFailureCode.UnsupportedTransform)]
    [InlineData("support", CoverageFailureCode.UnsupportedSupport)]
    [InlineData("capacity", CoverageFailureCode.CapacityExceeded)]
    public void Uniform_refusals_preserve_command_context(string fault, CoverageFailureCode expected)
    {
        var rig = new Fixture();
        var uniforms = new CoverageCarryUniforms(rig.Topology, 2);
        var pose = rig.PoseCommand(Matrix4x4.Identity);
        CoverageCommandPose[] commands = [fault switch
        {
            "region" => pose with { RegionIndex = -1 },
            "identity" => pose with { Id = 0 },
            "weight" => pose with { TransitionWeight = float.NaN },
            "support" => pose with { Clearance = 0 },
            _ => pose,
        }];
        if (fault == "overlap") commands = [pose, pose with { Id = 24 }];
        if (fault == "capacity") commands = Enumerable.Range(0, 5).Select(i => pose with { Id = (uint)i + 1 }).ToArray();
        Matrix4x4[] inverseBind = [Matrix4x4.Identity, Matrix4x4.Identity];
        Assert.False(uniforms.TryPrepare(rig.Palette, inverseBind, Matrix4x4.Identity, commands, out var failure));
        Assert.Equal(expected, failure.Code);
        Assert.Equal(rig.Topology.HostIdentity, failure.HostIdentity);
        if (fault == "capacity")
        {
            Assert.Equal(5, failure.Count);
            Assert.Equal(4, failure.Limit);
        }
        else
        {
            Assert.Equal(17ul, failure.EquipmentInstanceId);
            Assert.Equal(fault == "identity" ? 0u : fault == "overlap" ? 24u : 23u, failure.CommandId);
        }
        Assert.True(uniforms.Commands.IsEmpty);
        Assert.True(uniforms.ComposedPalette.IsEmpty);
        Assert.True(uniforms.TryPrepare(rig.Palette, inverseBind, Matrix4x4.Identity, [pose], out _));
    }

    internal static byte[] Bytes<T>(T[] records) where T : unmanaged => MemoryMarshal.AsBytes(records.AsSpan()).ToArray();

    internal sealed class Fixture
    {
        readonly CoverageFixture sourceFixture;
        internal Fixture()
        {
            sourceFixture = CoverageFixture.CurvedPanel(secondRegion: true);
            var skin = sourceFixture.Source.SkinVertices.ToArray();
            int zeroSource = Enumerable.Range(0, skin.Length).First(i => sourceFixture.Map.Topology[i].SurfaceId == 23);
            foreach (ref var v in skin.AsSpan())
            {
                v.Color.X = BitConverter.UInt32BitsToSingle(0x00800000);
                v.Color.Y = BitConverter.UInt32BitsToSingle(0x80800000);
                v.Uv.X = BitConverter.UInt32BitsToSingle(0x80000000);
            }
            skin[zeroSource].Normal = Vector3.Zero;
            skin[zeroSource].Tangent = new(0, 0, 0, -.75f);
            var source = new CoverageSourceTopology("carry-host", CoverageFixture.Hash, sourceFixture.Source.Positions,
                sourceFixture.Source.Vertices, sourceFixture.Source.Triangles, skin);
            Topology = CoverageRefinement.Build(source, sourceFixture.Map);
            ZeroDirectionVertex = Enumerable.Range(0, Topology.VertexCount).First(v => Topology.Vertices[v].SourceVertex == zeroSource);
            int[] representatives = Enumerable.Repeat(-1, Topology.PositionGroupCount).ToArray();
            float[] recessions = new float[Topology.PositionGroupCount * 2];
            for (int v = 0; v < Topology.VertexCount; v++)
            {
                var record = Topology.Vertices[v];
                if (record.Strength == 0 || representatives[record.PositionGroup] >= 0) continue;
                representatives[record.PositionGroup] = v;
                recessions[record.PositionGroup] = record.Strength * .03125f;
                recessions[Topology.PositionGroupCount + record.PositionGroup] = record.Strength * .0625f;
            }
            int zeroGroup = Topology.Vertices.First(v => v.RegionIndex == 0 && v.SourceVertex < 0 && v.Strength > 0).PositionGroup;
            recessions[zeroGroup] = recessions[Topology.PositionGroupCount + zeroGroup] = -0f;
            var identity = new CoverageRecessionIdentity(CoverageFixture.Hash, CoverageFixture.Hash, CoverageFixture.Hash,
                CoverageFixture.Hash, CoverageFixture.Hash, CoverageFixture.Hash);
            Table = CoverageRecessionTable.Create(identity, representatives, recessions, [new(new(1), 0), new(new(2), 1)]);
        }

        internal CoverageRefinement Topology { get; }
        internal CoverageRecessionTable Table { get; }
        internal int ZeroDirectionVertex { get; }
        internal Matrix4x4[] Palette { get; } = [Matrix4x4.CreateTranslation(2, -1, .5f),
            Matrix4x4.CreateScale(1.25f, .75f, 1.125f) * Matrix4x4.CreateTranslation(2, -1, .5f)];

        internal CoverageCommandPose PoseCommand(Matrix4x4 equipment)
        {
            Vector3[] positions = sourceFixture.RigidPositions;
            uint[] indices = sourceFixture.RigidIndices;
            var source = new CoverageSourceTopology("carry-support", CoverageFixture.Hash, positions,
                Enumerable.Range(0, positions.Length).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)),
                Enumerable.Range(0, indices.Length / 3).Select(t => new CoverageSourceTriangle(0, 0, t, -1, 0,
                    (int)indices[t * 3], (int)indices[t * 3 + 1], (int)indices[t * 3 + 2])));
            var support = CoverageSupportSurface.Compile(source, [new(0, 0, 0, 4)], Matrix4x4.Identity);
            return new(23, 17, Topology, 0, support, equipment, equipment, -2, 2, .4f, .02f, .8f);
        }
    }
}
