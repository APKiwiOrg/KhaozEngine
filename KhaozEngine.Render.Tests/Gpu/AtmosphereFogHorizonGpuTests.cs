using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereFogHorizonGpuTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;

    public AtmosphereFogHorizonGpuTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void FogColour_ApproachesTheDownwardColourWithZeroSlope()
    {
        _fixture.CaptureEffects(scene =>
        {
            _fixture.TimeOfDay = .747f;
            ((FlyCamera3D)scene.CameraOverride!).Position = Vector3.Zero;
        }, _ => { });
        var device = _fixture.GpuDevice;
        var factory = device.Factory;
        string bindings = ShaderSources.AtmosphereBindingsAt1Glsl
            .Replace("binding=1", "binding=0", StringComparison.Ordinal)
            .Replace("binding=2", "binding=1", StringComparison.Ordinal)
            .Replace("binding=3", "binding=2", StringComparison.Ordinal);
        string fragment = "#version 450\n" + bindings + ShaderSources.AtmosphereFogGlsl + @"
layout(location=0) out vec4 oColor;
void main() {
    float step = 0.0001 * ATMO_PI / 180.0;
    vec3 at = atmosphereFogColour(Atmo.Camera.xyz + vec3(-100000.0, 0.0, 0.0));
    vec3 above = atmosphereFogColour(Atmo.Camera.xyz + vec3(-cos(step), sin(step), 0.0) * 100000.0);
    oColor = vec4((above - at) / (step * length(at)), 1.0);
}";
        using var target = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget));
        using var staging = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
        using var framebuffer = factory.CreateFramebuffer(null, target);
        using var shaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, fragment);
        using var layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(AtmosphereBindings.AppendLayout()));
        using var set = factory.CreateResourceSet(new GpuResourceSetDescription(layout,
            _fixture.Scene.AtmosphereForTests.Bindings.AppendResources()));
        using var pipeline = factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = new[] { GpuBlendAttachment.OverrideBlend },
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                depthClipEnabled: false, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { layout },
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription>(),
            Outputs = framebuffer.Outputs,
        });
        using var commands = factory.CreateCommandList();
        using (GpuRecording.Open(device, commands, nameof(AtmosphereFogHorizonGpuTests)))
        {
            var scene = _fixture.Scene;
            var post = scene.Post;
            var model = post.ResolveAtmosphereModel();
            var frame = AtmosphereRenderer.ResolveFrame(post, model);
            var camera = scene.CameraOverride!;
            var uniforms = AtmosphereRenderer.PackUbo(post.Atmosphere, frame, model.EvaluateLighting(0, frame),
                camera.Eye, Vector3.Zero, camera.View, camera.Projection, 1, 1, post.Hdr);
            commands.UpdateBuffer(scene.AtmosphereForTests.Bindings.Uniforms, 0, in uniforms);
            commands.SetFramebuffer(framebuffer);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
            commands.CopyTexture(target, staging);
        }
        device.Submit(commands);
        device.WaitForIdle();
        var mapping = device.Map(staging, GpuMapMode.Read);
        var bytes = new byte[8];
        try { Marshal.Copy(mapping.Data, bytes, 0, bytes.Length); }
        finally { device.Unmap(staging); }
        var slope = new Vector3((float)BitConverter.ToHalf(bytes, 0), (float)BitConverter.ToHalf(bytes, 2),
            (float)BitConverter.ToHalf(bytes, 4));
        Console.WriteLine($"FOG_HORIZON_GPU_SLOPE relativeSlopePerRadian={slope}");
        Assert.True(slope.Length() < .01f, $"The upward GPU fog colour slope {slope} must meet the downward colour.");
    }

    [GpuFact]
    public void FoggedReceiver_HasContinuousColourAcrossEyeLevel()
    {
        foreach (float height in new[] { 1f, 2f, 16f })
            foreach (float time in new[] { .747f, .625f })
            {
                byte[] pixels = _fixture.CaptureEffects(scene =>
                {
                    _fixture.TimeOfDay = time;
                    var camera = (FlyCamera3D)scene.CameraOverride!;
                    camera.Position = new Vector3(0, height, 0);
                    camera.Yaw = -MathF.PI / 2;
                    camera.Pitch = 0;
                    camera.FieldOfView = MathF.PI / 180;
                    camera.FarPlane = 100000;
                    scene.Post.Atmosphere.HazeDensityPerKm = 100;
                }, scene => scene.DrawBillboard(new Vector3(-20000, height, 0), 4000,
                    KhaozEngine.Primitives.Color.Black));
                AtmosphereGoldenScene.WriteCapture($"scene3d_atmosphere_fog_horizon_{height}_{time}", pixels);
                int largestStep = 0;
                for (int y = 1; y < AtmosphereGoldenScene.Height; y++)
                    for (int channel = 0; channel < 3; channel++)
                    {
                        int index = (y * AtmosphereGoldenScene.Width + AtmosphereGoldenScene.Width / 2) * 4 + channel;
                        largestStep = Math.Max(largestStep, Math.Abs(pixels[index] - pixels[index - AtmosphereGoldenScene.Width * 4]));
                    }
                Console.WriteLine($"FOG_HORIZON_GPU height={height} time={time} maxAdjacentByteStep={largestStep}");
                Assert.True(largestStep <= 2, $"Fog colour changes by {largestStep} display levels over 1/320 degree.");
            }
    }

    [GpuFact]
    public void SharedFixture_HorizonCaptureMatchesFreshScene()
    {
        _fixture.CaptureEffects(scene => scene.Post.Atmosphere.Enabled = false, _ => { });
        _fixture.CaptureEffects(scene => scene.Post.Hdr.Exposure = 3, _ => { });
        static void Configure(AtmosphereGoldenScene fixture, Scene3D scene)
        {
            fixture.TimeOfDay = .747f;
            var camera = (FlyCamera3D)scene.CameraOverride!;
            camera.Position = new Vector3(0, 16, 0);
            camera.Yaw = -MathF.PI / 2;
            camera.Pitch = 0;
            camera.FieldOfView = MathF.PI / 180;
            camera.FarPlane = 100000;
            scene.Post.Atmosphere.HazeDensityPerKm = 100;
        }
        static void Draw(Scene3D scene) => scene.DrawBillboard(new Vector3(-20000, 16, 0), 4000,
            KhaozEngine.Primitives.Color.Black);
        using var fresh = new AtmosphereGoldenScene();
        var expected = GoldenCompare.Downsample(fresh.CaptureEffects(scene => Configure(fresh, scene), Draw),
            AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        var actual = GoldenCompare.Downsample(_fixture.CaptureEffects(scene => Configure(_fixture, scene), Draw),
            AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height);
        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(MathF.Abs(expected[i] - actual[i]), 0, GoldenCompare.InSessionTolerance);
    }
}
