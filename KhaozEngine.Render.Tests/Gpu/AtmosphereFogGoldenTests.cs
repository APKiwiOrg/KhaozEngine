using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class AtmosphereFogGoldenTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereFogGoldenTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void Golden_DisplayBillboardFogUsesTonemappedHorizon()
    {
        foreach (bool hdr in new[] { true, false })
        {
            void Configure(Scene3D scene)
            {
                FogControls(scene);
                scene.Post.Hdr.Enabled = hdr;
                scene.Post.Hdr.Exposure = 1.7f;
                scene.Post.Hdr.ChromaPreservation = .75f;
            }
            byte[] horizon = _fixture.CaptureEffects(Configure, _ => { });
            byte[] alpha = _fixture.CaptureEffects(Configure, scene =>
                scene.DrawBillboard(new Vector3(0, 0, 20000), 4000, Color.Black));
            AssertPatchClose(horizon, alpha, AtmosphereGoldenScene.Width / 2, AtmosphereGoldenScene.Height / 2 - 4, .02f);
            byte[] transparent = _fixture.CaptureEffects(Configure, scene =>
                scene.DrawBillboard(new Vector3(0, 0, 20000), 4000, new Color(1, 0, 0, 0)));
            Assert.Equal(horizon, transparent);
            if (hdr) GoldenCompare.AssertOrUpdate("scene3d_atmosphere_display_billboard_fog", alpha,
                AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        }
        VerifyDecalModes();
    }

    void VerifyDecalModes()
    {
        void Configure(Scene3D scene)
        {
            FogControls(scene);
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 100, 0);
            camera.Pitch = -MathF.Atan(.05f);
            scene.Post.Atmosphere.HazeDensityPerKm = 20;
        }
        var position = new Vector3(0, 0, 2000);
        void Decal(Scene3D scene, DecalBlend blend) => scene.DrawGroundDecal(new GroundDecal
        {
            Center = position,
            Shape = DecalShape.Circle,
            Size = new Vector4(300, 0, 0, 0),
            FillColor = Color.Black,
            FillFraction = 1,
            Blend = blend,
            VoidFallback = true,
        });
        byte[] baseline = _fixture.CaptureEffects(Configure, _ => { });
        byte[] additive = _fixture.CaptureEffects(Configure, scene => Decal(scene, DecalBlend.Additive));
        Assert.Equal(baseline, additive);
        byte[] alpha = _fixture.CaptureEffects(Configure, scene => Decal(scene, DecalBlend.Alpha));
        byte[] billboard = _fixture.CaptureEffects(Configure, scene => scene.DrawBillboard(position, 300, Color.Black));
        AssertPatchClose(billboard, alpha, AtmosphereGoldenScene.Width / 2, AtmosphereGoldenScene.Height / 2, .02f);
        byte[] unfogged = _fixture.CaptureEffects(scene =>
        {
            Configure(scene);
            scene.Post.Atmosphere.AerialDistanceScale = 0;
        }, scene => Decal(scene, DecalBlend.Alpha));
        Assert.True(Patch(alpha, AtmosphereGoldenScene.Width / 2, AtmosphereGoldenScene.Height / 2).Length()
            > Patch(unfogged, AtmosphereGoldenScene.Width / 2, AtmosphereGoldenScene.Height / 2).Length() + .05f,
            "The opaque black alpha decal must receive haze at its reconstructed fallback position.");
    }

    [GpuFact]
    public void Golden_AdditiveEffectsAttenuateWithoutHaze()
    {
        byte[] horizon = _fixture.CaptureEffects(FogControls, _ => { });
        byte[] black = _fixture.CaptureEffects(FogControls, scene => Effects(scene, Color.Black));
        Assert.Equal(horizon, black);
        byte[] fogged = _fixture.CaptureEffects(FogControls, scene => Effects(scene, Color.White));
        byte[] transmitted = _fixture.CaptureEffects(scene =>
        {
            FogControls(scene);
            scene.Post.Atmosphere.AerialDistanceScale = 0;
        }, scene => Effects(scene, Color.White));
        for (int x = 150; x <= 330; x += 90)
        {
            float energy = Patch(transmitted, x, AtmosphereGoldenScene.Height / 2).Length();
            Assert.True(energy > Patch(fogged, x, AtmosphereGoldenScene.Height / 2).Length() + .05f,
                $"The additive effect at x={x} must lose energy through fog.");
        }
        // The golden carries visible no-fog controls. The numerical comparison above proves fog attenuation.
        GoldenCompare.AssertOrUpdate("scene3d_atmosphere_additive_fog_controls", transmitted,
            AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
    }

    static void FogControls(Scene3D scene)
    {
        var camera = (FlyCamera3D)scene.CameraOverride!;
        camera.Position = Vector3.Zero;
        camera.Yaw = camera.Pitch = 0;
        camera.FarPlane = 100000;
        scene.Post.Atmosphere.HazeDensityPerKm = 2;
        scene.Post.Atmosphere.DayTint = new Vector3(.1f, .12f, .14f);
        scene.ParticleSoftFade = 0;
    }

    static void Effects(Scene3D scene, Color colour)
    {
        scene.DrawParticle(new ParticleSprite
        {
            Position = new Vector3(-6500, 0, 20000),
            Size = 1600,
            Color = colour,
            Shape = ParticleShape.SoftGlow,
            Blend = BillboardBlend.Additive
        });
        scene.DrawTrail(new[] { new TrailSample(new(0, -2500, 20000), 1600, 1),
            new TrailSample(new(0, 2500, 20000), 1600, 1) }, TrailStyle.Default with { Color = colour });
        scene.DrawBeam(new Vector3(6500, -2500, 20000), new Vector3(6500, 2500, 20000), 1600, colour);
        scene.DrawBillboard(new Vector3(0, 6500, 20000), 1600, colour, BillboardBlend.Additive);
        // A black additive void decal must also remain invisible, independently of its plane reconstruction.
        scene.DrawGroundDecal(new GroundDecal
        {
            Center = new Vector3(0, -2000, 20000),
            Shape = DecalShape.Circle,
            Size = new Vector4(5000, 0, 0, 0),
            FillColor = Color.Black,
            FillFraction = 1,
            Blend = DecalBlend.Additive,
            VoidFallback = true
        });
    }

    static void AssertPatchClose(byte[] expected, byte[] actual, int x, int y, float tolerance)
    {
        var delta = Vector3.Abs(Patch(expected, x, y) - Patch(actual, x, y));
        Assert.InRange(delta.X, 0, tolerance);
        Assert.InRange(delta.Y, 0, tolerance);
        Assert.InRange(delta.Z, 0, tolerance);
    }

    static Vector3 Patch(byte[] rgba, int x, int y)
    {
        Vector3 sum = default;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int i = ((y + dy) * AtmosphereGoldenScene.Width + x + dx) * 4;
                sum += new Vector3(rgba[i], rgba[i + 1], rgba[i + 2]) / 255f;
            }
        return sum / 9;
    }
}
