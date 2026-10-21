using System;
using System.Linq;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// D AS SHIPPED AT 583a3ae7b, FROZEN: the text Task 7f.3 (R3-lite, the current mask) changed outside the visibility
/// helper and the call site, copied verbatim from <c>ShaderSources.TemporalPrepare.cs</c>,
/// <c>ShaderSources.TemporalSplit.cs</c> and <c>ShaderSources.TemporalResolve.cs</c>. The mask is built in the split's
/// first pass and the fused entry's 3x3 loop, carried in the surface struct and stored in the surface target's fourth
/// channel, so D's surface struct and prepare signature, its store and read-back, the first pass's 3x3 loop and the
/// fused entry are held here. <see cref="TemporalVisibilityFrozenD.ToD"/> restores them through <see cref="Restore"/>
/// and drops the mask functions, so every program the D rig compiles is the program D shipped, which
/// <see cref="ProgramSha256"/> pins. Never edit the texts or their pins. Each piece is found between a first and a
/// last anchor that D and production share, which may move with production.
/// </summary>
internal static class TemporalVisibilityFrozenDMask
{
    /// <summary>One frozen piece: D's text from the start of <paramref name="FirstLine"/> to the end of the first
    /// <paramref name="LastLine"/> after it, and its SHA-256 over LF UTF-8 at 583a3ae7b.</summary>
    internal sealed record Hunk(string Name, string D, string Sha256, string FirstLine, string LastLine);
    /// <summary>D's surface struct, its comment and temporalPrepareSurface's signature.</summary>
    internal static readonly Hunk Surface = new(nameof(Surface), @"// What every display pixel whose 3x3 is centred on a texel reprojects by, from that texel's 3x3 alone: the motion and
// whether it reprojects as background, by the camera's rotation (temporalPreviousUv), the expected depth and whether
// the depth test runs (step 3), steps 1 and 3's marks (nearerMoved, band, followed, ownReprojected), whether the fast
// feature is narrow (narrowMoving, step 5), and step 6's edge signal (movingEdge and edgeMotion).
struct TemporalSurface {
    vec2 motion; bool background; float expectedDepth; bool depthTested; bool nearerMoved; bool band; bool followed;
    bool ownReprojected; bool narrowMoving; bool movingEdge; float edgeMotion;
};

TemporalSurface temporalPrepareSurface(ivec2 centreTexel, ivec2 closestTexel, float closestDepth, ivec2 maxTexel) {
    vec2 internalSize = Sizes.xy;",
        "c0248c9489eb8e3c2352310679b832c23d5f0e5d23c624363420b685d8a465c8",
        @"// What every display pixel whose 3x3 is centred on a texel reprojects by, from that texel's 3x3 alone: the motion and",
        @"    vec2 internalSize = Sizes.xy;");
    /// <summary>The end of temporalPrepareSurface, which sets no mask.</summary>
    internal static readonly Hunk Prepared = new(nameof(Prepared), @"    surface.edgeMotion = edgeMotion;
    return surface;",
        "f282121d9bd40a66b1a2fe9f5266322e03ec5f4288ff03d50c92282082efd04b",
        @"    surface.edgeMotion = edgeMotion;",
        @"    return surface;");
    /// <summary>The comment on the split's surface targets.</summary>
    internal static readonly Hunk StoreComment = new(nameof(StoreComment), @"// The split stores a texel's surface for its second pass in three targets: the motion and the flags in a half-float
// target, which holds the motion target's own half floats and a whole number below 256 exactly, and the expected depth
// and the edge motion in single-float targets.
const int SurfaceBackground = 1;",
        "c6fd88b5e804d3f13dc5d5f56d7161670fcf126c3268f88c38ebca504c5367eb",
        @"// The split stores a texel's surface for its second pass in three targets: the motion",
        @"const int SurfaceBackground = 1;");
    /// <summary>temporalStoreSurface's fourth channel, zero.</summary>
    internal static readonly Hunk Store = new(nameof(Store), @"        | (s.movingEdge ? SurfaceMovingEdge : 0);
    return vec4(s.motion, float(flags), 0.0);
}",
        "86a49046023beb8ebe864d5483bb5b9dc306c458ce3417f27ff3b439ebd6dab1",
        @"        | (s.movingEdge ? SurfaceMovingEdge : 0);",
        @"}");
    /// <summary>The end of temporalStoredSurface, which reads no mask.</summary>
    internal static readonly Hunk Stored = new(nameof(Stored), @"    s.edgeMotion = edgeMotion;
    return s;",
        "41c02bbf2abfc635e5bc3214af21cc10c06d5bd0baee082746228f2e943bf661",
        @"    s.edgeMotion = edgeMotion;",
        @"    return s;");
    /// <summary>The split's first pass, its 3x3 loop and its own view depth.</summary>
    internal static readonly Hunk PassOne = new(nameof(PassOne), @"    ivec2 closestTexel = texel;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 at = temporalNeighbourTexel(texel, x, y, maxTexel);
            temporalDilate(at, temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), at, 0).rg,
                texelFetch(sampler2D(SceneDepth, LinearClamp), at, 0).r), closestDepth, closestTexel);
        }
    }
    TemporalSurface surface = temporalPrepareSurface(texel, closestTexel, closestDepth, maxTexel);
    float viewDepth = temporalViewDepth(texelFetch(sampler2D(MotionTex, LinearClamp), texel, 0).rg,
        texelFetch(sampler2D(SceneDepth, LinearClamp), texel, 0).r);
    oPrepared = temporalPrepared(ycc, reactiveDifference);",
        "7d1a83bf0b08d3d4e51f6d39e644bb2faa7a39a68edba09536b2e180abaed383",
        @"    ivec2 closestTexel = texel;",
        @"    oPrepared = temporalPrepared(ycc, reactiveDifference);");
    /// <summary>The fused entry, temporalResolvePixel, with its 3x3 loop.</summary>
    internal static readonly Hunk FusedEntry = new(nameof(FusedEntry), @"// The fused entry point: one display pixel, its 3x3 read from the scene's inputs and prepared inline
// (temporalWeighted, temporalReactiveDifference, temporalDilate), gathered (temporalGather), its centre texel's
// surface prepared (temporalPrepareSurface), and the accumulation (temporalAccumulatePixel).
TemporalPixel temporalResolvePixel(ivec2 displayPixel) {
    vec2 uv;
    vec2 pixelCentre;
    ivec2 maxTexel;
    ivec2 centreTexel;
    temporalPixelSite(displayPixel, uv, pixelCentre, maxTexel, centreTexel);
    TemporalKernels kernels = temporalKernels(pixelCentre, centreTexel, maxTexel);
    TemporalNeighbourhood neighbourhood = temporalNeighbourhood();
    float closestDepth = 2.0 * BackgroundLinearDepth;
    ivec2 closestTexel = centreTexel;
    afloat lumas[9];

    ivec2 nextTexel = temporalFirstTexel(centreTexel, maxTexel);
    vec4 nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
    vec2 nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
    float nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
    vec3 nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            ivec2 texel = nextTexel;
            vec4 sceneColor = nextScene;
            vec2 motion = nextMotion;
            float ndcDepth = nextDepth;
            vec3 opaqueColor = nextOpaque;
            nextTexel = temporalNextTexel(centreTexel, x, y, maxTexel);
            nextScene = texelFetch(sampler2D(SceneColor, LinearClamp), nextTexel, 0);
            nextMotion = texelFetch(sampler2D(MotionTex, LinearClamp), nextTexel, 0).rg;
            nextDepth = texelFetch(sampler2D(SceneDepth, LinearClamp), nextTexel, 0).r;
            nextOpaque = texelFetch(sampler2D(OpaqueColor, LinearClamp), nextTexel, 0).rgb;
            vec3 weightedColor = temporalWeighted(sceneColor.rgb);
            vec4 prepared = temporalPrepared(rgbToYCoCg(weightedColor),
                temporalReactiveDifference(weightedColor, opaqueColor));
            lumas[(y + 1) * 3 + (x + 1)] = toAfloat(prepared.x);
            temporalGather(neighbourhood, kernels, x + 1, y + 1, toAvec3(prepared.xyz), toAfloat(sceneColor.a),
                toAfloat(prepared.w));
            temporalDilate(texel, temporalViewDepth(motion, ndcDepth), closestDepth, closestTexel);
        }
    }

    TemporalSurface surface = temporalPrepareSurface(centreTexel, closestTexel, closestDepth, maxTexel);
    bool ridge = temporalRidge(lumas[0], lumas[1], lumas[2], lumas[3], lumas[4], lumas[5], lumas[6], lumas[7],
        lumas[8]);
    return temporalAccumulatePixel(uv, centreTexel, maxTexel, neighbourhood, kernels, ridge, surface);",
        "af29feba6c7e0119e8299214d8172345cd8415c448513eca1e0621d910ede23d",
        @"// The fused entry point: one display pixel, its 3x3 read from the scene's inputs and prepared inline",
        @"    return temporalAccumulatePixel(uv, centreTexel, maxTexel, neighbourhood, kernels, ridge, surface);");
    /// <summary>The pieces in <c>TemporalPrepareGlsl</c>, which every program that prepares or reads a surface
    /// carries.</summary>
    internal static readonly Hunk[] SurfaceHunks = [Surface, Prepared, StoreComment, Store, Stored];

    /// <summary>Every frozen piece.</summary>
    internal static readonly Hunk[] All = [.. SurfaceHunks, PassOne, FusedEntry];

    /// <summary>SHA-256 over LF UTF-8 of every fragment program the resolve renderer and its split compile, as D
    /// shipped them at 583a3ae7b, read from that commit's build.</summary>
    internal static readonly (string Program, string Sha256)[] ProgramSha256 =
    [
        (nameof(ShaderSources.TemporalPrepareFrag), "81e0144e8741fafcbe5969c8bfc4d8346280f5a7793bd0a52d048a09aa8b6061"),
        (nameof(ShaderSources.TemporalResolveFrag), "1b9297b40e9897c7882ab9d3359048f934ad461876f1383913f1f074386e7c47"),
        (nameof(ShaderSources.TemporalResolveHalfFrag), "ea014b5ca452bd39af9778e31f1a379958b829a21a0c9d40bff6a187cf5fb64f"),
        (nameof(ShaderSources.TemporalAccumulateFrag), "7faa5e7e995b231ee7756b8e45387306fb8d6dd0a0de3414a02be8b2fadf93f8"),
        (nameof(ShaderSources.TemporalAccumulateHalfFrag), "34d7149f0fb54c85a5dd7aa4147d94e15b3820412c80a4660c15fa96bb87e216"),
        (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeFrag), "90c0fb2f522042362ce0d75758fb4edc8ebc2cfc35bdf1888dc2bb9a07bffbfa"),
        (nameof(ShaderSources.TemporalAccumulateAtDisplaySizeHalfFrag), "717433bef068794c7cb6d0c69f02851e9cfafc0d56bb62e7a34956dc510e6fa5"),
        (nameof(ShaderSources.TemporalDepthStoreFrag), "9a6ae3c990f3c3982aa036c679fecd7a078aae2316b8c61f86f5149d7a75969e"),
    ];

    /// <summary>A program with its current mask functions removed and every frozen piece it carries restored. A
    /// program with none of them, such as the depth store, comes back unchanged. One with the mask functions but not
    /// every surface piece once, or with a piece twice, or with both entries, throws.</summary>
    internal static string Restore(string program)
    {
        int masks = Count(program, ShaderSources.TemporalVisibilityMaskGlsl);
        int[] surfaces = SurfaceHunks.Select(h => Count(program, h.FirstLine)).ToArray();
        int passOne = Count(program, PassOne.FirstLine), fused = Count(program, FusedEntry.FirstLine);
        if (masks == 0 && surfaces.All(n => n == 0) && passOne == 0 && fused == 0) return program;
        if (masks != 1 || surfaces.Any(n => n != 1) || passOne + fused > 1)
            throw new InvalidOperationException($"the D mask restore found {masks} mask blocks, surface pieces "
                + $"[{string.Join(", ", surfaces)}], {passOne} first passes and {fused} fused entries; one mask block, "
                + "each surface piece once and at most one entry expected");
        string d = program.Replace(ShaderSources.TemporalVisibilityMaskGlsl, "", StringComparison.Ordinal);
        foreach (Hunk hunk in SurfaceHunks) d = Replace(d, hunk);
        if (passOne == 1) d = Replace(d, PassOne);
        if (fused == 1) d = Replace(d, FusedEntry);
        return d;
    }

    static string Replace(string program, Hunk hunk)
    {
        int start = program.IndexOf(hunk.FirstLine, StringComparison.Ordinal);
        int last = program.IndexOf(hunk.LastLine, start, StringComparison.Ordinal);
        if (last < 0) throw new InvalidOperationException($"the D mask restore found no end to {hunk.Name}");
        return program[..start] + hunk.D + program[(last + hunk.LastLine.Length)..];
    }

    static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
