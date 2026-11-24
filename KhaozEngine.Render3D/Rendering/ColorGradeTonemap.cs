using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

internal sealed class ColorGradeTonemap : IDisposable
{
    const int Size = ColorGradeLut.Size;
    const int TilesPerRow = 8;
    const int Width = TilesPerRow * Size, Height = ColorGradeLut.SampleCount / Width;
    readonly IGpuTexture _atlas;
    readonly IGpuResourceLayout _layout;
    readonly IGpuShaderSet _shaders;
    readonly IGpuPipeline _pipeline;
    readonly Dictionary<IGpuTexture, IGpuResourceSet> _sets = new();

    internal ColorGradeLut Look { get; }

    internal ColorGradeTonemap(IGpuDevice device, ColorGradeLut look, in GpuOutputDescription output)
    {
        Look = look;
        var factory = device.Factory;
        IGpuTexture? atlas = null;
        IGpuResourceLayout? layout = null;
        IGpuShaderSet? shaders = null;
        IGpuPipeline? pipeline = null;
        try
        {
            atlas = factory.CreateTexture(GpuTextureDescription.Texture2D(
                Width, Height, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled));
            device.UpdateTexture(atlas, PackAtlas(look.Samples), 0, 0, Width, Height);
            layout = factory.CreateResourceLayout(new GpuResourceLayoutDescription(T("Src"), S("Samp"), U("Tone"), T("Grade")));
            shaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.ColorGradeTonemap());
            pipeline = TemporalResolveRenderer.Fullscreen(factory, shaders, layout, output);
            shaders.EnsureCompilationReady();
            _atlas = atlas;
            _layout = layout;
            _shaders = shaders;
            _pipeline = pipeline;
        }
        catch
        {
            pipeline?.Dispose();
            shaders?.Dispose();
            layout?.Dispose();
            atlas?.Dispose();
            throw;
        }
    }

    static byte[] PackAtlas(ReadOnlySpan<Vector3> samples)
    {
        var bytes = new byte[Width * Height * 8];
        Span<Half> texels = MemoryMarshal.Cast<byte, Half>(bytes.AsSpan());
        for (int b = 0, i = 0; b < Size; ++b)
        for (int g = 0; g < Size; ++g)
        for (int r = 0; r < Size; ++r)
        {
            int x = b % TilesPerRow * Size + r;
            int y = b / TilesPerRow * Size + g;
            int p = (y * Width + x) * 4;
            Vector3 rgb = samples[i++];
            texels[p] = (Half)rgb.X;
            texels[p + 1] = (Half)rgb.Y;
            texels[p + 2] = (Half)rgb.Z;
            texels[p + 3] = (Half)1;
        }
        return bytes;
    }

    internal void Bind(IGpuDevice device, IPostChainTargets targets, IGpuBuffer toneBuffer)
    {
        void Add(IGpuTexture texture) => _sets.Add(texture, device.Factory.CreateResourceSet(
            new GpuResourceSetDescription(_layout, texture, device.LinearSampler, toneBuffer, _atlas)));
        Add(targets.PingA);
        Add(targets.PingB);
        for (int slot = 0; slot < targets.SourceSlotCount; ++slot) Add(targets.Source(slot));
    }

    internal void Draw(IGpuCommandList cl, IGpuTexture source, IGpuFramebuffer target)
    {
        cl.SetFramebuffer(target);
        cl.SetPipeline(_pipeline);
        cl.SetGraphicsResourceSet(0, _sets[source]);
        cl.Draw(3);
    }

    internal void ReleaseSets(GpuRetireQueue? retired)
    {
        foreach (IGpuResourceSet set in _sets.Values) ResourceRetirement.Free(set, retired);
        _sets.Clear();
    }

    public void Dispose()
    {
        ReleaseSets(null);
        _pipeline.Dispose();
        _shaders.Dispose();
        _layout.Dispose();
        _atlas.Dispose();
    }
}
