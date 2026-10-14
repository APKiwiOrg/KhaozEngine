using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

[Collection("NativeDeviceLifecycle")]
public sealed class AtmosphereHorizonGpuTests
{
    [GpuFact]
    public void HorizonEndpoint_IntegratesPastThePlanetTangent()
    {
        using var fixture = new AtmosphereGoldenScene();
        fixture.Capture(.5f);
        var device = fixture.GpuDevice;
        var factory = device.Factory;
        string source = ShaderSources.AtmosphereSkyViewFrag;
        string fragment = source[..source.LastIndexOf("void main()", StringComparison.Ordinal)] + @"
void main() {
    float radius = Atmo.Planet.x + Atmo.Camera.w;
    vec3 p = vec3(0.0, radius, 0.0);
    vec3 d = AtmoSkyDirection(vec2(96.5 / 192.0, 54.5 / 108.0), radius);
    float start, end; bool groundHit;
    AtmoScatteringInterval(p, d, start, end, groundHit);
    float tangent = -sqrt((radius - Atmo.Planet.x) * (radius + Atmo.Planet.x));
    oColor = vec4((length(p) - radius) * 1000.0, (dot(p, d) - tangent) * 1000.0, groundHit ? 1.0 : 0.0, end);
}";
        using var target = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.RenderTarget));
        using var staging = factory.CreateTexture(GpuTextureDescription.Texture2D(1, 1,
            GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Staging));
        using var framebuffer = factory.CreateFramebuffer(null, target);
        using var shaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, fragment);
        using var layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(
            new GpuResourceLayoutElement("Atmosphere", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
        using var set = factory.CreateResourceSet(new GpuResourceSetDescription(layout,
            fixture.Scene.AtmosphereForTests.Bindings.Uniforms));
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
        using (GpuRecording.Open(device, commands, nameof(AtmosphereHorizonGpuTests)))
        {
            // The fixture's capture does not initialize this recording's uniform-ring segment.
            var scene = fixture.Scene;
            var post = scene.Post;
            var camera = scene.CameraOverride!;
            var origin = scene.RenderOrigin.GetValueOrDefault();
            var model = post.ResolveAtmosphereModel();
            var frame = AtmosphereRenderer.ResolveFrame(post, model);
            var lighting = model.EvaluateLighting(post.Atmosphere.HeightKm(camera.Eye.Y), frame);
            var scattering = AtmosphereFogScattering.Evaluate(post.Atmosphere, model, frame,
                post.Atmosphere.HeightKm(camera.Eye.Y), lighting.SunTransmittance, lighting.MoonTransmittance);
            var uniforms = AtmosphereRenderer.PackUbo(post.Atmosphere, frame, lighting, scattering,
                camera.Eye - origin, origin, camera.View, camera.Projection,
                AtmosphereGoldenScene.Width, AtmosphereGoldenScene.Height, post.Hdr);
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
        var result = new float[4];
        for (int i = 0; i < result.Length; i++) result[i] = (float)BitConverter.ToHalf(bytes, i * 2);
        Assert.True(result[2] == 0f && result[3] > 100f,
            $"Radius error metres {result[0]}, tangent projection error metres {result[1]}, ground hit {result[2]}, interval end km {result[3]}.");
    }
}
