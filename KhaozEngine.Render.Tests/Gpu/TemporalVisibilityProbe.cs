using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

// Runs the shipped helper, with independently supplied footprints and full float readback.
internal sealed class TemporalVisibilityProbe : IDisposable
{
    readonly GpuDeviceContext _gpu = GpuDeviceContext.CreateHeadless();
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _layout;
    readonly IGpuPipeline _pipeline;
    readonly IGpuFramebuffer _framebuffer;
    readonly IGpuBuffer _uniforms;
    readonly IGpuTexture _output;
    internal IGpuDevice Device => _gpu.GpuDevice;

    const string Header = @"
layout(set=0,binding=0) uniform texture2D SceneDepth;
layout(set=0,binding=1) uniform texture2D MotionTex;
layout(set=0,binding=2) uniform texture2D PrevDepth;
layout(set=0,binding=3) uniform texture2D KeyShadowVisibility;
layout(set=0,binding=4) uniform texture2D PreviousKeyShadowVisibility;
layout(set=0,binding=5) uniform sampler LinearClamp;
layout(set=0,binding=6) uniform Probe {
    vec4 CurrentDepth; vec4 Sizes; vec4 Jitter; vec4 PreviousJitter;
    vec4 Site; vec4 Reference; vec4 Flags;
};
";
    const string Main = @"
layout(location=0) out float Result;
void main() {
    float change = 0.0;
    bool valid = Flags.x > 0.5 && Flags.y < 0.5
        && all(greaterThanEqual(Site.zw, vec2(0.0))) && all(lessThanEqual(Site.zw, vec2(1.0)))
        && temporalVisibilityChange(Site.xy, ivec2(Reference.yz), ivec2(Sizes.xy) - 1, Site.zw, Reference.x, change);
    // The stub used for behavioural RED must retain the same layout as the real helper.
    float sink = float(textureSize(sampler2D(SceneDepth, LinearClamp), 0).x
        + textureSize(sampler2D(MotionTex, LinearClamp), 0).x
        + textureSize(sampler2D(PrevDepth, LinearClamp), 0).x
        + textureSize(sampler2D(KeyShadowVisibility, LinearClamp), 0).x
        + textureSize(sampler2D(PreviousKeyShadowVisibility, LinearClamp), 0).x) + PreviousJitter.x;
    int channel = int(gl_FragCoord.x);
    Result = channel == 0 ? change : channel == 1 ? (valid ? 1.0 : 0.0)
        : channel == 2 ? (valid && abs(change) > 0.1 ? 1.0 : 0.0)
        : sink * 1e-30;
}
";

    internal TemporalVisibilityProbe()
    {
        IGpuResourceFactory f = Device.Factory;
        _output = Own(f.CreateTexture(GpuTextureDescription.Texture2D(4, 1, GpuPixelFormat.R32Float,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled)));
        _framebuffer = Own(f.CreateFramebuffer(null, _output));
        _uniforms = Own(f.CreateBuffer(new GpuBufferDescription(112, GpuBufferUsage.UniformBuffer)));
        var elements = new List<GpuResourceLayoutElement>();
        foreach (string name in new[] { "SceneDepth", "MotionTex", "PrevDepth", "KeyShadowVisibility", "PreviousKeyShadowVisibility" })
            elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
        elements.Add(new("Probe", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        _layout = Own(f.CreateResourceLayout(new GpuResourceLayoutDescription(elements.ToArray())));
        IGpuShaderSet shaders = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert,
            "#version 450\n" + Header + ShaderSources.TemporalCommonGlsl + ShaderSources.TemporalResolveTuningGlsl
            + ShaderSources.TemporalVisibilityGlsl + Main));
        _pipeline = Own(f.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendAttachments = [GpuBlendAttachment.OverrideBlend],
            DepthStencil = GpuDepthStencilState.Disabled,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, false, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = _framebuffer.Outputs,
        }));
    }

    T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    internal Vector3 Run(VisibilityCase c, bool r8 = false)
    {
        using IGpuTexture depth = Texture(c, GpuPixelFormat.R32Float, c.Depth);
        using IGpuTexture motion = Texture(c, GpuPixelFormat.R16G16Float, new float[c.Width * c.Height * 2]);
        using IGpuTexture previousDepth = Texture(c, GpuPixelFormat.R32Float, c.PreviousDepth);
        using IGpuTexture current = Texture(c, r8 ? GpuPixelFormat.R8UNorm : GpuPixelFormat.R32Float, c.Current);
        using IGpuTexture previous = Texture(c, r8 ? GpuPixelFormat.R8UNorm : GpuPixelFormat.R32Float, c.Previous);
        float[] uniforms = [0, 0, 1, 0, c.Width, c.Height, c.Width, c.Height,
            c.Jitter.X, c.Jitter.Y, 1, 1, c.PreviousJitter.X, c.PreviousJitter.Y, 1, 0,
            c.Uv.X, c.Uv.Y, c.PreviousUv.X, c.PreviousUv.Y, c.ExpectedDepth, c.CentreX, c.CentreY, 0,
            c.Ready ? 1 : 0, c.Background ? 1 : 0, 0, 0];
        Device.UpdateBuffer(_uniforms, 0, uniforms);
        using IGpuResourceSet set = Device.Factory.CreateResourceSet(new GpuResourceSetDescription(_layout,
            depth, motion, previousDepth, current, previous, Device.LinearSampler, _uniforms));
        using IGpuCommandList commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, nameof(TemporalVisibilityProbe)))
        {
            commands.SetFramebuffer(_framebuffer);
            commands.SetPipeline(_pipeline);
            commands.SetGraphicsResourceSet(0, set);
            commands.Draw(3);
        }
        Device.Submit(commands);
        Device.WaitForIdle();
        float[] values = TemporalTextureIo.Read(Device, _output);
        return new(values[0], values[1], values[2]);
    }

    IGpuTexture Texture(VisibilityCase c, GpuPixelFormat format, float[] values)
    {
        IGpuTexture texture = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D((uint)c.Width, (uint)c.Height,
            format, GpuTextureUsage.Sampled | (format == GpuPixelFormat.R8UNorm ? GpuTextureUsage.RenderTarget : 0)));
        if (format == GpuPixelFormat.R8UNorm)
        {
            bool uniform = Array.TrueForAll(values, v => v == values[0]);
            if (uniform)
            {
                using IGpuFramebuffer target = Device.Factory.CreateFramebuffer(null, texture);
                using IGpuCommandList clear = Device.Factory.CreateCommandList();
                using (GpuRecording.Open(Device, clear, nameof(TemporalVisibilityProbe)))
                {
                    clear.SetFramebuffer(target);
                    clear.ClearColorTarget(0, new KhaozEngine.Primitives.Color(values[0], 0, 0, 1));
                }
                Device.Submit(clear); Device.WaitForIdle();
                return texture;
            }
            var bytes = new byte[values.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Math.Clamp(MathF.Round(values[i] * 255), 0, 255);
            Device.UpdateTexture(texture, bytes, 0, 0, (uint)c.Width, (uint)c.Height);
        }
        else TemporalTextureIo.Upload(Device, texture, values);
        return texture;
    }

    internal byte ReadR8Conversion(float value)
    {
        using IGpuTexture texture = Texture(new VisibilityCase("conversion", value, 0), GpuPixelFormat.R8UNorm,
            new[] { value, value, value, value, value, value, value, value, value, value, value, value, value, value, value, value });
        using IGpuTexture staging = Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(4, 4,
            GpuPixelFormat.R8UNorm, GpuTextureUsage.Staging));
        using IGpuCommandList commands = Device.Factory.CreateCommandList();
        using (GpuRecording.Open(Device, commands, nameof(TemporalVisibilityProbe))) commands.CopyTexture(texture, staging);
        Device.Submit(commands); Device.WaitForIdle();
        MappedData map = Device.Map(staging, GpuMapMode.Read);
        try { return Marshal.ReadByte(map.Data); }
        finally { Device.Unmap(staging); }
    }

    public void Dispose()
    {
        Device.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _gpu.Dispose();
    }
}
