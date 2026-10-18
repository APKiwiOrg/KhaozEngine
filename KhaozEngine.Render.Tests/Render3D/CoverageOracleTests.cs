using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageOracleTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Fixtures => CoverageReferenceCases.Specs.Select(c => new object[] { c.Id });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_frozen_fixture_oracle_verdict_matches_exact_capture_certification(string id)
    {
        var c = CoverageReferenceCases.Get(id);
        var spec = CoverageReferenceCases.Spec(id);
        var capture = CoverageExactCaptureTests.Read(id);
        bool exactPass = CoverageCertification.ValidateGeometry(c.Topology, capture.Before, capture.Final,
            c.Model, c.Commands, new(c.Topology), out CoverageGeometryCertificate _, out var exactFailure);
        var exactStage = exactPass ? CoverageOracleStage.None : CoverageOracleStage.Certification;
        var oracle = new CoverageOracle(c.Topology, c.InverseBind);
        Assert.Same(c.Topology, oracle.Topology);
        bool oraclePass = oracle.TryCheck(c.Palette, c.Model, c.Commands, out var certificate, out var failure);
        output.WriteLine($"{id}: exact={exactPass}/{exactStage}/{exactFailure.Code}/count={exactFailure.Count} "
            + $"oracle={oraclePass}/{failure.Stage}/{failure.Failure.Code}/count={failure.Failure.Count}");
        Assert.Equal(!(spec.Challenging && spec.Weight == 1f), exactPass);
        Assert.Equal(exactPass, oraclePass);
        Assert.Equal(exactStage, failure.Stage);
        Assert.Equal(exactFailure.Code, failure.Failure.Code);
        if (oraclePass)
        {
            Assert.Equal(default, failure);
            Assert.Equal(c.Commands.All(c => c.TransitionWeight == 1f), certificate.FullCoreClearance);
        }
        else
        {
            Assert.Equal(default, certificate);
            Assert.Equal(CoverageOracleStage.Certification, failure.Stage);
            Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Failure.Code);
            Assert.True(exactFailure.Count > 0);
            Assert.True(failure.Failure.Count > 0);
        }
        Assert.Equal(1, oracle.CertificationRunsForTests);
        Assert.Equal(c.Topology.VertexCount, oracle.LastConstructedVertices.Length);
    }

    [Theory]
    [InlineData("escaped-core", 1f, CoverageFailureCode.CoreEscaped)]
    [InlineData("budget", 1f, CoverageFailureCode.RecessionExceeded)]
    [InlineData("budget", 0f, CoverageFailureCode.RecessionExceeded)]
    public void Oracle_negatives_preserve_stage_code_command_and_count(string kind, float weight, CoverageFailureCode code)
    {
        var (topology, _, model, commands, _) = CoverageCertificationTests.Scenario(kind);
        commands[0] = commands[0] with { TransitionWeight = weight };
        var oracle = new CoverageOracle(topology, CoverageOracleCases.IdentityBinds);
        Assert.False(oracle.TryCheck(CoverageFixture.Pose, model, commands, out var certificate, out var failure));
        Assert.Equal(default, certificate);
        Assert.Equal(CoverageOracleStage.Construction, failure.Stage);
        Assert.Equal(code, failure.Failure.Code);
        Assert.Equal(topology.HostIdentity, failure.Failure.HostIdentity);
        Assert.Equal(1ul, failure.Failure.EquipmentInstanceId);
        Assert.Equal(1u, failure.Failure.CommandId);
        Assert.True(failure.Failure.Count > 0);
        Assert.Equal(0, failure.Failure.Limit);
        Assert.True(oracle.LastConstructedVertices.IsEmpty);
        Assert.Equal(0, oracle.CertificationRunsForTests);
        var posed = CoverageOracleCases.Skin(topology, CoverageFixture.Pose, CoverageOracleCases.IdentityBinds);
        Assert.False(CoverageReferenceConstructor.TryConstruct(topology, posed, model, commands,
            new(topology), new ModelVertex[topology.VertexCount], out var expected));
        Assert.Equal(expected, failure.Failure);
    }

    [Fact]
    public void Protected_collar_gap_refuses_at_exact_separation_after_construction()
    {
        var c = CoverageOracleCases.TightCollar();
        Assert.Equal(new Vector3(-.75f, -.75f, -.01f), c.Topology.Source.Positions[0]);
        Assert.Equal(new Vector3(-.25f, -.75f, -.01f), c.Topology.Source.Positions[1]);
        Assert.Equal(new Vector3(-.25f, -.25f, -.1f), c.Topology.Source.Positions[5]);
        var solid = c.Commands[0].Support.Source.Positions;
        Assert.Equal(new Vector3(-1, -1, 0), solid[0]);
        Assert.Equal(new Vector3(1, -1, 0), solid[1]);
        Assert.Equal(new Vector3(1, 1, 0), solid[2]);
        double gap = CoverageTriangleGeometry.SeparationLowerBound(
            CoverageVector.From(c.Topology.Source.Positions[0]), CoverageVector.From(c.Topology.Source.Positions[1]),
            CoverageVector.From(c.Topology.Source.Positions[5]), CoverageVector.From(solid[0]),
            CoverageVector.From(solid[1]), CoverageVector.From(solid[2]));
        Assert.InRange(gap, .00999999, .01);
        Assert.All(c.Topology.Map.Regions[0].Strengths.Where(v => v.Strength == 1),
            v => Assert.Equal(-.1f, c.Topology.Source.Positions[v.SurfaceId].Z));
        var oracle = new CoverageOracle(c.Topology, c.InverseBind);
        Assert.False(oracle.TryCheck(c.Palette, c.Model, c.Commands, out var certificate, out var failure));
        Assert.Equal(default, certificate);
        Assert.Equal(CoverageOracleStage.Certification, failure.Stage);
        Assert.Equal(CoverageFailureCode.EquipmentIntersection, failure.Failure.Code);
        Assert.Equal(1u, failure.Failure.CommandId);
        Assert.True(failure.Failure.Count > 0);
        Assert.Equal(1, oracle.CertificationRunsForTests);
        Assert.False(oracle.LastConstructedVertices.IsEmpty);
        var posed = CoverageOracleCases.Skin(c.Topology, c.Palette, c.InverseBind);
        var scratch = new CoverageReferenceScratch(c.Topology);
        var final = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, posed, c.Model, c.Commands, scratch, final, out _));
        Assert.False(CoverageCertification.ValidateGeometry(c.Topology, scratch.Before, final, c.Model, c.Commands,
            new(c.Topology), out var exactFailure));
        Assert.Equal(exactFailure, failure.Failure);
    }

    [Fact]
    public void Unabsorbed_error_scenario_reports_the_exact_certifier_refusal()
    {
        var (topology, _, model, commands, _) = CoverageCertificationTests.Scenario("unabsorbed-error");
        var oracle = new CoverageOracle(topology, CoverageOracleCases.IdentityBinds);
        Assert.False(oracle.TryCheck(CoverageFixture.Pose, model, commands, out var certificate, out var failure));
        Assert.Equal(default, certificate);
        Assert.False(oracle.LastConstructedVertices.IsEmpty);
        Assert.Equal(1, oracle.CertificationRunsForTests);
        Assert.Equal(CoverageOracleStage.Certification, failure.Stage);
        Assert.Equal(CoverageFailureCode.UnsupportedTransform, failure.Failure.Code);
        Assert.Equal(1u, failure.Failure.CommandId);
    }

    [Theory]
    [InlineData("escaped-core")]
    [InlineData("budget")]
    [InlineData("unabsorbed-error")]
    [InlineData("skinning")]
    [InlineData("capacity")]
    public void Refusal_does_not_poison_the_next_check(string kind)
    {
        var (topology, _, model, commands, _) = CoverageCertificationTests.Scenario("valid");
        var oracle = new CoverageOracle(topology, CoverageOracleCases.IdentityBinds);
        var palette = CoverageFixture.Pose;
        Assert.True(oracle.TryCheck(palette, model, commands, out var first, out _));
        string bits = CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices);
        var bad = (CoverageCommandPose[])commands.Clone();
        if (kind == "escaped-core") bad[0] = CoverageOracleCases.Escaped(bad[0]);
        if (kind == "budget") bad[0] = bad[0] with { MaxWorldRecession = .1f };
        if (kind == "unabsorbed-error") bad[0] = bad[0] with { Clearance = 1e-6f };
        if (kind == "skinning") palette[0].M11 = float.NaN;
        if (kind == "capacity") bad = [];
        Assert.False(oracle.TryCheck(palette, model, bad, out var failed, out _));
        Assert.Equal(default, failed);
        if (kind != "unabsorbed-error") Assert.True(oracle.LastConstructedVertices.IsEmpty);
        Assert.Equal(kind == "unabsorbed-error" ? 2 : 1, oracle.CertificationRunsForTests);
        Assert.True(oracle.TryCheck(CoverageFixture.Pose, model, commands, out var next, out var failure));
        Assert.Equal(default, failure);
        Assert.Equal(first, next);
        Assert.Equal(bits, CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Subnormal_input_is_flushed_and_certifies_like_its_signed_zero(bool negative)
    {
        var c = CoverageReferenceCases.Get("cost-typical-l2-lifted-w1");
        var palette = (Matrix4x4[])c.Palette.Clone();
        palette[1].M12 = MathF.CopySign(MathF.ScaleB(1, -130), negative ? -1 : 1);
        var posed = CoverageOracleCases.Skin(c.Topology, palette, c.InverseBind);
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, posed, c.Model, c.Commands,
            new(c.Topology), new ModelVertex[c.Topology.VertexCount], out _));
        var oracle = new CoverageOracle(c.Topology, c.InverseBind);
        Assert.True(oracle.TryCheck(palette, c.Model, c.Commands, out var tiny, out var failure), failure.ToString());
        Assert.Equal(default, failure);
        string bits = CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices);
        palette[1].M12 = MathF.CopySign(0, negative ? -1 : 1);
        Assert.True(oracle.TryCheck(palette, c.Model, c.Commands, out var zero, out failure));
        Assert.Equal(default, failure);
        Assert.Equal(tiny, zero);
        Assert.Equal(bits, CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
        Assert.Equal(2, oracle.CertificationRunsForTests);
        var escaped = c.Commands.Select(CoverageOracleCases.Escaped).ToArray();
        palette[1].M12 = MathF.ScaleB(1, -130);
        Assert.False(oracle.TryCheck(palette, c.Model, escaped, out _, out failure));
        Assert.Equal(CoverageOracleStage.Construction, failure.Stage);
        Assert.Equal(CoverageFailureCode.CoreEscaped, failure.Failure.Code);
        Assert.Equal(2, oracle.CertificationRunsForTests);
    }

    [Fact]
    public void Oracle_skinning_is_the_pinned_reference_skinning()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        Matrix4x4[] inverse = [Matrix4x4.CreateTranslation(-.25f, 0, 0), Matrix4x4.CreateTranslation(.125f, 0, 0)];
        Matrix4x4[] palette = [Matrix4x4.CreateTranslation(.25f, 0, 0), Matrix4x4.CreateTranslation(-.125f, 0, 0)];
        var oracle = new CoverageOracle(c.Topology, inverse);
        var posed = CoverageOracleCases.Skin(c.Topology, palette, inverse);
        var expected = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, posed, c.Model, c.Commands, new(c.Topology), expected, out _));
        Assert.True(oracle.TryCheck(palette, c.Model, c.Commands, out var first, out _));
        Assert.Equal(CoverageReferenceOutputs.Hash(expected), CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
        inverse[0] = Matrix4x4.CreateTranslation(100, 0, 0);
        inverse[1].M11 = float.NaN;
        Assert.True(oracle.TryCheck(palette, c.Model, c.Commands, out var next, out _));
        Assert.Equal(first, next);
        Assert.Equal(CoverageReferenceOutputs.Hash(expected), CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
        int runs = oracle.CertificationRunsForTests;
        Assert.Throws<ArgumentException>(() => oracle.TryCheck(palette.AsSpan(1), c.Model, c.Commands, out _, out _));
        Assert.True(oracle.LastConstructedVertices.IsEmpty);
        Assert.Equal(runs, oracle.CertificationRunsForTests);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.5f)]
    public void Partial_weight_certificate_does_not_claim_core_clearance(float weight)
    {
        var c = CoverageReferenceCases.Get(weight == 0 ? "curved-ordinary-seams-l2-w0" : "curved-ordinary-seams-l2-w05");
        var oracle = new CoverageOracle(c.Topology, c.InverseBind);
        Assert.True(oracle.TryCheck(c.Palette, c.Model, c.Commands, out var certificate, out _));
        Assert.False(certificate.FullCoreClearance);
        Assert.True(double.IsNaN(certificate.MinimumCoreClearance));
        if (weight != 0) return;
        var scratch = new CoverageReferenceScratch(c.Topology);
        Assert.True(CoverageReferenceConstructor.TryInterpolate(c.Topology,
            CoverageOracleCases.Skin(c.Topology, c.Palette, c.InverseBind), scratch, out _));
        Assert.Equal(CoverageReferenceOutputs.Hash(scratch.Before), CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
        Assert.Equal(0, certificate.DisplacedVertices);
    }

    [Fact]
    public void Creation_rejects_invalid_skin_capacity_and_inverse_binds()
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        Assert.Throws<ArgumentNullException>(() => new CoverageOracle(null!, c.InverseBind));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageOracle(c.Topology, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageOracle(c.Topology, new Matrix4x4[129]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageOracle(c.Topology, c.InverseBind, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageOracle(c.Topology, c.InverseBind, 5));
        Assert.Throws<ArgumentException>(() => new CoverageOracle(c.Topology, new[] { Matrix4x4.Identity }));
        var invalid = (Matrix4x4[])c.InverseBind.Clone();
        invalid[0].M14 = .1f;
        Assert.Throws<ArgumentException>(() => new CoverageOracle(c.Topology, invalid));
        invalid[0] = Matrix4x4.Identity;
        invalid[0].M41 = float.NaN;
        Assert.Throws<ArgumentException>(() => new CoverageOracle(c.Topology, invalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Command_capacity_refuses_before_construction(int count)
    {
        var c = CoverageReferenceCases.Get("curved-ordinary-seams-l2-w1");
        var oracle = new CoverageOracle(c.Topology, c.InverseBind, 1);
        var commands = Enumerable.Repeat(c.Commands[0], count).ToArray();
        Assert.False(oracle.TryCheck(c.Palette, c.Model, commands, out var certificate, out var failure));
        Assert.Equal(default, certificate);
        Assert.Equal(CoverageOracleStage.Construction, failure.Stage);
        Assert.Equal(new CoverageFailure(CoverageFailureCode.CapacityExceeded, c.Topology.HostIdentity, 0, 0, count, 1), failure.Failure);
        Assert.True(oracle.LastConstructedVertices.IsEmpty);
        Assert.Equal(0, oracle.CertificationRunsForTests);
    }

    [Fact]
    public void Zero_weight_bones_keep_identity_skinning()
    {
        var c = CoverageOracleCases.TightCollar();
        var oracle = new CoverageOracle(c.Topology, c.InverseBind);
        var partial = c.Commands.Select(c => c with { TransitionWeight = 0 }).ToArray();
        Assert.True(oracle.TryCheck(c.Palette, c.Model, partial, out var expected, out _));
        string bits = CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices);
        Assert.True(oracle.TryCheck(new[] { Matrix4x4.CreateTranslation(100, 200, 300) }, c.Model, partial, out var actual, out _));
        Assert.Equal(expected, actual);
        Assert.Equal(bits, CoverageReferenceOutputs.Hash(oracle.LastConstructedVertices));
    }

    [Fact]
    public void Failure_numbering_is_append_only()
    {
        CoverageFailureCode[] codes = [CoverageFailureCode.None, CoverageFailureCode.UnknownRegion, CoverageFailureCode.IdentityMismatch,
            CoverageFailureCode.UnsupportedSupport, CoverageFailureCode.UnsupportedTransform, CoverageFailureCode.CoreEscaped,
            CoverageFailureCode.CapContact, CoverageFailureCode.RecessionExceeded, CoverageFailureCode.FoldedPatch,
            CoverageFailureCode.EquipmentIntersection, CoverageFailureCode.HostIntersection, CoverageFailureCode.OverlappingConformers,
            CoverageFailureCode.CapacityExceeded, CoverageFailureCode.StaleFrame, CoverageFailureCode.UnsupportedAdapter];
        Assert.Equal(Enumerable.Range(0, 15), codes.Select(c => (int)c));
        Assert.Equal(codes, Enum.GetValues<CoverageFailureCode>());
        foreach (var code in codes.Skip(1)) Assert.Equal(code, new CoverageFailure(code, null, 0, 0, 0, 0).Code);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoverageFailure((CoverageFailureCode)15, null, 0, 0, 0, 0));
        Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<CoverageOracleStage>().Select(s => (int)s));
        Assert.Equal(0, (int)CoverageOracleStage.None);
        Assert.Equal(1, (int)CoverageOracleStage.Construction);
        Assert.Equal(2, (int)CoverageOracleStage.Certification);
    }
}

internal static class CoverageOracleCases
{
    internal static Matrix4x4[] IdentityBinds => [Matrix4x4.Identity, Matrix4x4.Identity];

    internal static CoverageCommandPose Escaped(CoverageCommandPose command)
    {
        var world = command.EquipmentWorld * Matrix4x4.CreateTranslation(.9f, 0, 0);
        return command with { EquipmentWorld = world, CoverageWorld = command.Support.CoverageLocal * world };
    }

    internal static ModelVertex[] Skin(CoverageRefinement topology, Matrix4x4[] palette, Matrix4x4[] inverse)
    {
        var composed = palette.Select((m, i) => CoverageReferenceSkinning.Compose(m, inverse[i])).ToArray();
        var posed = new ModelVertex[topology.SourceVertexCount];
        Assert.True(CoverageReferenceSkinning.TrySkin(new(), new(topology.Source, inverse.Length), topology.Source, composed, posed, out _));
        return posed;
    }

    internal static CoverageReferenceCase TightCollar()
    {
        var skin = new SkinnedVertex[16];
        var strengths = new CoverageSurfaceStrength[16];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int v = y * 4 + x;
                bool core = x is 1 or 2 && y is 1 or 2;
                skin[v] = new() { Position = new(-.75f + x * .5f, -.75f + y * .5f, core ? -.1f : -.01f), Normal = Vector3.UnitZ, Color = Vector4.One };
                strengths[v] = new(v, core ? 1 : 0);
            }
        var triangles = new List<CoverageSourceTriangle>();
        var roles = new List<HostCoverageRegionTriangles>();
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                int a = y * 4 + x;
                Add(a, a + 1, a + 5); Add(a, a + 5, a + 4);
            }
        void Add(int a, int b, int c)
        {
            int t = triangles.Count;
            triangles.Add(new(0, 0, t, -1, 0, a, b, c));
            roles.Add(new(new(0, 0, t, 1), strengths[a].Strength == 1 && strengths[b].Strength == 1 && strengths[c].Strength == 1
                ? CoverageTriangleRole.Core : CoverageTriangleRole.Collar));
        }
        var source = new CoverageSourceTopology("oracle-collar", CoverageFixture.Hash, skin.Select(v => v.Position).ToArray(),
            Enumerable.Range(0, 16).Select(v => new CoverageSourceVertex(0, 0, v, -1, 0, v)), triangles, skin);
        var map = new HostCoverageMap(CoverageFixture.Hash, Enumerable.Range(0, 16).Select(v => new CoverageTopologyEntry(0, 0, v, v)),
            [new("collar", roles, strengths)], [], new("oracle", "collar"));
        var topology = CoverageRefinement.Build(source, map);
        Vector3[] solid = [new(-1, -1, 0), new(1, -1, 0), new(1, 1, 0), new(-1, 1, 0),
            new(-1, -1, .1f), new(1, -1, .1f), new(1, 1, .1f), new(-1, 1, .1f)];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6, 0, 4, 5, 0, 5, 1, 1, 5, 6, 1, 6, 2,
            2, 6, 7, 2, 7, 3, 3, 7, 4, 3, 4, 0];
        var support = CoverageSupportSurface.Compile(CoverageCommands.Solid("oracle-solid", solid, indices), [new(0, 0, 0, 2)], Matrix4x4.Identity);
        CoverageCommandPose[] commands = [new(1, 1, topology, 0, support, Matrix4x4.Identity, Matrix4x4.Identity, -2, 2, .4f, .02f, .8f, 1)];
        return new("oracle-tight-collar", topology, [Matrix4x4.Identity], [Matrix4x4.Identity],
            Skin(topology, [Matrix4x4.Identity], [Matrix4x4.Identity]), Matrix4x4.Identity, commands);
    }
}
