using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereFogGoldenTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereFogGoldenTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void Golden_AtmosphereFogDistantTerrain()
        => GoldenCompare.AssertOrUpdate("scene3d_atmosphere_fog", _fixture.CaptureFog(),
            AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);

    [GpuFact]
    public void Golden_FogSurvivesOriginStep()
    {
        var a = GoldenCompare.Downsample(_fixture.CaptureFog(), AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        var b = GoldenCompare.Downsample(_fixture.CaptureFog(new Vector3(100000, 1900, 100000)),
            AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        for (int i = 0; i < a.Length; i++) Assert.InRange(MathF.Abs(a[i] - b[i]), 0, .01f);
    }

    [GpuFact]
    public void Golden_OpaqueReceiversMatchCpuFogWithAndWithoutTemporal()
    {
        foreach (bool temporal in new[] { false, true })
            foreach (string receiver in new[] { "rigid", "dissolve", "gpu-skinned", "cpu-skinned",
                "gpu-skinned-dissolve", "cpu-skinned-dissolve", "splat", "tile", "foliage" })
            {
                byte[] clear = _fixture.CaptureReceiver(receiver, temporal, fog: false);
                AtmosphereGoldenScene.WriteCapture($"scene3d_atmosphere_receiver_{receiver}_{(temporal ? "temporal" : "off")}_clear", clear);
                byte[] fogged = _fixture.CaptureReceiver(receiver, temporal);
                AtmosphereGoldenScene.WriteCapture($"scene3d_atmosphere_receiver_{receiver}_{(temporal ? "temporal" : "off")}", fogged);
                var scene = _fixture.Scene;
                var model = scene.Post.ResolveAtmosphereModel();
                var frame = model.CreateFrame(scene.Post.AtmosphereCycle!.Value);
                Vector3 lit = new(scene.Post.AmbientColor.R + scene.Post.LightColor.R,
                    scene.Post.AmbientColor.G + scene.Post.LightColor.G,
                    scene.Post.AmbientColor.B + scene.Post.LightColor.B);
                foreach ((int x, float distance) in new[] { (151, 100f), (329, 1000f) })
                {
                    var unfogged = TonemapMath.Map(lit.X, lit.Y, lit.Z, 1, 0, 0);
                    Assert.True(Vector3.Distance(Patch(clear, x, 160), new(unfogged.R, unfogged.G, unfogged.B)) <= .03f,
                        $"{receiver}, temporal={temporal}, x={x} must contain the white receiver. "
                        + $"GPU {Patch(clear, x, 160)}, CPU {unfogged}, lighting {lit}, key direction {scene.Post.LightDirection}");
                    float focal = AtmosphereGoldenScene.Height * .5f / MathF.Tan(MathF.PI / 6);
                    var fragment = new Vector3(-(x + .5f - 240) * distance / focal,
                        2 + (160 - 160.5f) * distance / focal, distance);
                    Vector3 expected = AtmosphereFog.Shade(scene.Post.Atmosphere, model, frame, lit,
                        new Vector3(0, 2, 0), fragment, Vector3.Zero);
                    var mapped = TonemapMath.Map(expected.X, expected.Y, expected.Z, 1, 0, 0);
                    Vector3 delta = Vector3.Abs(Patch(fogged, x, 160) - new Vector3(mapped.R, mapped.G, mapped.B));
                    Assert.True(MathF.Max(delta.X, MathF.Max(delta.Y, delta.Z)) <= .03f,
                        $"{receiver}, temporal={temporal}, x={x}, CPU fog error {delta}");
                }
            }
    }

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
            AtmosphereGoldenScene.WriteCapture($"scene3d_atmosphere_display_billboard_fog_{(hdr ? "hdr" : "ldr")}", alpha);
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
            scene.Post.Atmosphere.DayTint = Vector3.One;
        }
        var position = new Vector3(0, 0, 2000);
        void Decal(Scene3D scene, DecalBlend blend) => scene.DrawGroundDecal(new GroundDecal
        {
            Center = position,
            Shape = DecalShape.Circle,
            Size = new Vector4(1000, 0, 0, 0),
            FillColor = Color.Black,
            FillFraction = 1,
            Blend = blend,
            VoidFallback = true,
        });
        byte[] baseline = _fixture.CaptureEffects(Configure, _ => { });
        byte[] additive = _fixture.CaptureEffects(Configure, scene => Decal(scene, DecalBlend.Additive));
        Assert.Equal(baseline, additive);
        byte[] alpha = _fixture.CaptureEffects(Configure, scene => Decal(scene, DecalBlend.Alpha));
        byte[] billboard = _fixture.CaptureEffects(Configure, scene => scene.DrawBillboard(position, 1000, Color.Black));
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
        byte[] receiver = _fixture.CaptureEffects(FogControls, _ => { }, opaqueReceiver: true);
        byte[] black = _fixture.CaptureEffects(FogControls, scene => Effects(scene, Color.Black), opaqueReceiver: true);
        Assert.Equal(receiver, black);
        byte[] fogged = _fixture.CaptureEffects(FogControls, scene => Effects(scene, Color.White), opaqueReceiver: true);
        byte[] transmitted = _fixture.CaptureEffects(scene =>
        {
            FogControls(scene);
            scene.Post.Atmosphere.AerialDistanceScale = 0;
        }, scene => Effects(scene, Color.White), opaqueReceiver: true);
        for (int x = 150; x <= 330; x += 90)
        {
            float energy = Patch(transmitted, x, AtmosphereGoldenScene.Height / 2).Length();
            Assert.True(energy > Patch(fogged, x, AtmosphereGoldenScene.Height / 2).Length() + .05f,
                $"The additive effect at x={x} must lose energy through fog.");
        }
        // These are invariant rows. Only the four phase-1 sky and terrain scenes need hosted grids.
        AtmosphereGoldenScene.WriteCapture("scene3d_atmosphere_additive_fog_controls", transmitted);
        AtmosphereGoldenScene.WriteCapture("scene3d_atmosphere_additive_fog", fogged);
    }

    [GpuFact]
    public void Golden_TexturedParticleAndTrailBlendModesIncludeTemporalVariants()
    {
        var scene = _fixture.Scene;
        var texture = scene.GetOrLoadTexture("atmosphere-white", () => scene.LoadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1));
        foreach (bool temporal in new[] { false, true })
            foreach (string kind in new[] { "textured", "particle", "trail" })
            {
                void Configure(Scene3D s)
                {
                    FogControls(s);
                    s.Post.Atmosphere.DayTint = Vector3.One;
                    s.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
                    s.Post.Temporal.Upscale = TemporalUpscale.Native;
                    s.Post.Temporal.Sharpness = 0;
                    s.CameraCut();
                }
                void Draw(Scene3D s, BillboardBlend blend, Color colour)
                {
                    if (kind == "textured") s.DrawBillboard(texture, new Vector3(0, 0, 20000), 4000, colour, blend);
                    else if (kind == "particle") s.DrawParticle(new ParticleSprite
                    {
                        Position = new Vector3(0, 0, 20000),
                        Size = 4000,
                        Color = colour,
                        Blend = blend,
                        Flipbook = new ParticleFlipbook(texture, 1, 1),
                    });
                    else s.DrawTrail(new[] { new TrailSample(new(0, -3000, 20000), 4000, 1),
                        new TrailSample(new(0, 3000, 20000), 4000, 1) }, TrailStyle.Default with
                        { Color = colour, Blend = blend == BillboardBlend.Alpha ? TrailBlend.Alpha : TrailBlend.Additive, SoftEdge = 0 });
                }
                byte[] baseline = _fixture.CaptureEffects(Configure, _ => { }, opaqueReceiver: true);
                byte[] black = _fixture.CaptureEffects(Configure, s => Draw(s, BillboardBlend.Additive, Color.Black), opaqueReceiver: true);
                // Different temporal phases move the surrounding sky by subpixels. Test the flat receiver.
                if (temporal) AssertPatchClose(baseline, black, 240, 160, .01f);
                else Assert.Equal(baseline, black);
                byte[] alpha = _fixture.CaptureEffects(Configure, s => Draw(s, BillboardBlend.Alpha, Color.Black), opaqueReceiver: true);
                var model = scene.Post.ResolveAtmosphereModel();
                var frame = model.CreateFrame(scene.Post.AtmosphereCycle!.Value);
                var expected = AtmosphereFog.Shade(scene.Post.Atmosphere, model, frame, Vector3.Zero,
                    Vector3.Zero, new Vector3(0, 0, 20000), Vector3.Zero);
                var mapped = TonemapMath.Map(expected.X, expected.Y, expected.Z, 1, 0, scene.Post.Hdr.ChromaPreservation);
                Vector3 error = Vector3.Abs(Patch(alpha, 240, 160) - new Vector3(mapped.R, mapped.G, mapped.B));
                Assert.True(MathF.Max(error.X, MathF.Max(error.Y, error.Z)) <= .03f,
                    $"{kind}, temporal={temporal}, alpha fog error {error}");
                byte[] fogged = _fixture.CaptureEffects(Configure, s => Draw(s, BillboardBlend.Additive, Color.White), opaqueReceiver: true);
                byte[] clear = _fixture.CaptureEffects(s => { Configure(s); s.Post.Atmosphere.AerialDistanceScale = 0; },
                    s => Draw(s, BillboardBlend.Additive, Color.White), opaqueReceiver: true);
                Assert.True(Patch(clear, 240, 160).Length() > Patch(fogged, 240, 160).Length() + .05f,
                    $"{kind}, temporal={temporal}, additive energy must attenuate.");
                AtmosphereGoldenScene.WriteCapture($"scene3d_atmosphere_{kind}_{(temporal ? "temporal" : "off")}_alpha", alpha);
            }
    }

    [GpuFact]
    public void Golden_WaterReceivesFogAtItsWorldPosition()
    {
        var scene = _fixture.Scene;
        var floor = scene.GetOrLoadMesh("atmosphere-water-floor", () => scene.LoadMesh(MeshPrimitives.Tile(6000)));
        void Configure(Scene3D scene)
        {
            FogControls(scene);
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 100, 0);
            camera.Pitch = -MathF.Atan(.1f);
            scene.Post.Atmosphere.DayTint = Vector3.One;
            scene.Post.Atmosphere.HazeDensityPerKm = 20;
            WaterSceneTuning.ApplyCalmMirror(scene.Post.Water);
        }
        void Floor(Scene3D s) => s.Draw(floor, Matrix4x4.CreateTranslation(0, -10, 1500), Color.Black);
        void Water(Scene3D s)
        {
            Floor(s);
            s.DrawWater(new WaterPlane(0, 0, 1500, 2000));
        }
        byte[] fogged = _fixture.CaptureEffects(Configure, Water);
        byte[] clear = _fixture.CaptureEffects(scene => { Configure(scene); scene.Post.Atmosphere.AerialDistanceScale = 0; }, Water);
        byte[] floorOnly = _fixture.CaptureEffects(s => { Configure(s); s.Post.Atmosphere.AerialDistanceScale = 0; }, Floor);
        Assert.True(Vector3.Distance(Patch(clear, 240, 160), Patch(floorOnly, 240, 160)) > .03f,
            "The no-fog control must render water over the submerged receiver.");
        AtmosphereGoldenScene.WriteCapture("scene3d_atmosphere_water_fog", fogged);
        AtmosphereGoldenScene.WriteCapture("scene3d_atmosphere_water_clear", clear);
        var post = _fixture.Scene.Post;
        post.Atmosphere.AerialDistanceScale = 20;
        var model = post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(post.AtmosphereCycle!.Value);
        var expected = AtmosphereFog.Shade(post.Atmosphere, model, frame, Vector3.Zero,
            new Vector3(0, 100, 0), new Vector3(0, 0, 1000), Vector3.Zero);
        var mapped = TonemapMath.Map(expected.X, expected.Y, expected.Z, 1, 0, post.Hdr.ChromaPreservation);
        var actual = Patch(fogged, 240, 160);
        var target = new Vector3(mapped.R, mapped.G, mapped.B);
        Assert.True(Vector3.Distance(actual, target) <= .03f, $"Water GPU {actual}, CPU {target}, error {actual - target}");
        Assert.True(Vector3.Distance(Patch(clear, 240, 160), target) > .05f, "The no-fog water control must be distinguishable.");
    }

    [GpuFact]
    public void Golden_SharedFixtureMatchesFreshEffectsScene()
    {
        _fixture.CaptureEffects(scene =>
        {
            FogControls(scene);
            scene.Post.Hdr.Enabled = false;
            scene.Post.Atmosphere.Enabled = false;
        }, scene => Effects(scene, Color.White), opaqueReceiver: true);
        _fixture.CaptureEffects(scene =>
        {
            FogControls(scene);
            scene.Post.Hdr.Exposure = .5f;
            scene.Post.Hdr.Operator = TonemapOperator.Reinhard;
            scene.Post.Atmosphere.HazeDensityPerKm = .2f;
            scene.Post.Atmosphere.AerialDistanceScale = .1f;
        }, scene => Effects(scene, Color.White), opaqueReceiver: true);
        _fixture.CaptureEffects(scene =>
        {
            FogControls(scene);
            scene.Post.Hdr.Exposure = 3;
            scene.Post.Hdr.ChromaPreservation = 0;
            scene.Post.Atmosphere.HazeDensityPerKm = 6;
            scene.Post.Atmosphere.DayTint = new Vector3(.4f, .2f, .1f);
        }, scene => Effects(scene, Color.White), opaqueReceiver: true);

        static void Configure(Scene3D scene)
        {
            FogControls(scene);
            scene.Post.Hdr.Exposure = 1.7f;
            scene.Post.Hdr.ChromaPreservation = .75f;
            scene.Post.Atmosphere.HazeDensityPerKm = 1.1f;
            scene.Post.Atmosphere.AerialDistanceScale = .8f;
        }
        byte[] fresh = _fixture.CaptureFresh(Configure, scene => Effects(scene, Color.White), opaqueReceiver: true);
        byte[] shared = _fixture.CaptureEffects(Configure, scene => Effects(scene, Color.White), opaqueReceiver: true);
        var expected = GoldenCompare.Downsample(fresh, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        var actual = GoldenCompare.Downsample(shared, AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, GoldenCompare.InSessionTolerance);
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
