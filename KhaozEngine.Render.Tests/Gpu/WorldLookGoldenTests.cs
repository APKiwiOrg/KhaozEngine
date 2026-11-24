using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class WorldLookGoldenTests
{
    const int Width = 161, Height = 119;

    [GpuFact]
    public void ContactShadingOnlyRemovesIndirectDiffuse()
    {
        byte[] ambient = CaptureContact(false, indirect: true), occluded = CaptureContact(true, indirect: true);
        int darker = 0;
        for (int i = 0; i < ambient.Length; i += 4)
        {
            if (ambient[i] - occluded[i] >= 3) darker++;
            Assert.True(occluded[i] <= ambient[i] + 1);
        }
        Assert.True(darker > 30, $"Expected a visible contact shadow, got {darker} darker pixels.");
        Assert.Equal(ambient.AsSpan(0, Width * 4).ToArray(), occluded.AsSpan(0, Width * 4).ToArray());
        Assert.Equal(CaptureContact(false, indirect: false), CaptureContact(true, indirect: false));
    }

    [GpuFact]
    public void GradingUsesTrilinearDisplayCoordinatesAndKeepsTheBaselineAtIdentityAndZero()
    {
        Vector3[] samples = new Vector3[ColorGradeLut.SampleCount];
        for (int b = 0, i = 0; b < 32; ++b)
        for (int g = 0; g < 32; ++g)
        for (int r = 0; r < 32; ++r)
            samples[i++] = new Vector3(b / 31f, r / 31f, g / 31f);
        var look = new ColorGradeLut(samples);
        byte[] baseline = CaptureGrade(null, 1f);
        Assert.Equal(baseline, CaptureGrade(ColorGradeLut.Identity, 1f));
        Assert.Equal(baseline, CaptureGrade(look, 0f));
        byte[] graded = CaptureGrade(look, 0.5f);
        int center = (Height / 2 * Width + Width / 2) * 4;
        Assert.InRange(graded[center], 109, 111);
        Assert.InRange(graded[center + 1], 62, 64);
        Assert.InRange(graded[center + 2], 128, 131);
        for (int i = 3; i < baseline.Length; i += 4)
            Assert.Equal(baseline[i], graded[i]);
        Assert.Equal(0, graded[3]);
        Assert.Equal(255, graded[center + 3]);

        int displayPixels = 0;
        for (int i = 0; i < baseline.Length; i += 4)
        {
            if (baseline[i] != 255 || baseline[i + 1] != 0 || baseline[i + 2] != 255) continue;
            Assert.True(baseline.AsSpan(i, 4).SequenceEqual(graded.AsSpan(i, 4)));
            displayPixels++;
        }
        Assert.True(displayPixels > 5, "Expected an on-screen display overlay to survive grading.");
    }

    [GpuFact]
    public void NativeTaaKeepsContactThroughMotionAndDropsItAfterACut()
    {
        var baseline = CaptureMotion(useAo: false);
        var withAo = CaptureMotion(useAo: true);
        Assert.Equal(TemporalResetReason.AmbientOcclusion, withAo.ToggleReset);
        Assert.Equal(TemporalResetReason.CameraCutRequested, withAo.CutReset);
        Assert.True(withAo.ActiveBytes > 0);
        Assert.Equal(0, withAo.DisabledBytes);
        int motionContact = 0;
        for (int i = 0; i < baseline.Moving.Length; i += 4)
            if (baseline.Moving[i] - withAo.Moving[i] >= 3) motionContact++;
        Assert.True(motionContact > 20, $"Expected visible contact while moving, got {motionContact} pixels.");
        Assert.Equal(baseline.Cut, withAo.Cut);
    }

    static (byte[] Moving, byte[] Cut, TemporalResetReason ToggleReset, TemporalResetReason CutReset,
        long ActiveBytes, long DisabledBytes) CaptureMotion(bool useAo)
    {
        Scene3D current = null!;
        MeshHandle ground = default, box = default;
        byte[] moving = [], cut = [];
        TemporalResetReason toggleReset = default, cutReset = default;
        long active = 0, disabled = 0;
        Render3DSnapshot.CaptureSequence(Width, Height, scene =>
        {
            Configure(scene);
            current = scene;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
            scene.Post.Background = BackgroundMode.Solid;
            scene.Post.AmbientColor = new Color(.25f, .25f, .25f, 1f);
            ground = scene.LoadMesh(MeshPrimitives.Tile(8));
            box = scene.LoadMesh(MeshPrimitives.Box());
        }, (scene, frame) =>
        {
            scene.Post.AmbientOcclusion.Enabled = useAo && frame is >= 2 and < 8;
            scene.Draw(ground, Matrix4x4.Identity, Color.White);
            if (frame < 7)
                scene.Draw(box, Matrix4x4.CreateTranslation((frame - 3) * .07f, .5f, 0), Color.White);
            else if (frame == 7)
                scene.CameraCut();
        }, frames: 9, (frame, image) =>
        {
            if (frame == 2) toggleReset = current.LastTemporalDiagnostics.LastReset;
            if (frame == 6) { moving = image.Rgba; active = current.AmbientOcclusionTextureBytes; }
            if (frame == 7) { cut = image.Rgba; cutReset = current.LastTemporalDiagnostics.LastReset; }
            if (frame == 8) disabled = current.AmbientOcclusionTextureBytes;
        }, warmupFrames: 2);
        return (moving, cut, toggleReset, cutReset, active, disabled);
    }

    static byte[] CaptureGrade(ColorGradeLut? look, float strength)
    {
        MeshHandle box = default;
        return Render3DSnapshot.Capture(Width, Height, scene =>
        {
            Configure(scene);
            scene.Post.Hdr.Operator = TonemapOperator.Clamp;
            scene.Post.Background = BackgroundMode.Solid;
            scene.Post.TransparentBackground = true;
            scene.Post.ColorGrade.Look = look;
            scene.Post.ColorGrade.Strength = strength;
            box = scene.LoadMesh(MeshPrimitives.Box());
        }, scene =>
        {
            scene.Draw(box, Matrix4x4.CreateTranslation(0, 0.7f, 0),
                Color.Black, new Material(new Color(0.173f, 0.325f, 0.69f, 1), 0, 1));
            scene.DebugFilledQuad(new Vector3(-1.1f, 0.8f, 0f), Vector3.UnitZ, Vector3.UnitX,
                new Vector2(.25f), new Color(1f, 0f, 1f, 1f));
        });
    }

    static byte[] CaptureContact(bool enabled, bool indirect)
    {
        MeshHandle ground = default, box = default;
        return Render3DSnapshot.Capture(Width, Height, scene =>
        {
            Configure(scene);
            scene.Post.AmbientOcclusion.Enabled = enabled;
            scene.Post.AmbientColor = indirect ? new Color(.35f, .35f, .35f, 1f) : Color.Black;
            scene.Post.LightColor = indirect ? Color.Black : new Color(.7f, .7f, .7f, 1f);
            ground = scene.LoadMesh(MeshPrimitives.Tile(8));
            box = scene.LoadMesh(MeshPrimitives.Box());
        }, scene =>
        {
            if (!indirect) scene.AddLight(new Vector3(-1, 1.3f, -1), new Color(1f, .6f, .2f, 1f), 4f, 1.5f);
            scene.Draw(ground, Matrix4x4.Identity, Color.White);
            scene.Draw(box, Matrix4x4.CreateTranslation(0, 0.5f, 0), Color.White,
                indirect ? Material.None : new Material(new Color(.75f, .25f, .125f, 1), 0, 1));
        });
    }

    static void Configure(Scene3D scene)
    {
        scene.CameraOverride = new FlyCamera3D
        {
            Position = new Vector3(0, 2.2f, -4),
            Pitch = -MathF.Atan2(1.5f, 4f),
        };
        scene.Post.RenderScale = RenderScale.MatchViewport;
        scene.Post.BackgroundColor = new Color(.23f, .19f, .13f, 1f);
        scene.Post.LightColor = Color.Black;
        scene.Post.FillLightColor = Color.Black;
        scene.Post.AmbientColor = Color.Black;
    }
}
