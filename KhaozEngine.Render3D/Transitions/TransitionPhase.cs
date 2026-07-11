namespace KhaozEngine.Render3D
{
    /// <summary>The lifecycle phases a teleport transition moves through. See <see cref="ITransition"/>.</summary>
    public enum TransitionPhase
    {
        /// <summary>Not started (or reset). Fully revealed - the live view.</summary>
        Idle,
        /// <summary>Covering the screen / dissolving the avatar out. The swap happens when this completes.</summary>
        Cover,
        /// <summary>Fully covered, waiting for the destination to stream in (bounded by a timeout). Screen-space
        /// effects hold here; world-space effects skip it.</summary>
        Hold,
        /// <summary>Uncovering the screen / dissolving the avatar back in.</summary>
        Reveal,
        /// <summary>Finished - fully revealed again.</summary>
        Done,
    }
}
