namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A development view of the scene's temporal machinery, selected with <see cref="Scene3D.DebugView"/>.
    /// <see cref="MotionVectors"/> turns temporal rendering on while it is set. The other three show the temporal
    /// resolve's decisions and take effect only under <see cref="AntiAliasing.Temporal"/>. A change takes effect at the
    /// frame's first render, and one made after that render waits for the next frame. Not a player setting.
    /// </summary>
    public enum SceneDebugView
    {
        /// <summary>The normal image. The default.</summary>
        None,
        /// <summary>The screen-space motion vectors in place of the final image: hue for direction (rightward cyan,
        /// leftward red, downward violet, upward yellow-green), brightness for magnitude, black for the background
        /// and for no motion. Screen overlays drawn after the post chain still draw over it.</summary>
        MotionVectors,
        /// <summary>The resolve's history: black where history was just reset, white at the full accumulated
        /// weight, green where thin feature retention holds a pixel against clipping. Takes effect only under
        /// <see cref="AntiAliasing.Temporal"/>.</summary>
        History,
        /// <summary>Red where the resolve rejected history because the pixel was hidden last frame or reprojected off
        /// screen, over the dimmed scene. Takes effect only under <see cref="AntiAliasing.Temporal"/>.</summary>
        Disocclusion,
        /// <summary>Yellow where the reactive estimate lowered the history weight for transparent content, over the
        /// dimmed scene. Takes effect only under <see cref="AntiAliasing.Temporal"/>.</summary>
        Reactive,
    }
}
