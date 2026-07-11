using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>How a screen-space transition covers the frame, so the engine's transition renderer knows which
    /// fullscreen pass to run.</summary>
    public enum ScreenTransitionStyle
    {
        /// <summary>A solid fullscreen fill of <see cref="IScreenTransition.Color"/> at opacity
        /// <see cref="ITransition.Cover"/> (<see cref="HardBlink"/>).</summary>
        Solid,
        /// <summary>A crossfade from the frozen pre-teleport frame to the live view, the frozen frame's weight being
        /// <see cref="ITransition.Cover"/> (<see cref="CameraDissolve"/>).</summary>
        FrozenCrossfade,
    }

    /// <summary>
    /// A screen-space <see cref="ITransition"/> the Render3D scene renders as a fullscreen pass over the final image
    /// (assign it to <c>Scene3D.ScreenTransition</c>; the scene captures the frozen frame and draws the overlay for
    /// you). The engine ships <see cref="HardBlink"/> and <see cref="CameraDissolve"/>; a consumer may implement this
    /// to have its own screen effect auto-rendered by the scene. World-space effects (<see cref="CharDissolve"/>) are
    /// applied per-draw instead and do not implement this.
    /// </summary>
    public interface IScreenTransition : ITransition
    {
        /// <summary>Which built-in fullscreen render the scene runs for this transition.</summary>
        ScreenTransitionStyle Style { get; }

        /// <summary>The fill colour for <see cref="ScreenTransitionStyle.Solid"/> (ignored by
        /// <see cref="ScreenTransitionStyle.FrozenCrossfade"/>, which samples the frozen frame).</summary>
        Color Color { get; }
    }
}
