using System.Linq;
using System.Numerics;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Examples;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageConsumerExampleTests
{
    // Guard the compiled public example against dropping fallback draws or rebuilding partial descriptors.
    [Theory]
    [InlineData(10ul, 0)]
    [InlineData(20ul, 1)]
    [InlineData(30ul, -1)]
    [InlineData(99ul, -1)]
    public void Public_example_selects_a_table_or_explicit_current_pose(ulong key, int entry)
    {
        using var f = new CoveragePublicationFixture();
        f.Binding.Dispose();
        using var consumer = new EquipmentCoverageConsumer(f.Host.Adapter, f.Host.Skeleton, f.Host.Hosts,
            f.Host.Actor, f.Host.Table);
        Assert.True(consumer.TrySetEquipment([f.Host.Equipment()], out _));
        var model = Matrix4x4.CreateTranslation(1, 0, 0);
        var hosts = f.Host.Hosts.Select(mesh => new SkinnedInstanceDraw(mesh, model)
        {
            Tint = new Color(.2f, .3f, .4f, .8f),
            Material = CoverageBindingFixture.Template.Material,
            Dissolve = .2f,
            DissolveEdgeWidth = .1f,
            DissolveEdgeColor = Color.White,
            InvertShadowDissolve = true,
            DissolveComplement = 1,
        }).ToArray();
        var equipment = new AttachmentDraw[f.Host.Bound.DrawCount];
        f.Host.Bound.WriteDraws(f.Host.Skeleton, f.Host.Palette, model,
            MotionKey.Combine(MotionKey.Combine(f.Host.Actor, 17), 0), CoverageBindingFixture.Template, equipment);

        EquipmentCoverageConsumer.SubmitFrame(f.Scene, f.Submission, f.Commands, 128, 96, () =>
        {
            bool accepted = consumer.Queue(f.Host.Palette, model, new CoverageStateKey(key),
                CoverageBindingFixture.Template, hosts, out var failure);
            Assert.Equal(entry >= 0, accepted);
            var queuedCurrentPoseDescriptors = f.Scene.QueuedSkinnedInstancesForTests;
            Assert.NotEmpty(queuedCurrentPoseDescriptors);
            if (entry < 0) Assert.Equal(CoverageFailureCode.TableHole, failure.Code);
            else Assert.Equal(CoverageFailureCode.None, failure.Code);
            Assert.Equal(hosts.Length, queuedCurrentPoseDescriptors.Count);
            for (int i = 0; i < hosts.Length; i++)
                Assert.Equal(new SkinnedSceneInstances.Instance(hosts[i] with { Motion = f.Host.Actor }),
                    queuedCurrentPoseDescriptors[i]);
            Assert.Equal(equipment.Length, f.Scene.QueuedInstancesForTests.Count);
            for (int i = 0; i < equipment.Length; i++)
                Assert.Equal(new SceneInstances.Instance(equipment[i].Rigid), f.Scene.QueuedInstancesForTests[i]);
        }, commands => f.Scene.RenderInternal(commands, 128, 96, f.Target));

        Assert.NotNull(f.History.SurfaceToken);
        Assert.Equal(entry, f.History.Entry);
        Assert.Equal(model, f.History.Model);
    }

    [Fact]
    public void Legacy_attachment_adapter_still_compiles_without_coverage()
    {
        using var f = new MixedAttachmentSubmissionFixture();
        IAttachmentScene legacy = f.Capability;
        Assert.False(legacy is IAttachmentCoverageScene);
        f.Scene.Begin();
        var descriptors = new AttachmentDraw[f.Bound.DrawCount];
        EquipmentCoverageConsumer.QueueOrdinary(legacy, f.Bound, f.Skeleton, f.Palette,
            f.Model, f.Key, MixedAttachmentSubmissionFixture.Template, descriptors);
        Assert.NotEmpty(f.Scene.QueuedInstancesForTests);
        Assert.NotEmpty(f.Scene.QueuedSkinnedInstancesForTests);
        var rigid = descriptors.Where(d => !d.IsSkinned).ToArray();
        var skinned = descriptors.Where(d => d.IsSkinned).ToArray();
        Assert.Equal(rigid.Length, f.Scene.QueuedInstancesForTests.Count);
        Assert.Equal(skinned.Length, f.Scene.QueuedSkinnedInstancesForTests.Count);
        for (int i = 0; i < rigid.Length; i++)
            Assert.Equal(new SceneInstances.Instance(rigid[i].Rigid), f.Scene.QueuedInstancesForTests[i]);
        for (int i = 0; i < skinned.Length; i++)
            Assert.Equal(new SkinnedSceneInstances.Instance(skinned[i].Skinned), f.Scene.QueuedSkinnedInstancesForTests[i]);
    }
}
