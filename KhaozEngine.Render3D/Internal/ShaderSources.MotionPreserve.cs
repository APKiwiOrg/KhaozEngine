namespace KhaozEngine.Render3D.Internal;

/// <summary>
/// The temporal variants of the transparent passes that draw into the model framebuffer: textured billboards, beams,
/// trails, overlay meshes and silhouettes (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 4). Each is its base fragment
/// byte for byte plus a fourth output written zero, which the pipeline's PreserveDestination blend discards, the pattern
/// those passes already use for the normal and depth attachments. The motion of the opaque surface behind a transparent
/// pass therefore survives it, and round 2's reactive estimate handles the transparent content itself. Part of the
/// <see cref="ShaderSources"/> partial.
/// </summary>
internal static partial class ShaderSources
{
    static string PreservingMotion(string fragment) => ShaderText.BeforeEndOfMain(
        ShaderText.After(fragment, DepthOutputGlsl, "\nlayout(location=3) out vec4 oMotion;"),
        "    oMotion = vec4(0.0);   // discarded (PreserveDestination blend on attachment 3)\n");

    public static readonly string TexturedBillboardMotionFrag = PreservingMotion(TexturedBillboardFrag);
    public static readonly string BeamMotionFrag = PreservingMotion(BeamFrag);
    public static readonly string TrailMotionFrag = PreservingMotion(TrailFrag);
    public static readonly string OverlayUnlitMotionFrag = PreservingMotion(OverlayUnlitFrag);
    public static readonly string SilhouetteMotionFrag = PreservingMotion(SilhouetteFrag);
}
