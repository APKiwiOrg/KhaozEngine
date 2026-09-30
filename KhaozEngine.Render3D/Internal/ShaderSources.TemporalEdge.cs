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
        //      spliced at type initialisation, so its declarations, its normal and depth taps and its edge test cannot
        //      drift from the chain's. Three places change. The header comment and the stage interface: the opaque-only
        //      copy joins as a sixth binding after the edge block, and the pass writes two colour outputs, the outlined
        //      lit colour and the outlined opaque copy, from one edge test. The start of main: the UV comes from
        //      gl_FragCoord (upper-left on every backend) times Texel.xy, one over the internal size, so the outlined
        //      images keep ColorTex's orientation, which the resolve reads by texelFetch, and the normal and depth are
        //      read at the same UV with no parity flip. The interpolant stays live at a 1e-30 weight, which changes no
        //      output, so the Direct3D 11 pixel-input signature keeps the TEXCOORD0 then SV_Position shape the other
        //      fullscreen passes ship. And the output: both images take the same edge. Each splice throws at type
        //      initialisation if EdgeFrag stops holding its text exactly once. EdgeFrag is a verbatim string, so its
        //      line breaks are the checkout's, CRLF on a Windows one, and the splices take whichever it uses. ----
        public static readonly string TemporalEdgeFrag = TemporalEdgeProgram(EdgeFrag);

        /// <summary>The outline program spliced from <paramref name="edgeFrag"/>, whose line breaks it keeps. Internal,
        /// for the test that splices a CRLF copy.</summary>
        internal static string TemporalEdgeProgram(string edgeFrag)
        {
            string nl = edgeFrag.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            string source = TemporalEdgeSplice(edgeFrag,
                TemporalEdgeRegion(edgeFrag, "// Texel.xy=1/size", "layout(location=0) out vec4 oColor;" + nl),
                "// Texel.xy=1/internal size, .z=isPerspective, .w=distanceFadeOn. Thresh.x=depth, .y=normal, .z=near," + nl
                + "// .w=far. Fade.x=start, .y=end. Every image is read at this pixel's own centre, so nothing flips." + nl
                + "layout(set=0, binding=5) uniform texture2D OpaqueTex;" + nl
                + "layout(location=0) in vec2 vUvIn;" + nl
                + "layout(location=0) out vec4 oScene;" + nl
                + "layout(location=1) out vec4 oOpaque;" + nl);
            source = TemporalEdgeSplice(source,
                "    vec2 nuv = (Fade.z > 0.5) ? vec2(vUv.x, 1.0 - vUv.y) : vUv;" + nl
                + "    // Up-front, in binding order (Color, Normal, Depth) - see Bug B note above." + nl
                + "    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);" + nl,
                "    vec2 vUv = gl_FragCoord.xy * Texel.xy + vUvIn * 1.0e-30;   // upright: this pixel's centre, internal UV" + nl
                + "    vec2 nuv = vUv;" + nl
                + "    vec4 baseSrc = texture(sampler2D(ColorTex, Samp), vUv);" + nl
                + "    vec4 opaqueSrc = texture(sampler2D(OpaqueTex, Samp), vUv);" + nl);
            return TemporalEdgeSplice(source,
                "    oColor = vec4(mix(base, OutlineColor.rgb, edge), baseSrc.a); // preserve background alpha marker",
                "    oScene = vec4(mix(base, OutlineColor.rgb, edge), baseSrc.a); // preserve background alpha marker" + nl
                + "    oOpaque = vec4(mix(opaqueSrc.rgb, OutlineColor.rgb, edge), opaqueSrc.a);");
        }

        // The text of source from the first character of start to the last of end, each present exactly once.
        static string TemporalEdgeRegion(string source, string start, string end)
        {
            int from = TemporalEdgeFind(source, start), to = TemporalEdgeFind(source, end) + end.Length;
            if (to <= from)
                throw new InvalidOperationException($"EdgeFrag must hold \"{start}\" before \"{end}\" for TemporalEdgeFrag.");
            return source.Substring(from, to - from);
        }

        static string TemporalEdgeSplice(string source, string oldText, string newText)
        {
            int at = TemporalEdgeFind(source, oldText);
            return string.Concat(source.AsSpan(0, at), newText, source.AsSpan(at + oldText.Length));
        }

        static int TemporalEdgeFind(string source, string text)
        {
            int at = source.IndexOf(text, StringComparison.Ordinal);
            if (at < 0 || source.IndexOf(text, at + text.Length, StringComparison.Ordinal) >= 0)
                throw new InvalidOperationException($"EdgeFrag must hold \"{text}\" exactly once for TemporalEdgeFrag.");
            return at;
        }
    }
}
