using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Real target resources with independently controlled source-slot admission.</summary>
internal sealed class PostChainTestTargets(IPostChainTargets inner, int slots) : IPostChainTargets
{
    public int Width => inner.Width;
    public int Height => inner.Height;
    public int Generation => inner.Generation;
    public int SourceSlotCount => slots;
    public int SourceSlot { get; set; }
    public IGpuTexture Source(int slot) => inner.Source(0);
    public IGpuTexture NormalTex => inner.NormalTex;
    public IGpuTexture DepthColorTex => inner.DepthColorTex;
    public IGpuTexture PingA => inner.PingA;
    public IGpuTexture PingB => inner.PingB;
    public IGpuFramebuffer PingAFB => inner.PingAFB;
    public IGpuFramebuffer PingBFB => inner.PingBFB;
    public bool BloomAllocated => inner.BloomAllocated;
    public IGpuTexture? BloomA => inner.BloomA;
    public IGpuTexture? BloomB => inner.BloomB;
    public IGpuFramebuffer? BloomAFB => inner.BloomAFB;
    public IGpuFramebuffer? BloomBFB => inner.BloomBFB;
    public int BloomWidth => inner.BloomWidth;
    public int BloomHeight => inner.BloomHeight;
    public bool DistortAllocated => inner.DistortAllocated;
    public IGpuTexture? DistortTex => inner.DistortTex;
}
