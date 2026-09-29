using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>The scene's choice of the resolve's entry point (<see cref="TemporalResolvePolicy"/>), made on each
    /// render that runs the resolve, from the device's backend and whether the internal targets are smaller than the
    /// display. A test can force this scene's own.</summary>
    public sealed partial class Scene3D
    {
        /// <summary>The entry point this scene's resolve records from its next resolving render, or null for the
        /// policy. For tests, such as the cost measurement, which times both on one scene.</summary>
        internal TemporalResolveEntry? TemporalResolveEntryForTests { get; set; }

        // PrepareTemporalResolve's choice, on the render that runs the resolve, once the internal targets have the
        // frame's size: the preset's ratio or an explicit one, times the render cap.
        TemporalResolveEntry ChooseTemporalEntry(int displayWidth, int displayHeight) =>
            TemporalResolveEntryForTests ?? TemporalResolvePolicy.Choose(_gd.Backend,
                TemporalResolvePolicy.Upscales(_res.Width, _res.Height, displayWidth, displayHeight));
    }
}
