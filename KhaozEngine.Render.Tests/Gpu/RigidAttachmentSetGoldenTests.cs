using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class RigidAttachmentSetGoldenTests(RigidAttachmentGpuScene scene, ITestOutputHelper output)
    : IClassFixture<RigidAttachmentGpuScene>
{
    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Five_independent_parts_and_two_materials_match_literal_baked_node_controls(bool posed)
    {
        byte[] actual = scene.Capture(control: false, posed);
        byte[] control = scene.Capture(control: true, posed);
        RigidAttachmentGpuScene.Evidence(posed ? "posed-set" : "rest-set", actual);
        RigidAttachmentGpuScene.Evidence(posed ? "posed-control" : "rest-control", control);
        AssertColoredCoverage(actual);
        Assert.Equal(control, actual);
    }

    [GpuFact]
    public void Independently_moving_each_part_changes_its_own_screen_coverage()
    {
        byte[] rest = scene.Capture(control: false, posed: false);
        byte[] posed = scene.Capture(control: false, posed: true);
        IsoCamera3D camera = MotionExpectation.Snapshot(scene.Fixture.Scene.Camera);
        Matrix4x4[] restWorlds = RigidAttachmentGpuScene.ControlWorlds(false);
        Matrix4x4[] posedWorlds = RigidAttachmentGpuScene.ControlWorlds(true);
        for (int part = 0; part < 5; part++)
        {
            int changed = 0;
            // Each literal-control pair occupies its own separate rectangle in this front view.
            camera.WorldToScreen(restWorlds[part].Translation, RigidAttachmentGpuScene.W, RigidAttachmentGpuScene.H, out Vector2 old);
            camera.WorldToScreen(posedWorlds[part].Translation, RigidAttachmentGpuScene.W, RigidAttachmentGpuScene.H, out Vector2 now);
            int left = Math.Max(0, (int)MathF.Min(old.X, now.X) - 35);
            int right = Math.Min(RigidAttachmentGpuScene.W, (int)MathF.Max(old.X, now.X) + 45);
            int top = Math.Max(0, (int)MathF.Min(old.Y, now.Y) - 45);
            int bottom = Math.Min(RigidAttachmentGpuScene.H, (int)MathF.Max(old.Y, now.Y) + 35);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int i = (y * RigidAttachmentGpuScene.W + x) * 4;
                    if (Math.Abs(rest[i] - posed[i]) + Math.Abs(rest[i + 1] - posed[i + 1]) > 40) changed++;
                }
            Assert.True(changed > 100, $"part {part + 1} changed only {changed} pixels");
        }
    }

    [GpuFact]
    public void Shadow_omission_matches_controls_and_removes_real_cast_shadow_pixels()
    {
        byte[] omitted = scene.Capture(control: false, posed: false, shadows: true, castsShadows: false);
        byte[] control = scene.Capture(control: true, posed: false, shadows: true, castsShadows: false);
        byte[] casting = scene.Capture(control: false, posed: false, shadows: true, castsShadows: true);
        RigidAttachmentGpuScene.Evidence("shadow-omitted", omitted);
        RigidAttachmentGpuScene.Evidence("shadow-control", control);
        RigidAttachmentGpuScene.Evidence("shadow-casting", casting);
        Assert.Equal(control, omitted);
        int darker = 0;
        for (int i = 0; i < casting.Length; i += 4)
            if (omitted[i] + omitted[i + 1] + omitted[i + 2] > casting[i] + casting[i + 1] + casting[i + 2] + 30) darker++;
        Assert.True(darker > 100, $"shadow casting darkened only {darker} pixels");
        output.WriteLine($"Shadow casting darkened {darker} pixels.");
    }

    [GpuFact]
    public void Borrowed_descriptors_render_the_same_outline_as_literal_controls()
    {
        byte[] actual = scene.Capture(control: false, posed: true, outlines: true);
        byte[] control = scene.Capture(control: true, posed: true, outlines: true);
        RigidAttachmentGpuScene.Evidence("posed-outline", actual);
        AssertColoredCoverage(actual);
        Assert.Equal(control, actual);
        int blue = 0;
        for (int i = 0; i < actual.Length; i += 4)
            if (actual[i + 2] > actual[i] * 2 && actual[i + 2] > actual[i + 1] * 2 && actual[i + 2] > 80) blue++;
        Assert.True(blue > 100, $"outline drew only {blue} blue pixels");
        output.WriteLine($"Outline covered {blue} blue pixels.");
    }

    [GpuTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mesh_revision_or_caller_incarnation_replacement_starts_new_motion_history(bool replaceCaller)
    {
        scene.Fixture.Scene.CameraCut();
        MotionKey key = MotionKey.From(123);
        scene.MotionFrame(10, replacement: false, key);
        byte[] moving = scene.MotionFrame(10.4f, replacement: false, key);
        IsoCamera3D camera = MotionExpectation.Snapshot(scene.Fixture.Scene.Camera);
        Vector2 expected = MotionExpectation.Moved(camera, camera, new Vector3(10.4f, 0, 0),
            new Vector3(10, 0, 0), RigidAttachmentGpuScene.W, RigidAttachmentGpuScene.H);
        Assert.True(expected.Length() > 10, $"control motion was only {expected} pixels");
        Assert.True(MotionExpectation.AssertDrawnPixels(scene.Fixture.Scene.ReadMotionTargetForTests(),
            (_, _) => expected, .05f) > 1000);
        key = replaceCaller ? MotionKey.From(124) : key;
        byte[] replaced = scene.MotionFrame(10.8f, replacement: !replaceCaller, key);
        MotionTargetReadback replacement = scene.Fixture.Scene.ReadMotionTargetForTests();
        Assert.True(MotionExpectation.AssertDrawnPixels(replacement, (_, _) => Vector2.Zero, .005f) > 1000);
        scene.MotionFrame(11.2f, replacement: !replaceCaller, key);
        Assert.True(MotionExpectation.AssertDrawnPixels(scene.Fixture.Scene.ReadMotionTargetForTests(),
            (_, _) => expected, .05f) > 1000);
        RigidAttachmentGpuScene.Evidence(replaceCaller ? "caller-moving" : "revision-moving", moving);
        RigidAttachmentGpuScene.Evidence(replaceCaller ? "caller-replaced" : "revision-replaced", replaced);
        output.WriteLine($"Stable translation motion {expected} pixels, replacement motion zero, resumed motion {expected} pixels.");
    }

    [GpuFact]
    public void Reused_scene_matches_fresh_scene_after_pose_shadow_and_outline_changes()
    {
        scene.Capture(control: false, posed: false, shadows: true);
        scene.Capture(control: true, posed: true, outlines: true);
        scene.Capture(control: false, posed: false);
        byte[] aged = scene.Capture(control: false, posed: true);
        using var fresh = new RigidAttachmentGpuScene();
        byte[] alone = fresh.Capture(control: false, posed: true);
        AssertColoredCoverage(aged);
        Assert.Equal(alone, aged);
    }

    void AssertColoredCoverage(byte[] pixels)
    {
        int red = 0, green = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] > pixels[i + 1] * 2 && pixels[i] > pixels[i + 2] * 2 && pixels[i] > 60) red++;
            if (pixels[i + 1] > pixels[i] * 2 && pixels[i + 1] > pixels[i + 2] * 2 && pixels[i + 1] > 60) green++;
        }
        Assert.True(red > 500, $"red material covered only {red} pixels");
        Assert.True(green > 500, $"green material covered only {green} pixels");
        output.WriteLine($"{scene.Fixture.Device.Backend} on {scene.Fixture.Device.Capabilities.DeviceName}, red {red} pixels, green {green} pixels.");
    }
}
