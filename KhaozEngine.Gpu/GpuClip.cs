using System.Numerics;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Adapts a world-&gt;clip view-projection to the live backend's clip-space convention. Backends
    /// disagree on the clip-space Y direction (Vulkan inverts it relative to Metal/D3D11), so a matrix authored
    /// for one backend renders vertically flipped on another unless compensated. Apply <see cref="Correct"/> at
    /// the point a view-projection is handed to the GPU - uploaded to a UBO, or used to transform vertices into
    /// clip space - NOT to matrices used for CPU-side world/screen / picking math, which must stay in the
    /// authored convention (an earlier unconditional flip broke both the render and picking on Metal).
    /// </summary>
    /// <remarks>
    /// Depth range is intentionally not remapped: every currently-supported backend (Metal, D3D11, Vulkan) uses a
    /// [0,1] NDC depth range (<see cref="GpuCapabilities.DepthRangeZeroToOne"/> == true); only legacy OpenGL would
    /// differ, and it is not a supported backend. On Metal (the only backend the golden snapshot tests exercise)
    /// <see cref="GpuCapabilities.ClipSpaceYInverted"/> is false, so <see cref="Correct"/> is the identity and
    /// rendering is byte-identical. The Y-flip branch activates on inverted-Y backends (Vulkan), following the
    /// <see cref="GpuCapabilities.ClipSpaceYInverted"/> contract, and it is not yet validated on non-Metal
    /// hardware.
    /// </remarks>
    public static class GpuClip
    {
        /// <summary>Returns <paramref name="viewProj"/> unchanged when the backend's clip-space Y matches the
        /// authored (Metal/D3D) convention, or with clip-space Y negated when
        /// <see cref="GpuCapabilities.ClipSpaceYInverted"/> is set.</summary>
        public static Matrix4x4 Correct(Matrix4x4 viewProj, GpuCapabilities caps) =>
            caps.ClipSpaceYInverted ? viewProj * Matrix4x4.CreateScale(1f, -1f, 1f) : viewProj;
    }
}
