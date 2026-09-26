namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A development view of the scene's temporal machinery, selected with <see cref="Scene3D.DebugView"/>. Any value
    /// other than <see cref="None"/> turns temporal rendering on while it is set. A change takes effect at the frame's
    /// first render, and one made after that render waits for the next frame. Not a player setting.
    /// </summary>
    public enum SceneDebugView
    {
        /// <summary>The normal image. The default.</summary>
        None,
        /// <summary>The screen-space motion vectors in place of the final image: hue for direction (rightward cyan,
        /// leftward red, downward violet, upward yellow-green), brightness for magnitude, black for the background
        /// and for no motion. Screen overlays drawn after the post chain still draw over it.</summary>
        MotionVectors,
        /// <summary>The temporal history in place of the final image.</summary>
        History,
        /// <summary>Where the temporal resolve found the previous frame hidden.</summary>
        Disocclusion,
        /// <summary>Where the temporal resolve found transparent content changing the image.</summary>
        Reactive,
    }
}
