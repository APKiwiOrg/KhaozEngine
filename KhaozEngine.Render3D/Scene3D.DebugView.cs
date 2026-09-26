using System;

namespace KhaozEngine.Render3D
{
    /// <summary>The debug view (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 5). This half holds the
    /// selection and turns temporal rendering on. Drawing each view is the work of the pass that owns its data.</summary>
    public sealed partial class Scene3D
    {
        SceneDebugView _debugView;

        /// <summary>
        /// A development view that replaces the final image with a view of the temporal machinery.
        /// <see cref="SceneDebugView.None"/>, the default, renders normally. Any other value turns temporal rendering on
        /// for as long as it is set, which jitters the rasterised image by under half an internal pixel on each axis
        /// each frame. Not a player setting.
        /// <para>
        /// The selection counts per frame. A change takes effect at the frame's first render, including one made
        /// between <see cref="Begin"/> and that render. A change made after that render waits for the next frame, so
        /// a second render inside the same frame keeps the first render's temporal state.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="SceneDebugView"/>.</exception>
        public SceneDebugView DebugView
        {
            get => _debugView;
            set
            {
                if (value < SceneDebugView.None || value > SceneDebugView.Reactive)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined SceneDebugView.");
                _debugView = value;
            }
        }
    }
}
