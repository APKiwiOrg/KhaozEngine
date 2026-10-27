using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Owns the single-sample pre-water copy, or borrows the scene's MSAA resolve destination.</summary>
internal sealed class WaterColumnColorSource : IDisposable
{
    readonly IGpuDevice _device;
    readonly GpuRetireQueue _retired;
    readonly IGpuTexture _placeholder;
    IGpuTexture? _copy;
    RenderResources? _targets;
    IGpuTexture? _color;
    int _targetGeneration, _width, _height, _samples;
    GpuPixelFormat _format;
    bool _wanted;

    public IGpuTexture Texture { get; private set; }
    public int Generation { get; private set; }

    public WaterColumnColorSource(IGpuDevice device, GpuRetireQueue retired)
    {
        _device = device;
        _retired = retired;
        _placeholder = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
        try { device.UpdateTexture(_placeholder, new byte[4], 0, 0, 1, 1); }
        catch { _placeholder.Dispose(); throw; }
        Texture = _placeholder;
    }

    public void Prepare(RenderResources targets, bool wanted)
    {
        if (ReferenceEquals(_targets, targets) && _targetGeneration == targets.Generation
            && ReferenceEquals(_color, targets.ColorTex) && _width == targets.Width && _height == targets.Height
            && _format == targets.ColorTex.Format && _samples == targets.SampleCount && _wanted == wanted) return;

        // Allocate before publishing or retiring anything, so a failed replacement preserves the current source.
        IGpuTexture? copy = wanted && !targets.Msaa
            ? _device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                (uint)targets.Width, (uint)targets.Height, targets.ColorTex.Format, GpuTextureUsage.Sampled))
            : null;
        _retired.Retire(_copy);
        _copy = copy;
        Texture = wanted ? copy ?? targets.ColorTex : _placeholder;
        _targets = targets;
        _targetGeneration = targets.Generation;
        _color = targets.ColorTex;
        _width = targets.Width;
        _height = targets.Height;
        _format = targets.ColorTex.Format;
        _samples = targets.SampleCount;
        _wanted = wanted;
        Generation++;
    }

    public WaterColumnCopyPath Record(IGpuCommandList cl, RenderResources targets, bool wanted)
    {
        // Legacy unbound water can replace its targets after either preparation form.
        Prepare(targets, wanted);
        if (!wanted) return WaterColumnCopyPath.None;
        if (targets.Msaa)
        {
            cl.ResolveTexture(targets.MsColor!, targets.ColorTex);
            return WaterColumnCopyPath.MsaaResolve;
        }
        // The source may carry a full mip chain. Only its base level belongs in this single-mip destination.
        cl.CopyTextureSubresource(targets.ColorTex, 0, 0, Texture, (uint)targets.Width, (uint)targets.Height);
        return WaterColumnCopyPath.SingleSampleCopy;
    }

    public void Dispose()
    {
        _copy?.Dispose();
        _placeholder.Dispose();
    }
}
