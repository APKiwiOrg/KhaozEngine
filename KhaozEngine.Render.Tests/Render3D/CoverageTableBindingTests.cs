using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageTableBindingTests
{
    [Fact]
    public void Configuration_hash_is_canonical_and_preserves_authored_words()
    {
        CoverageConfigurationPart first = new(1, "root", Matrix4x4.Identity, Matrix4x4.Identity);
        CoverageConfigurationPart second = new(2, "joint", Matrix4x4.CreateTranslation(1, 2, 3), Matrix4x4.Identity);
        CoverageConfigurationItem a = new(new string('a', 64), new string('b', 64), [first, second]);
        CoverageConfigurationItem b = new(new string('c', 64), new string('d', 64), [first]);
        string hash = CoverageConfigurationIdentity.Compute([a, b], [Matrix4x4.Identity]);
        Assert.Equal(hash, CoverageConfigurationIdentity.Compute([b, a with { Parts = [second, first] }], [Matrix4x4.Identity]));
        Assert.NotEqual(hash, CoverageConfigurationIdentity.Compute([a], [Matrix4x4.Identity]));
        Matrix4x4 signed = Matrix4x4.Identity;
        signed.M12 = BitConverter.UInt32BitsToSingle(0x80000000);
        Assert.NotEqual(hash, CoverageConfigurationIdentity.Compute([a, b], [signed]));
    }

    [Theory]
    [InlineData("attachment")]
    [InlineData("companion")]
    [InlineData("id")]
    [InlineData("joint")]
    [InlineData("socket")]
    [InlineData("alignment")]
    public void Configuration_hash_changes_for_each_bound_field(string field)
    {
        CoverageConfigurationPart part = new(1, "joint", Matrix4x4.Identity, Matrix4x4.Identity);
        CoverageConfigurationItem item = new(new string('a', 64), new string('b', 64), [part]);
        string original = CoverageConfigurationIdentity.Compute([item], [Matrix4x4.Identity]);
        Assert.Equal("cb8445c7148633e3fefd4a1a62954bb6fb15d8f271b18270a5113cb4cfb426fb", original);
        var moved = Matrix4x4.CreateTranslation(1, 0, 0);
        var changed = field switch
        {
            "attachment" => item with { AttachmentSha256 = new string('c', 64) },
            "companion" => item with { CompanionSha256 = new string('c', 64) },
            "id" => item with { Parts = [part with { Id = 2 }] },
            "joint" => item with { Parts = [part with { HostJoint = "other" }] },
            "socket" => item with { Parts = [part with { Socket = moved }] },
            _ => item with { Parts = [part with { BodyAlignment = moved }] },
        };
        Assert.NotEqual(original, CoverageConfigurationIdentity.Compute([changed], [Matrix4x4.Identity]));
    }

    [Fact]
    public void State_key_selection_never_uses_pose_geometry()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var first, out _));
        Assert.Equal(0, f.Scene.CoverageEntryForTests(first));
        f.Scene.Begin();
        f.Palette[0] = Matrix4x4.CreateTranslation(0, 0, 100);
        Assert.True(f.Prepare(binding, out var otherPose, out _));
        Assert.Equal(0, f.Scene.CoverageEntryForTests(otherPose));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var otherKey, out _, 20));
        Assert.Equal(1, f.Scene.CoverageEntryForTests(otherKey));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("map")]
    [InlineData("refinement")]
    [InlineData("arithmetic")]
    [InlineData("version-one")]
    public void Stale_table_source_identity_refuses(string field)
    {
        using var f = new CoverageBindingFixture();
        string different = new('b', 64);
        var identity = field switch
        {
            "host" => f.Table.Identity with { HostSha256 = different },
            "map" => f.Table.Identity with { HostMapSha256 = different },
            "refinement" => f.Table.Identity with { RefinementSha256 = different },
            "version-one" => f.Table.Identity with { ArithmeticSha256 = CoverageArithmeticContractTests.V1Digest },
            _ => f.Table.Identity with { ArithmeticSha256 = different },
        };
        var error = Assert.Throws<CoverageTableDataException>(() => f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, f.WithIdentity(identity)));
        Assert.Equal(CoverageTableRefusal.HostIdentityMismatch, error.Reason);
        using var control = f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, f.Table);
    }

    [Theory]
    [InlineData("count", CoverageTableRefusal.GroupCountMismatch)]
    [InlineData("upper", CoverageTableRefusal.RepresentativeOutsideTopology)]
    [InlineData("group", CoverageTableRefusal.RepresentativeGroupMismatch)]
    public void Representatives_belong_to_the_bound_topology(string defect, CoverageTableRefusal reason)
    {
        using var f = new CoverageBindingFixture();
        int[] reps = f.Table.Representatives.ToArray();
        int active = Array.FindIndex(reps, r => r >= 0);
        Assert.True(active >= 0);
        if (defect == "count") reps = reps[..^1];
        if (defect == "upper") reps[active] = f.Topology.VertexCount;
        if (defect == "group") reps[active] = Enumerable.Range(0, f.Topology.VertexCount).First(v => f.Topology.Vertices[v].PositionGroup != active);
        var table = CoverageRecessionTable.Create(f.Table.Identity, reps, new float[reps.Length], [new(new(10), 0)]);
        Assert.Equal(reason, Assert.Throws<CoverageTableDataException>(() => f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, table)).Reason);
        using var control = f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, f.Table);
    }

    [Fact]
    public void Duplicate_command_ids_refuse_before_publication()
    {
        using var f = new CoverageBindingFixture();
        using var host = f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, f.Table);
        host.BindActor(f.Actor);
        var d = f.Coverage.Definition.Commands[0];
        var s = f.Coverage.Supports[0];
        var command = new CoverageCommandPose(d.Id, 17, host.Topology, 0, s, Matrix4x4.Identity, s.CoverageLocal,
            d.MinZ, d.MaxZ, d.CollarWidth, d.Clearance, d.MaxWorldRecession);
        f.Scene.Begin();
        Assert.False(host.TryPrepare(f.Palette, Matrix4x4.Identity, new(10), [command, command], f.Actor, out var frame, out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Equal(d.Id, failure.CommandId);
        Assert.Equal(default, frame);
        // A single command passes the shared uniform admission used by the runtime owner.
        var uniforms = new CoverageCarryUniforms(host.Topology, f.Palette.Length);
        Assert.True(uniforms.TryPrepare(f.Palette, f.Meshes[0].InverseBind, Matrix4x4.Identity, [command], out _));
    }
}
