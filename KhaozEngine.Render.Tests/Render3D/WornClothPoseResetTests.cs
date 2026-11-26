using System.Numerics;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

public sealed class WornClothPoseResetTests
{
    [Fact]
    public void Reset_before_the_first_pose_discards_a_compatible_rebind_and_seeds_the_current_fit()
    {
        using var rig = new OutfitRig();
        var set = rig.Wear("reset-pending.json", ClothAsset(rig).Set);
        var options = new ChainSimulationOptions { HoldBlendSeconds = 0, Gravity = new Vector3(0, 0, 9.81f) };
        var old = set.CreateMotion(null, options);
        var next = set.CreateMotion(null, options);
        var surface = set.ClothSurface(ClothPartId)!;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var selection = new WornClothSelection(slot, 0, ClothPartId);
        old.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 0, MotionLevel.Held, selection);
        for (int i = 0; i < 30; i++)
            old.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, 1f / 120, MotionLevel.Held, selection);
        Assert.True(old.TryGetClothPresentation(0, ClothPartId, out var deflected));
        Assert.True(Vector3.Distance(deflected.Nodes[2], surface.FittedNodes[2]) > .005f);

        Assert.Equal(0ul, next.PoseRevision);
        Assert.True(slot.Rebind(next, 0, ClothPartId));
        next.Reset();
        Assert.True(slot.IsOccupied);
        next.Step(rig.Skeleton, rig.Palette, Matrix4x4.Identity, .004f, MotionLevel.Held, selection);

        Assert.True(next.TryGetClothPresentation(0, ClothPartId, out var reset));
        Assert.True(reset.IsCurrentPose);
        Assert.False(old.TryGetClothPresentation(0, ClothPartId, out _));
        Assert.Equal(0, next.ClothStatus.Substeps);
        for (int node = 0; node < reset.Nodes.Length; node++)
            Assert.InRange(Vector3.Distance(surface.FittedNodes[node], reset.Nodes[node]), 0, .0005f);
    }
}
