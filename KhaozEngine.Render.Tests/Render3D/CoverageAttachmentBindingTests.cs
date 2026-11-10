using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageAttachmentBindingTests
{
    [Fact]
    public void Ordinary_adapters_keep_empty_configuration_and_refuse_nonempty_coverage()
    {
        using var f = new CoverageBindingFixture();
        var ordinary = new MixedAttachmentScene(f.Adapter);
        using var binding = new HostCoverageBinding(ordinary, f.Skeleton, f.Hosts, f.Actor, f.Table);
        Assert.True(binding.TrySetEquipment([], out _));
        Assert.True(f.Prepare(binding, out var frame, out _));
        Assert.Equal(default, frame);
        Assert.False(binding.TrySetEquipment([f.Equipment()], out var failure));
        Assert.Equal(CoverageFailureCode.UnsupportedAdapter, failure.Code);
        Assert.True(binding.PreparedEquipmentDraws.IsEmpty);
    }

    [Fact]
    public void Attachment_binding_retains_socket_and_alignment_as_separate_authored_words()
    {
        var skeleton = RigidAttachmentTestRig.Create(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .37f));
        var map = RigidAttachmentTestRig.Map(skeleton);
        Matrix4x4 socket = Matrix4x4.CreateRotationX(.21f);
        socket.M12 = BitConverter.UInt32BitsToSingle(0x80000000);
        var part = RigidAttachmentTestRig.Part(1, "hip", socket);
        var binding = new AttachmentSetBinding(new([part]), map);
        var captured = Assert.Single(binding.ConfigurationParts);
        Assert.Equal(part.Id, captured.Id);
        Assert.Equal("hip", captured.HostJoint);
        Assert.Equal(BitConverter.SingleToUInt32Bits(socket.M12), BitConverter.SingleToUInt32Bits(captured.Socket.M12));
        Assert.Equal(socket, captured.Socket);
        Assert.Equal(map.BodyAlignment(map.Node("hip")), captured.BodyAlignment);
        Assert.NotEqual(socket * captured.BodyAlignment, captured.Socket);
    }

    [Fact]
    public void Changed_host_inverse_binds_refuse_with_existing_binding_reasons()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        f.Meshes[0].InverseBind[0] = Matrix4x4.CreateTranslation(1, 0, 0);
        var changed = f.Loaded.Bind(f.Map, f.Meshes[0]);
        Assert.False(binding.TrySetEquipment([new(17, changed, f.Coverage)], out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        f.Meshes[0].InverseBind[0] = new Matrix4x4(float.NaN, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        Assert.Equal(AttachmentBindingRefusal.HostInverseBindNonFinite,
            Assert.Throws<AttachmentBindingDataException>(() => f.Loaded.Bind(f.Map, f.Meshes[0])).Reason);
        f.Meshes[0].InverseBind[0] = Matrix4x4.Identity;
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
    }

    [Fact]
    public void Duplicate_authored_command_ids_across_equipment_refuse_atomically()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        Assert.False(binding.TrySetEquipment([f.Equipment(), f.Equipment(18)], out var failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        Assert.Equal(23u, failure.CommandId);
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equipment_replacement_is_atomic_and_requires_table_configuration_identity(bool companionOnly)
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var token, out _));
        var before = binding.PreparedEquipmentDraws.ToArray();
        var definition = f.Loaded.Definition;
        string changedHash = f.Source.Add("changed.json", System.Text.Encoding.UTF8.GetBytes(" " + System.Text.Encoding.UTF8.GetString(
            KhaozEngine.Attachments.AttachmentSetJson.Write(definition))));
        var loaded = f.Cache.Get("changed.json", changedHash);
        var companion = f.Coverage.Definition;
        string coverageHash = f.Source.Add("changed-coverage.json", KhaozEngine.Attachments.EquipmentCoverageJson.Write(
            new(changedHash, companion.HostMapSha256, companion.Commands)));
        var changed = new CoveredAttachmentInstance(19, loaded.Bind(f.Map, f.Meshes[0]), f.Cache.GetCoverage(loaded, "changed-coverage.json", coverageHash));
        if (companionOnly)
        {
            string hash = f.Source.Add("reformatted-coverage.json", System.Text.Encoding.UTF8.GetBytes(" " +
                System.Text.Encoding.UTF8.GetString(f.Source.Bytes["coverage.json"])));
            changed = new(19, f.Bound, f.Cache.GetCoverage(f.Loaded, "reformatted-coverage.json", hash));
        }
        Assert.False(binding.TrySetEquipment([changed], out var failure));
        Assert.Equal(CoverageFailureCode.TableIdentityMismatch, failure.Code);
        Assert.Equal(before.Select(d => d.Rigid.World), binding.PreparedEquipmentDraws.ToArray().Select(d => d.Rigid.World));
        Assert.Equal(0, f.Scene.CoverageEntryForTests(token));
        f.Scene.DrawSkinned(f.Host(token), f.Palette);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out _, out _));
        Assert.Equal(before[0].Rigid.Motion, binding.PreparedEquipmentDraws.Span[0].Rigid.Motion);
        Assert.True(binding.TrySetEquipment([f.Equipment(99)], out _));
    }

    [Theory]
    [InlineData(30ul)]
    [InlineData(99ul)]
    public void Holes_publish_nothing_and_allow_explicit_current_pose_fallback(ulong key)
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        f.Scene.Begin();
        Assert.False(f.Prepare(binding, out var frame, out var failure, key));
        Assert.Equal(CoverageFailureCode.TableHole, failure.Code);
        Assert.Equal(default, frame);
        Assert.True(binding.PreparedEquipmentDraws.IsEmpty);
        Assert.Empty(f.Scene.QueuedSkinnedInstancesForTests);
        Assert.Empty(f.Scene.QueuedInstancesForTests);
        Assert.True(f.Scene.CoverageActorRegisteredForTests(f.Actor, f.Hosts));
        foreach (var host in f.Hosts) f.Scene.DrawSkinned(new(host, Matrix4x4.Identity) { Motion = f.Actor }, f.Palette);
        f.Bound.Queue(f.Skeleton, f.Palette, Matrix4x4.Identity, MotionKey.From(17), CoverageBindingFixture.Template);
        Assert.Equal(2, f.Scene.QueuedSkinnedInstancesForTests.Count);
        Assert.All(f.Scene.QueuedSkinnedInstancesForTests, draw => Assert.Equal(f.Actor, draw.Motion));
        Assert.Single(f.Scene.QueuedInstancesForTests);
    }

    [Fact]
    public void Empty_equipment_uses_the_ordinary_path_and_shared_tables_exclude_incarnations()
    {
        using var f = new CoverageBindingFixture();
        using var binding = f.Binding();
        f.Scene.Begin();
        Assert.True(f.Prepare(binding, out var frame, out var failure, 99));
        Assert.True(failure.IsSuccess);
        Assert.Equal(default, frame);
        Assert.True(binding.PreparedEquipmentDraws.IsEmpty);
        Assert.True(binding.TrySetEquipment([f.Equipment()], out _));
        using var second = new HostCoverageBinding(f.Adapter, f.Skeleton, f.Hosts, MotionKey.From(902), f.Table);
        Assert.True(second.TrySetEquipment([f.Equipment(456)], out _));
        Assert.True(f.Prepare(binding, out var first, out _));
        Assert.True(f.Prepare(second, out var other, out _));
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData("incomplete", HostCoverageRefusal.MissingMaterialSlices)]
    [InlineData("skeleton", HostCoverageRefusal.SkeletonMismatch)]
    [InlineData("foreign", HostCoverageRefusal.HostHandleOwnershipMismatch)]
    [InlineData("unowned", HostCoverageRefusal.HostHandleOwnershipMismatch)]
    public void Host_binding_requires_complete_owned_materials(string defect, HostCoverageRefusal reason)
    {
        using var f = new CoverageBindingFixture();
        using var other = new CoverageBindingFixture();
        var hosts = defect == "incomplete" ? f.Hosts[..1] : defect == "foreign" ? other.Hosts
            : defect == "unowned" ? f.Hosts.Select(h => new SkinnedMeshHandle(h.Index, h.Generation)).ToArray() : f.Hosts;
        Assert.Equal(f.Hosts[0].Index, other.Hosts[0].Index);
        Assert.Equal(f.Hosts[0].Generation, other.Hosts[0].Generation);
        Assert.NotEqual(f.Hosts[0], other.Hosts[0]);
        var error = Assert.Throws<HostCoverageDataException>(() => f.Scene.CreateHostCoverage(
            defect == "skeleton" ? other.Skeleton : f.Skeleton, hosts, f.Table));
        Assert.Equal(reason, error.Reason);
        if (defect == "incomplete") Assert.Equal(new[] { 1 }, error.MissingMaterialSlices);
        using var control = f.Scene.CreateHostCoverage(f.Skeleton, f.Hosts, f.Table);
        // Ordinary paths deliberately keep the pre-stamp slot/generation behavior.
        f.Scene.Begin();
        f.Scene.DrawSkinned(new(new(f.Hosts[0].Index, f.Hosts[0].Generation), Matrix4x4.Identity), f.Palette);
        Assert.Single(f.Scene.QueuedSkinnedInstancesForTests);
    }
}
