using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Diagnostics;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Internal;

/// <summary>The additional colour pair at exactly twice the display on each axis. Owned by TemporalHistory and
/// indexed by its pair, never independently advanced. Allocation, generation changes and retirement belong to that
/// owner. The Fine resolve reads this pair and updates its write side from the four child targets before post,
/// including on the first frame after a reset.</summary>
internal sealed class TemporalDetailStore(IGpuDevice gd)
{
    readonly IGpuTexture?[] _color = new IGpuTexture?[2];
    TemporalDetailPass? _resolve;

    // The GPU seam has no portable creation refusal type. A managed allocation failure still propagates.
    internal static bool CanFallbackFromCreationFailure(Exception failure) => failure is not OutOfMemoryException;

    internal void BindResolve(TemporalHistory history, IGpuResourceLayout layout, IGpuResourceLayout fineLayout,
        Func<string, string>? fragmentSourceForTests = null) =>
        _resolve ??= new TemporalDetailPass(gd, history, this, layout, fineLayout, fragmentSourceForTests);

    /// <summary>Borrowed live resolve transport for the path-count parity test. Never dispose or mutate.</summary>
    internal IGpuTexture TransportForTests => _resolve!.TransportForTests;

    internal IGpuResourceSet ReadSet(int write) => _resolve!.ReadSet(write);

    internal void Resolve(IGpuCommandList cl, IGpuResourceSet inputs, int write, RenderPassProfiling? profiling) =>
        _resolve!.Resolve(cl, inputs, write, profiling);

    internal void Update(IGpuCommandList cl, int write) => _resolve!.Update(cl, write);

    internal IGpuTexture Color(int index) => _color[index] ?? throw new InvalidOperationException(
        "The detail store has no colour targets. Allocate it together with temporal history.");

    internal void Allocate(int displayWidth, int displayHeight)
    {
        var size = TemporalDetailMath.Extent(displayWidth, displayHeight, gd.Capabilities.MaxRenderTexture2DDimension)
            ?? throw new InvalidOperationException("The detail history extent exceeds the device limit.");
        IGpuResourceFactory f = gd.Factory;
        const GpuTextureUsage usage = GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled;
        for (int i = 0; i < _color.Length; i++)
            _color[i] = f.CreateTexture(GpuTextureDescription.Texture2D((uint)size.Width, (uint)size.Height,
                TemporalFormats.HistoryColor, usage));
    }

    /// <summary>The owner already drained or supplied its completion queue. Release the pass before the textures
    /// it borrows. Each texture is recorded as it is allocated so partial creation can also be released.</summary>
    internal void Release(GpuRetireQueue? retired)
    {
        _resolve?.Release(retired);
        _resolve = null;
        for (int i = 0; i < _color.Length; i++)
            Free(ref _color[i], retired);
    }
}
