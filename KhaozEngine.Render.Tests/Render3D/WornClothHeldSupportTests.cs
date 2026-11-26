using System;
using System.Numerics;
using System.Text.Json.Nodes;
using KhaozEngine.Attachments;
using KhaozEngine.Attachments.Render3D;
using KhaozEngine.Tests.Attachments;
using Xunit;
using static KhaozEngine.Tests.Render3D.SkinnedAttachmentBindingTests;

namespace KhaozEngine.Tests.Render3D;

public sealed class WornClothHeldSupportTests
{
    [Fact]
    public void The_midtick_rotating_held_segment_reaches_the_pinned_seam()
    {
        using var rig = new OutfitRig();
        var cloth = rig.Wear("arm-cloth.json", ClothAsset(rig, hipOnlyPins: true).Set);
        var glb = SkinnedAttachmentGlbFixture.Chained();
        glb.Nodes[0]!["children"] = new JsonArray(1);
        glb.Nodes[1]!["children"] = new JsonArray(2);
        glb.Nodes[2]!["translation"] = new JsonArray(0, 0, 0);
        glb.Nodes[3]!["translation"] = new JsonArray(.1f, 0, 0);
        glb.SetInverseBind(0, Matrix4x4.CreateTranslation(-.1f, -2, 0));
        glb.SetInverseBind(2, Matrix4x4.CreateTranslation(0, -2, 0));
        const uint armId = 124;
        var arm = new SkinnedAttachmentPartDefinition(armId, rig.Source.Mesh("rotating-arm.glb", glb.Build()))
        {
            Motion = new ChainMotionDefinition([new ChainDefinition(["left0", "left1"], .1f * Vector3.UnitX, FallSettings)], []),
            Regions = [new("alpha", 1)],
        };
        var lower = rig.Wear("rotating-arm.json", new AttachmentSetDefinition([arm], AttachmentLayer.Garment, []));
        using var outfit = rig.Cache.BindOutfit(OutfitLiftDrawTests.Body(rig), [lower, cloth]);
        var motion = outfit.CreateMotion(null, new ChainSimulationOptions { HoldBlendSeconds = 0 });
        var surface = outfit.ClothSurface(1, ClothPartId)!;
        var slot = new WornClothSlot();
        slot.Prepare(surface);
        var select = new WornClothSelection(slot, 1, ClothPartId);
        var pose = (Matrix4x4[])rig.Palette.Clone();
        var pin = surface.FittedNodes[1];
        float midReach = .2f * MathF.Sqrt(.5f);
        var anchor = pin + new Vector3(-midReach, 0, midReach);

        // The exact circular tip touches the pin at 45 degrees. The endpoint chord falls
        // over four centimetres short in Z alone, outside both contact radii.
        Assert.True(midReach - .1f > FallSettings.Radius + surface.Rig.Material.HalfThickness);
        pose[0] = Matrix4x4.CreateTranslation(anchor);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 0, MotionLevel.Held, select);
        Assert.True(motion.TryGetClothPresentation(1, ClothPartId, out _));
        pose[0] = Matrix4x4.CreateRotationY(MathF.PI / 2) * Matrix4x4.CreateTranslation(anchor);
        motion.Step(rig.Skeleton, pose, Matrix4x4.Identity, 1f / 60, MotionLevel.Held, select);
        Assert.Equal(1, motion.ClothStatus.Substeps);
        Assert.Equal(ClothStepStatus.UnresolvedContact, motion.ClothStatus.Solve.Status);
        Assert.Equal(armId, motion.ClothStatus.Solve.PartId);
    }
}
