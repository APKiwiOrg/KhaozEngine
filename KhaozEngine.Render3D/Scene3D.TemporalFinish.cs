using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The finishing layer over the temporal resolve (docs/design/TEMPORAL-RESOLVE-UPSCALING-DESIGN-2026-09-24.md,
    /// section 4 and amendment 20): the sharpen's test seams. The sharpen runs in the display post chain, which only
    /// the first render of a resolving frame runs, so a later render's chain and a frame without the resolve never
    /// build it. A partial of its own because <c>Scene3D.cs</c> is frozen by the file-size ratchet.
    /// </summary>
    public sealed partial class Scene3D
    {
        /// <summary>Whether the post chain has built the temporal sharpen. Internal, for the tests that pin that a
        /// frame without temporal sharpening builds nothing.</summary>
        internal bool TemporalSharpenBuiltForTests => _post.SharpenBuilt;

        /// <summary>Whether the later renders' post chain has built the temporal sharpen, which it never should.
        /// Internal, for the tests.</summary>
        internal bool LaterRenderSharpenBuiltForTests => _laterRenderPost?.SharpenBuilt == true;

        /// <summary>The post chain's temporal sharpen, null until it is built. Internal, for the tests that pin that it
        /// follows the display targets whenever they are rebuilt.</summary>
        internal TemporalSharpenPass? TemporalSharpenForTests => _post.SharpenForTests;
    }
}
