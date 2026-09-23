using System;

namespace KhaozEngine.Render3D
{
    /// <summary>Which rigid depth pipeline the key light's cascade pass records one caster draw through. One member
    /// per rigid pipeline <c>ShadowMapRenderer</c> builds, so a draw names exactly the pipeline it binds.</summary>
    enum ShadowDepthVariant : byte
    {
        /// <summary>The depth-only rigid pipeline: no texture sample and no discard. Every opaque caster, and every
        /// CPU-skinned caster.</summary>
        Opaque = 0,
        /// <summary>The rigid dissolve pipeline (issue #287).</summary>
        Dissolve = 1,
        /// <summary>The rigid inverted dissolve pipeline, the merged half of an HLOD crossfade (issue #391).</summary>
        DissolveInverted = 2,
        /// <summary>The alpha-cutout pipeline (issue #15): a MASK caster, dissolving or not.</summary>
        Cutout = 3,
        /// <summary>The alpha-cutout pipeline with the inverted dither: a MASK caster that is the merged half of a
        /// crossfade.</summary>
        CutoutInverted = 4,
    }

    /// <summary>
    /// The one definition of which depth pipeline each caster takes in the key light's cascade pass. Pure, so the
    /// pass and the headless tests read the same rules.
    /// <para>
    /// The rigid rule is per SPAN and has two inputs: the span's <see cref="ShadowCastKind"/> (per instance,
    /// classified where instances are grouped) and whether its MESH cuts out (per mesh, fixed at load). The cutout
    /// is a mesh property rather than a new cast kind on purpose: the kind list also feeds the point-light depth
    /// pass, which has no cutout pipeline, and a mesh's cutoff and albedo never change after load, so the caster
    /// signature the dirty check compares (mesh handle plus kind) already covers it.
    /// </para>
    /// <para>
    /// An opaque caster costs what it did before: <see cref="ShadowDepthVariant.Opaque"/> is chosen for every
    /// plain span of a mesh that does not cut out, and that pipeline samples no texture and discards nothing.
    /// </para>
    /// </summary>
    static class ShadowDepthSelection
    {
        /// <summary>Whether a mesh's shadow alpha-tests: it carries a MASK cutoff AND a cutout set, which exists
        /// only when the mesh was loaded with an albedo texture. A MASK mesh with no albedo samples the white
        /// default in the colour pass, so it never discards there either and keeps the cheap depth path.</summary>
        internal static bool MeshCutsOutShadow(float alphaCutoff, bool hasCutoutMaterial)
            => alphaCutoff > 0f && hasCutoutMaterial;

        /// <summary>The pipeline one rigid caster span draws through. <see cref="ShadowCastKind.None"/> never
        /// reaches the pass (<c>Scene3D.AppendCasterSpans</c> emits no span for it), so it is refused.</summary>
        internal static ShadowDepthVariant ForRigidSpan(ShadowCastKind kind, bool meshCutsOut) => kind switch
        {
            ShadowCastKind.Opaque => meshCutsOut ? ShadowDepthVariant.Cutout : ShadowDepthVariant.Opaque,
            ShadowCastKind.Dissolving => meshCutsOut ? ShadowDepthVariant.Cutout : ShadowDepthVariant.Dissolve,
            ShadowCastKind.DissolvingInverted => meshCutsOut
                ? ShadowDepthVariant.CutoutInverted : ShadowDepthVariant.DissolveInverted,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "an opted-out caster has no depth span"),
        };
    }
}
