namespace KhaozEngine.Render3D
{
    /// <summary>Why temporal history was last reset. A reset drops everything carried from the previous frame for one
    /// frame, so that frame renders as if nothing came before it.</summary>
    public enum TemporalResetReason
    {
        /// <summary>No reset has happened.</summary>
        None,
        /// <summary>The first frame rendered with temporal rendering active, since the scene was created or since
        /// temporal rendering was last off.</summary>
        FirstFrame,
        /// <summary>The internal render target changed size.</summary>
        Resize,
        /// <summary>The render scale mode or its factor changed.</summary>
        RenderScale,
        /// <summary>The anti-aliasing selection changed.</summary>
        AntiAliasing,
        /// <summary>The consumer called <c>Scene3D.CameraCut</c>.</summary>
        CameraCutRequested,
        /// <summary>The camera moved or turned further in one frame than the cut thresholds allow.</summary>
        CameraCutDetected,
        /// <summary>The scene rebuilt its colour targets and every pipeline that draws into them for a colour-format
        /// change, which today is the HDR chain being toggled.</summary>
        DeviceReset,
    }
}
