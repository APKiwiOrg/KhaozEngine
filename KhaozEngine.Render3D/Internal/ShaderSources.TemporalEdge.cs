using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The edge outline the temporal resolve reads. Part of the <see cref="ShaderSources"/> partial: see
    /// ShaderSources.cs for the shared contract. It pairs with <see cref="FullscreenVert"/>.
    /// </summary>
    internal static partial class ShaderSources
    {
        // ---- The toon edge outline for the pass PixelPostProcess runs ahead of the temporal resolve. It is EdgeFrag
        //      with two lines changed, spliced here so the edge test cannot drift from the chain's. The UV comes from
        //      gl_FragCoord (upper-left on every backend) times Texel.xy, one over the internal size, so the outlined
        //      image keeps ColorTex's orientation, which the resolve reads by texelFetch, and the normal and depth are
        //      read at the same UV with no parity flip. The interpolant stays live at a 1e-30 weight, which changes no
        //      output, so the Direct3D 11 pixel-input signature keeps the TEXCOORD0 then SV_Position shape the other
        //      fullscreen passes ship. The splice throws at type initialisation if EdgeFrag stops holding either line
        //      exactly once. ----
        public static readonly string TemporalEdgeFrag = TemporalEdgeSplice(TemporalEdgeSplice(EdgeFrag,
            "layout(location=0) in vec2 vUv;",
            "layout(location=0) in vec2 vUvIn;"),
            "    vec2 nuv = (Fade.z > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;",
            "    vec2 vUv = gl_FragCoord.xy * Texel.xy + vUvIn * 1.0e-30;   // upright: this pixel's centre, internal UV\n"
            + "    vec2 nuv = vUv;");

        static string TemporalEdgeSplice(string source, string oldText, string newText)
        {
            int at = source.IndexOf(oldText, StringComparison.Ordinal);
            if (at < 0 || source.IndexOf(oldText, at + oldText.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException($"EdgeFrag must hold \"{oldText}\" exactly once for TemporalEdgeFrag.");
            return string.Concat(source.AsSpan(0, at), newText, source.AsSpan(at + oldText.Length));
        }
    }
}
