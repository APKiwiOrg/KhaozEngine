using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereLutGoldenTests : IClassFixture<AtmosphereGoldenScene>
{
    readonly AtmosphereGoldenScene _fixture;
    public AtmosphereLutGoldenTests(AtmosphereGoldenScene fixture) => _fixture = fixture;

    [GpuFact]
    public void Golden_LutTexelsMatchCpu()
    {
        _fixture.Capture(.5f);
        var scene = _fixture.Scene;
        var renderer = scene.AtmosphereForTests;
        var model = scene.Post.ResolveAtmosphereModel();
        var frame = model.CreateFrame(scene.Post.AtmosphereCycle!.Value);
        foreach ((string name, IGpuTexture texture, int width, int height, bool compress) in new[]
        {
            ("transmittance", renderer.TransmittanceTexture!, 256, 64, false),
            ("multi", renderer.MultiScatteringTexture!, 32, 32, true),
            ("sky", renderer.Bindings.SkyView, 192, 108, true),
        })
        {
            byte[] rgba = Visualize(_fixture.GpuDevice, texture, width, height, compress);
            bool positive = false;
            for (int i = 0; i < rgba.Length; i += 4)
                positive |= rgba[i] > 0 || rgba[i + 1] > 0 || rgba[i + 2] > 0;
            Assert.True(positive, $"{name} must contain non-clear RGB and positive noon scattering.");
            foreach (float coordinate in new[] { .1f, .5f, .9f })
            {
                int x = (int)(width * coordinate), y = (int)(height * coordinate);
                var uv = new Vector2((x + .5f) / width, (y + .5f) / height);
                Vector3 expected = name switch
                {
                    "transmittance" => model.Tables.Transmittance[y * width + x],
                    "multi" => model.Tables.MultiScattering[y * width + x],
                    _ => model.SkyRadiance(.002f, AtmosphereLutMapping.SkyDirection(uv, 6360.002f, 6360), frame.SunDirection)
                        + frame.MoonFraction * model.SkyRadiance(.002f,
                            AtmosphereLutMapping.SkyDirection(uv, 6360.002f, 6360), frame.MoonDirection),
                };
                if (compress) expected /= Vector3.One + expected;
                int offset = (y * width + x) * 4;
                Vector3 actual = new Vector3(rgba[offset], rgba[offset + 1], rgba[offset + 2]) / 255;
                var error = Vector3.Abs(actual - expected);
                float tolerance = compress ? .02f : .01f;
                Assert.True(MathF.Max(error.X, MathF.Max(error.Y, error.Z)) <= tolerance,
                    $"{name} UV {uv}, GPU {actual}, CPU {expected}, error {error}");
            }
        }
    }

    [GpuFact]
    public void Golden_StaticLutsRebuildOnlyForPhysicalMutation()
    {
        using var fixture = new AtmosphereGoldenScene();
        fixture.Capture(.5f);
        var renderer = fixture.Scene.AtmosphereForTests;
        Assert.Equal(1, renderer.StaticBuildCount);
        fixture.Capture(.747f);
        fixture.Capture(scene => scene.Post.Atmosphere.DayTint = new Vector3(.8f, .9f, 1));
        fixture.Capture(.5f, atmosphere: false);
        fixture.Capture(.5f);
        Assert.Equal(1, renderer.StaticBuildCount);
        fixture.Capture(scene => scene.Post.Atmosphere.MieScatteringPerKm *= 1.1f);
        Assert.Equal(2, renderer.StaticBuildCount);
        fixture.Capture(scene => scene.Post.Atmosphere.MieScatteringPerKm *= 1.1f);
        Assert.Equal(2, renderer.StaticBuildCount);
    }

    // Test-only display shader. It observes the real half-float producer targets through RGBA8 readback.
    static byte[] Visualize(IGpuDevice device, IGpuTexture source, int width, int height, bool compress)
    {
        string fragment = "#version 450\n"
            + "layout(set=0,binding=0) uniform texture2D Lut;\n"
            + "layout(set=0,binding=1) uniform sampler Clamp;\n"
            + "layout(location=0) in vec2 vUv;\nlayout(location=0) out vec4 oColor;\n"
            + "void main() { vec2 uv=gl_FragCoord.xy/vec2(textureSize(sampler2D(Lut,Clamp),0));"
            + "vec3 c=textureLod(sampler2D(Lut,Clamp),uv,0.0).rgb;"
            + (compress ? "c=c/(vec3(1.0)+c);" : "") + "oColor=vec4(c,1.0); }\n";
        var factory = device.Factory;
        using var target = factory.CreateTexture(GpuTextureDescription.Texture2D((uint)width, (uint)height,
            GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        using var framebuffer = factory.CreateFramebuffer(null, target);
        using var shaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, fragment);
        using var layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Lut", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
            new GpuResourceLayoutElement("Clamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment)));
        using var sampler = factory.CreateSampler(GpuSamplerDescription.Linear);
        using var set = factory.CreateResourceSet(new GpuResourceSetDescription(layout, source, sampler));
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
        using (GpuRecording.Open(device, commands, "AtmosphereLutGoldenTests.Visualize"))
        {
            commands.SetFramebuffer(framebuffer);
            commands.SetPipeline(pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
        }
        device.Submit(commands);
        device.WaitForIdle();
        return GpuReadback.ToRgba(device, target, width, height);
    }
}
