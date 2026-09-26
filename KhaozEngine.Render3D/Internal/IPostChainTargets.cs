using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// THE TARGETS ONE RUN OF THE POST CHAIN READS AND WRITES. <see cref="RenderResources"/> is the internal-resolution
    /// chain every frame without the temporal resolve runs. <c>TemporalPostTargets</c> is the display-resolution
    /// chain after the resolve (TEMPORAL-RESOLVE-UPSCALING-DESIGN section 1). <see cref="Rendering.PixelPostProcess"/>
    /// builds its resource sets over whichever it is handed.
    /// <para><b>THE SOURCE IS A SLOT, NOT A TEXTURE.</b> The temporal chain's source is the history target the resolve
    /// wrote this frame, and the two history targets alternate. The chain builds its source-reading sets once per
    /// <see cref="Generation"/> for every slot, and <see cref="SourceSlot"/> picks one per frame, so a steady frame builds
    /// and allocates nothing.</para>
    /// <para><b>THE EDGE PASS READS THE INTERNAL ATTACHMENTS ON EITHER CHAIN.</b> <see cref="NormalTex"/> and
    /// <see cref="DepthColorTex"/> are always the model pass's own, at the internal size, sampled at normalised
    /// coordinates, so the outline lands where it did.</para>
    /// </summary>
    internal interface IPostChainTargets
    {
        /// <summary>The chain's resolution: its source and its ping pair.</summary>
        int Width { get; }
        int Height { get; }
        /// <summary>Changes whenever any texture this reports is replaced, including a source slot's.</summary>
        int Generation { get; }
        /// <summary>How many textures can be the chain's source: 1, or 2 for the temporal chain.</summary>
        int SourceSlotCount { get; }
        /// <summary>The slot holding this frame's image. Read when the chain runs, after the resolve.</summary>
        int SourceSlot { get; }
        IGpuTexture Source(int slot);
        IGpuTexture NormalTex { get; }
        IGpuTexture DepthColorTex { get; }
        IGpuTexture PingA { get; }
        IGpuTexture PingB { get; }
        IGpuFramebuffer PingAFB { get; }
        IGpuFramebuffer PingBFB { get; }
        bool BloomAllocated { get; }
        IGpuTexture? BloomA { get; }
        IGpuTexture? BloomB { get; }
        IGpuFramebuffer? BloomAFB { get; }
        IGpuFramebuffer? BloomBFB { get; }
        int BloomWidth { get; }
        int BloomHeight { get; }
        bool DistortAllocated { get; }
        IGpuTexture? DistortTex { get; }
    }
}
