using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Tests.Gpu;

// Test-only matched format control. Both arms compile the unchanged shipped fused resolve.
internal sealed class TemporalVisibilityFormatControl : IDisposable
{
    readonly IGpuDevice _device;
    readonly List<IDisposable> _owned = [];
    readonly IGpuResourceLayout _layout;
    readonly IGpuPipeline _pipeline;
    readonly IGpuFramebuffer _target;
    readonly IGpuBuffer _uniforms, _visibilityUniforms;
    readonly IGpuTexture _colour, _state;
    readonly IGpuTexture _scene, _depth, _motion, _previousDepth, _history, _confidence;
    const int N = 8;

    internal TemporalVisibilityFormatControl(IGpuDevice device, TemporalResolveUniforms uniforms, float ndcDepth)
    {
        _device = device;
        IGpuResourceFactory f = device.Factory;
        _colour = Own(Target(GpuPixelFormat.R16G16B16A16Float));
        _state = Own(Target(GpuPixelFormat.R16G16Float));
        _target = Own(f.CreateFramebuffer(null, _colour, _state));
        _uniforms = Own(f.CreateBuffer(new(TemporalResolveUniforms.SizeInBytes, GpuBufferUsage.UniformBuffer)));
        _visibilityUniforms = Own(f.CreateBuffer(new(16, GpuBufferUsage.UniformBuffer)));
        _device.UpdateBuffer(_uniforms, 0, uniforms);
        _device.UpdateBuffer(_visibilityUniforms, 0, new TemporalVisibilityUniforms { PreviousJitter = new(0, 0, 1, 0) });
        _scene = Own(Input(GpuPixelFormat.R16G16B16A16Float, 4, (pixel, channel) => channel == 3 ? 1 : pixel == 3 * N + 3 ? 0.7f : 0.2f));
        _depth = Own(Input(GpuPixelFormat.R32Float, 1, (_, _) => ndcDepth));
        var qualifiedDepth = new float[N * N]; Array.Fill(qualifiedDepth, 1f);
        qualifiedDepth[3 * N + 3] = ndcDepth;
        TemporalTextureIo.Upload(_device, _depth, qualifiedDepth);
        _motion = Own(Input(GpuPixelFormat.R16G16Float, 2, (_, _) => 0));
        _previousDepth = Own(Input(GpuPixelFormat.R32Float, 1, (_, _) => TemporalResolveMath.LinearDepth(ndcDepth, uniforms.CurrentDepth)));
        _history = Own(Input(GpuPixelFormat.R16G16B16A16Float, 4, (_, channel) => channel == 3 ? 1 : 0.48f));
        _confidence = Own(Input(GpuPixelFormat.R16G16Float, 2, (_, _) => 1));
        var elements = new List<GpuResourceLayoutElement>();
        foreach (string name in new[] { "SceneColor", "OpaqueColor", "SceneDepth", "MotionTex", "PrevDepth", "HistoryColor", "HistoryConfidence" })
            elements.Add(new(name, GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("LinearClamp", GpuResourceKind.Sampler, GpuShaderStages.Fragment));
        elements.Add(new("Resolve", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        elements.Add(new("KeyShadowVisibility", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("PreviousKeyShadowVisibility", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment));
        elements.Add(new("VisibilityHistory", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment));
        _layout = Own(f.CreateResourceLayout(new(elements.ToArray())));
        IGpuShaderSet shader = Own(f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TemporalResolveFrag));
        _pipeline = Own(TemporalResolveRenderer.Fullscreen(f, shader, _layout, _target.Outputs));
    }

    T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    IGpuTexture Target(GpuPixelFormat format) => _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(N, N,
        format, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
    IGpuTexture Input(GpuPixelFormat format, int channels, Func<int, int, float> value)
    {
        IGpuTexture texture = _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(N, N, format, GpuTextureUsage.Sampled));
        var values = new float[N * N * channels];
        for (int i = 0; i < N * N; i++) for (int c = 0; c < channels; c++) values[i * channels + c] = value(i, c);
        TemporalTextureIo.Upload(_device, texture, values);
        return texture;
    }

    internal (float[] Colour, float[] State) Render(float current, float previous, GpuPixelFormat format)
    {
        using IGpuTexture now = Signal(current, format);
        using IGpuTexture before = Signal(previous, format);
        using IGpuResourceSet set = _device.Factory.CreateResourceSet(new(_layout, _scene, _scene, _depth, _motion,
            _previousDepth, _history, _confidence, _device.LinearSampler, _uniforms, now, before, _visibilityUniforms));
        using IGpuCommandList commands = _device.Factory.CreateCommandList();
        using (GpuRecording.Open(_device, commands, nameof(TemporalVisibilityFormatControl)))
        {
            commands.SetFramebuffer(_target); commands.SetPipeline(_pipeline);
            commands.SetGraphicsResourceSet(0, set); commands.Draw(3);
        }
        _device.Submit(commands); _device.WaitForIdle();
        return (TemporalTextureIo.Read(_device, _colour), TemporalTextureIo.Read(_device, _state));
    }

    IGpuTexture Signal(float value, GpuPixelFormat format)
    {
        IGpuTexture texture = Target(format);
        using IGpuFramebuffer target = _device.Factory.CreateFramebuffer(null, texture);
        using IGpuCommandList commands = _device.Factory.CreateCommandList();
        using (GpuRecording.Open(_device, commands, nameof(TemporalVisibilityFormatControl)))
        {
            commands.SetFramebuffer(target);
            commands.ClearColorTarget(0, new KhaozEngine.Primitives.Color(value, 0, 0, 1));
        }
        _device.Submit(commands); _device.WaitForIdle();
        return texture;
    }

    public void Dispose()
    {
        _device.WaitForIdle();
        for (int i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }
}
