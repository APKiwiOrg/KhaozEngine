using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>The scene's choice between the single-pass and the two-pass resolve
    /// (<see cref="TemporalResolvePath"/>). The process default holds unless a test sets this scene's own.</summary>
    public sealed partial class Scene3D
    {
        bool? _temporalSplitForTests;

        /// <summary>True for the two-pass resolve, false for the single pass, null for the process default. For tests,
        /// such as the cost measurement, which times both on one scene. It applies from the next resolving
        /// frame.</summary>
        internal bool? TemporalResolveSplitForTests
        {
            get => _temporalSplitForTests;
            set
            {
                _temporalSplitForTests = value;
                if (value is { } split && _temporalResolve is { } resolve) resolve.Split = split;
            }
        }

        // The scene's renderer, created on its first resolving frame with this scene's choice.
        TemporalResolveRenderer NewTemporalResolveRenderer()
        {
            var resolve = new TemporalResolveRenderer(_gd);
            if (_temporalSplitForTests is { } split) resolve.Split = split;
            return resolve;
        }
    }
}
