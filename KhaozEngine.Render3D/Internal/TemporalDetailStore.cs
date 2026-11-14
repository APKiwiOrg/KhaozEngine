using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The additional colour pair at exactly twice the display on each axis. Owned by TemporalHistory and
/// indexed by its pair, never independently advanced. Allocation, generation changes and retirement belong to that
/// owner. Nothing samples these textures yet. Replication writes the resolve's history-space colour before post.</summary>
internal sealed class TemporalDetailStore(IGpuDevice gd)
{
    readonly IGpuTexture?[] _color = new IGpuTexture?[2];
    readonly IGpuFramebuffer?[] _framebuffers = new IGpuFramebuffer?[2];
    readonly IGpuResourceSet?[] _sets = new IGpuResourceSet?[2];
    IGpuShaderSet? _shaders;
    IGpuResourceLayout? _layout;
    IGpuSampler? _sampler;
    IGpuBuffer? _parameters;
    IGpuPipeline? _pipeline;

    internal IGpuTexture Color(int index) => _color[index] ?? throw new InvalidOperationException(
        "The detail store has no colour targets. Allocate it together with temporal history.");

    internal void Allocate(TemporalHistory history, int displayWidth, int displayHeight)
    {
        var size = TemporalDetailMath.Extent(displayWidth, displayHeight, gd.Capabilities.MaxRenderTexture2DDimension)
            ?? throw new InvalidOperationException("The detail history extent exceeds the device limit.");
        IGpuResourceFactory f = gd.Factory;
        // Reuse the shipped blit with nearest sampling, no colour filter arithmetic, and its alpha-preserving mode.
        // The 2:1 destination centres map to quarter and three-quarter positions inside each source texel. Both
        // select that exact stored texel. Flip V to preserve the history's physical row order on every backend.
        _shaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.BlitFrag);
        _layout = f.CreateResourceLayout(new GpuResourceLayoutDescription(T("Src"), S("Samp"), U("Final")));
        _sampler = f.CreateSampler(GpuSamplerDescription.Point);
        _parameters = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));
        _pipeline = TemporalResolveRenderer.Fullscreen(f, _shaders, _layout,
            new GpuOutputDescription(null, TemporalFormats.HistoryColor));
        const GpuTextureUsage usage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
        for (int i = 0; i < 2; i++)
        {
            _color[i] = f.CreateTexture(GpuTextureDescription.Texture2D((uint)size.Width, (uint)size.Height,
                TemporalFormats.HistoryColor, usage));
            _framebuffers[i] = f.CreateFramebuffer(null, Color(i));
            _sets[i] = f.CreateResourceSet(new GpuResourceSetDescription(_layout, history.Color(i), _sampler, _parameters));
        }
    }

    /// <summary>Before the first framebuffer of the render opens, like every post uniform upload.</summary>
    internal void Prepare(IGpuCommandList cl)
    {
        var parameters = new Vector4(1f, 1f, 0f, 0f);
        cl.UpdateBuffer(_parameters!, 0, in parameters);
    }

    /// <summary>Only after the frame's first resolve. Writes its display colour to all four owned texels. On a
    /// reset this seeds the new write side, which is the only side the next frame may read.</summary>
    internal void Replicate(IGpuCommandList cl, int writeIndex)
    {
        cl.SetFramebuffer(_framebuffers[writeIndex]!);
        cl.SetPipeline(_pipeline!);
        cl.SetGraphicsResourceSet(0, _sets[writeIndex]!);
        cl.Draw(3);
    }

    /// <summary>The history owner already drained or supplied its completion queue. Sets go first because they
    /// borrow the owner's display targets. Every field is recorded as it is allocated, including partial creation.</summary>
    internal void Release(GpuRetireQueue? retired)
    {
        for (int i = 0; i < 2; i++)
        {
            Free(ref _sets[i], retired);
            Free(ref _framebuffers[i], retired);
            Free(ref _color[i], retired);
        }
        Free(ref _pipeline, retired);
        Free(ref _parameters, retired);
        Free(ref _sampler, retired);
        Free(ref _layout, retired);
        Free(ref _shaders, retired);
    }
}
