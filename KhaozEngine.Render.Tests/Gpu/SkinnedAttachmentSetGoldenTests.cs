using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class SkinnedAttachmentSetGoldenTests(SkinnedAttachmentGpuScene scene, ITestOutputHelper output)
    : IClassFixture<SkinnedAttachmentGpuScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Posed_garment_on_host_matches_independent_skinned_controls(bool gpuSkinning)
    {
        byte[] attachmentPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, gpuSkinning);
        byte[] independentControlPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Skinned, gpuSkinning);
        byte[] cpuSkinningPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, false);
        byte[] gpuSkinningPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, true);
        Evidence($"posed-{gpuSkinning}", attachmentPixels, independentControlPixels);
        int garmentColoredPixels = GarmentCount(attachmentPixels);
        int hostColoredPixels = Count(attachmentPixels, 1);
        Assert.True(garmentColoredPixels > 500);
        Assert.True(hostColoredPixels > 500);
        Assert.Equal(independentControlPixels, attachmentPixels);
        Assert.Equal(cpuSkinningPixels, gpuSkinningPixels);
        byte[] rest = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, gpuSkinning, posed: false);
        Assert.True(Changed(rest, attachmentPixels) > 500);
        output.WriteLine($"{scene.Backend}: garment {garmentColoredPixels}, host {hostColoredPixels} pixels.");
    }

    public static IEnumerable<object[]> FlagCases()
    {
        foreach (bool gpu in new[] { false, true })
            foreach (float dissolve in new[] { 0f, .5f, 1f })
                foreach (float complement in new[] { 0f, 1f })
                    foreach (bool invert in new[] { false, true })
                        foreach (int light in new[] { 1, 2 })
                            yield return [gpu, dissolve, complement, invert, light];
    }

    [GpuTheory]
    [MemberData(nameof(FlagCases))]
    public void Mixed_dissolve_and_shadow_flags_match_rigid_controls(
        bool gpuSkinning, float dissolve, float complement, bool invert, int light)
    {
        byte[] skinnedGarmentPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, dissolve: dissolve, complement: complement, invert: invert, light: light);
        byte[] rigidDissolveControlPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Rigid,
            gpuSkinning, dissolve: dissolve, complement: complement, invert: invert, light: light);
        string name = $"flags-{gpuSkinning}-{dissolve}-{complement}-{invert}-{light}";
        Evidence(name, skinnedGarmentPixels, rigidDissolveControlPixels);
        Assert.Equal(rigidDissolveControlPixels, skinnedGarmentPixels);
        byte[] noShadow = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, dissolve: dissolve, complement: complement, invert: invert, light: light, castsShadows: false);
        // Restrict this comparison to visible receiver pixels, excluding the host and red garment.
        int receiverChanges = DarkenedReceiver(noShadow, skinnedGarmentPixels);
        if (dissolve == .5f || (dissolve == 0f && complement == 0f))
            Assert.True(receiverChanges > 100, $"light {light} changed only {receiverChanges} receiver pixels");
        output.WriteLine($"{scene.Backend}: {name}, receiver changes {receiverChanges}, exact colour and receiver parity.");
    }

    [GpuTheory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Shadow_only_omits_colour_and_still_changes_the_receiver(bool gpuSkinning, int light)
    {
        byte[] receiverOnlyPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, light: 3, receiverOnly: true);
        byte[] shadowOnlyColourPixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, light: 3, shadowOnly: true);
        Assert.Equal(receiverOnlyPixels, shadowOnlyColourPixels);
        byte[] litReceiver = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, light: light, receiverOnly: true);
        byte[] shadowed = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, light: light, shadowOnly: true);
        byte[] control = scene.Capture(SkinnedAttachmentGpuScene.Control.Rigid,
            gpuSkinning, light: light, shadowOnly: true);
        Evidence($"shadow-only-{gpuSkinning}-{light}", shadowed, control);
        Assert.Equal(control, shadowed);
        int shadowOnlyChangedReceiverPixels = DarkenedReceiver(litReceiver, shadowed);
        Assert.True(shadowOnlyChangedReceiverPixels > 100);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Complementary_coverage_partitions_the_solid_garment(bool gpuSkinning)
    {
        byte[] solid = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, gpuSkinning);
        byte[] ordinary = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, gpuSkinning, dissolve: .5f);
        byte[] complement = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, dissolve: .5f, complement: 1);
        Evidence($"complement-{gpuSkinning}", ordinary, complement);
        Assert.True(GarmentCount(ordinary) > 100);
        Assert.True(GarmentCount(complement) > 100);
        for (int i = 0; i < solid.Length; i += 4)
            if (solid[i] > 80 && solid[i] > solid[i + 1] * 2 && solid[i] > solid[i + 2] * 2)
                Assert.NotEqual(ordinary[i] == solid[i], complement[i] == solid[i]);
    }

    [GpuTheory]
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]
    [InlineData(false, 1f)]
    [InlineData(true, 1f)]
    public void Target_outline_matches_direct_skinned_control_with_nonempty_coverage(bool gpuSkinning, float complement)
    {
        byte[] actual = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments,
            gpuSkinning, dissolve: .5f, complement: complement, outlines: true);
        byte[] control = scene.Capture(SkinnedAttachmentGpuScene.Control.Skinned,
            gpuSkinning, dissolve: .5f, complement: complement, outlines: true);
        Evidence($"outline-{gpuSkinning}-{complement}", actual, control);
        Assert.True(GarmentCount(actual, 2) > 100);
        Assert.Equal(control, actual);
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dissolve_motion_varyings_preserve_pose_and_model_motion(bool gpuSkinning)
    {
        scene.MotionFrame(gpuSkinning, 0);
        scene.MotionFrame(gpuSkinning, 1);
        IsoCamera3D camera = MotionExpectation.Snapshot(scene.Fixture.Scene.Camera);
        Vector2 expected = MotionExpectation.Moved(camera, camera, new Vector3(.5f, .25f, 0),
            Vector3.Zero, SkinnedAttachmentGpuScene.W, SkinnedAttachmentGpuScene.H);
        Assert.True(expected.Length() > 5);
        Assert.True(MotionExpectation.AssertDrawnPixels(scene.Fixture.Scene.ReadMotionTargetForTests(),
            (_, _) => expected, .05f) > 500);
        scene.MotionFrame(gpuSkinning, 2);
        Assert.True(MotionExpectation.AssertDrawnPixels(scene.Fixture.Scene.ReadMotionTargetForTests(),
            (_, _) => expected, .05f) > 500);
        output.WriteLine($"{scene.Backend}: motion locations 10 and 11 preserve {expected} pixel motion.");
    }

    [GpuFact]
    public void Reused_scene_matches_fresh_after_pose_and_flag_changes()
    {
        scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, false, posed: false, light: 2, shadowOnly: true);
        scene.Capture(SkinnedAttachmentGpuScene.Control.Rigid, true, dissolve: 1, complement: 1, light: 1);
        scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, false, dissolve: .5f, invert: true, outlines: true);
        byte[] agedScenePixels = scene.Capture(SkinnedAttachmentGpuScene.Control.Attachments, true,
            dissolve: .5f, complement: 1, light: 2);
        using var fresh = new SkinnedAttachmentGpuScene();
        byte[] freshScenePixels = fresh.Capture(SkinnedAttachmentGpuScene.Control.Attachments, true,
            dissolve: .5f, complement: 1, light: 2);
        Evidence("aged-fresh", agedScenePixels, freshScenePixels);
        Assert.Equal(freshScenePixels, agedScenePixels);
    }

    static void Evidence(string name, byte[] actual, byte[] control)
    {
        SkinnedAttachmentGpuScene.Evidence(name + "-actual", actual);
        SkinnedAttachmentGpuScene.Evidence(name + "-control", control);
    }

    int GarmentCount(byte[] pixels, int channel = 0)
    {
        scene.Fixture.Scene.Camera.WorldToScreen(new Vector3(12, 2, 0),
            SkinnedAttachmentGpuScene.W, SkinnedAttachmentGpuScene.H, out Vector2 probe);
        return Count(pixels, channel, probe.X > SkinnedAttachmentGpuScene.W / 2);
    }

    static int Count(byte[] pixels, int channel, bool? rightHalf = null)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (rightHalf.HasValue && (i / 4 % SkinnedAttachmentGpuScene.W >= SkinnedAttachmentGpuScene.W / 2) != rightHalf.Value)
                continue;
            if (pixels[i + channel] > 80 && pixels[i + channel] > pixels[i + (channel + 1) % 3] * 2
                && pixels[i + channel] > pixels[i + (channel + 2) % 3] * 2) count++;
        }
        return count;
    }

    static int Changed(byte[] before, byte[] after)
    {
        int count = 0;
        for (int i = 0; i < before.Length; i += 4)
            if (Math.Abs(before[i] - after[i]) + Math.Abs(before[i + 1] - after[i + 1])
                + Math.Abs(before[i + 2] - after[i + 2]) > 40) count++;
        return count;
    }

    static int DarkenedReceiver(byte[] before, byte[] after)
    {
        int count = 0;
        for (int i = 0; i < before.Length; i += 4)
            if (Math.Abs(before[i] - before[i + 1]) < 5 && Math.Abs(before[i] - before[i + 2]) < 5
                && before[i] > 40 && before[i] + before[i + 1] + before[i + 2]
                    > after[i] + after[i + 1] + after[i + 2] + 30) count++;
        return count;
    }
}
