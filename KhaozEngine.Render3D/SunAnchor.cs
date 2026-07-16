namespace KhaozEngine.Render3D
{
    /// <summary>
    /// How the procedural sky places its sun disc on screen (see <see cref="SkySettings.Anchor"/>). A single-choice
    /// selector like <see cref="ShadowMode"/>:
    /// <list type="bullet">
    /// <item><see cref="World"/> - the default. The disc is anchored to the WORLD-SPACE sun direction via a true
    ///   point-at-infinity projection (rotate the world sun direction into view space, project through the camera
    ///   projection, perspective-divide), and drawn only when the sun is in front of the camera. Orbiting the camera
    ///   keeps the disc fixed relative to world directions (it sits where the sun really is, over the world features
    ///   the light agrees with); a pure camera translation never moves it. This is the physically-correct placement
    ///   for a perspective camera (the follow/fly cameras). Under an orthographic camera a directional sun is a point
    ///   at infinity with no finite screen position (all view rays are parallel), so the disc resolves to nothing -
    ///   use <see cref="StylizedBackdrop"/> for the ortho iso look.</item>
    /// <item><see cref="StylizedBackdrop"/> - the legacy stylized placement. The disc sits at the sun direction's
    ///   view-space (right, up) components read directly as screen NDC (no perspective divide, visible whenever the
    ///   sun is above the view horizon). Not a physical projection, but it keeps the disc agreeing with the light
    ///   AZIMUTH for BOTH the orthographic iso camera (where the world projection degenerates) and the perspective
    ///   follow camera, which is what a stylized backdrop wants. Pick this for a decorative sky that should read the
    ///   same under an iso camera, or to reproduce the pre-<c>World</c> behaviour exactly.</item>
    /// </list>
    /// </summary>
    public enum SunAnchor
    {
        /// <summary>World-space point-at-infinity projection (default): the disc tracks the real sun direction and is
        /// suppressed when the sun is behind the camera. Correct for perspective cameras; degenerate under ortho.</summary>
        World,
        /// <summary>Legacy stylized backdrop: the sun's view-space (right, up) read directly as screen NDC, visible
        /// above the view horizon. Works under both the ortho iso camera and the perspective camera; not a physical
        /// projection.</summary>
        StylizedBackdrop,
    }
}
