using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>The scene's choice of the resolve's entry point (<see cref="TemporalResolvePolicy"/>), made on each
    /// render that runs the resolve. A test can force this scene's own.</summary>
    public sealed partial class Scene3D
    {
        /// <summary>The entry point this scene's resolve records from its next resolving render, or null for the
        /// policy. For tests, such as the cost measurement, which times both on one scene.</summary>
        internal TemporalResolveEntry? TemporalResolveEntryForTests { get; set; }

        // PrepareTemporalResolve's choice, on the render that runs the resolve. A device with too few colour attachments
        // for the split's first pass records the fused entry point, forced or not.
        TemporalResolveEntry ChooseTemporalEntry() =>
            TemporalResolvePolicy.Supported(TemporalResolveEntryForTests ?? TemporalResolvePolicy.Choose(),
                _gd.Capabilities.MaxColorAttachments);
    }
}
