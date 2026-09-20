namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Where the procedural sky puts its horizon. See <see cref="SkySettings.Horizon"/>.
    /// </summary>
    public enum SkyHorizon
    {
        /// <summary>The historical look and the default. The gradient is a vertical ramp over the SCREEN (horizon
        /// colour at the bottom edge, zenith at the top) and the sky has no ground. It reads under every camera,
        /// including the orthographic iso camera, but it has no notion of where elevation zero is, so a low sun
        /// hangs in mid-sky above a finite world's edge and can only fade out, never set.</summary>
        Screen = 0,

        /// <summary>The gradient is anchored to the WORLD horizon (elevation zero of each pixel's view ray) and the
        /// sky paints <see cref="SkySettings.GroundColor"/> below it, so a finite world appears to run on to the
        /// horizon and the sun disc sets THROUGH that line, occluded by the ground band. Needs a perspective
        /// camera and <see cref="SunAnchor.World"/>. Under an orthographic projection or
        /// <see cref="SunAnchor.StylizedBackdrop"/> the sky falls back to <see cref="Screen"/>, because parallel
        /// view rays have one shared elevation and the stylized disc is not at a physical position to clip.</summary>
        World = 1,
    }
}
