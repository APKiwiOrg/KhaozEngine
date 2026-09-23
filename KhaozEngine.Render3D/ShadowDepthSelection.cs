using System;

namespace KhaozEngine.Render3D
{
    /// <summary>Which depth pipeline the key light's cascade pass records one caster draw through. One member per
    /// pipeline <c>ShadowMapRenderer</c> builds, so a draw names exactly the pipeline it binds.</summary>
    enum ShadowDepthVariant : byte
    {
        /// <summary>The depth-only rigid pipeline: no texture sample and no discard. Every opaque caster, and every
        /// CPU-skinned caster that is not dissolving.</summary>
        Opaque = 0,
        /// <summary>The rigid dissolve pipeline (issue #287). Also a dissolving CPU-skinned caster (issue #387),
        /// whose per-draw instance carries its threshold.</summary>
        Dissolve = 1,
        /// <summary>The rigid inverted dissolve pipeline, the merged half of an HLOD crossfade (issue #391).</summary>
        DissolveInverted = 2,
        /// <summary>The alpha-cutout pipeline (issue #15): a MASK caster, dissolving or not.</summary>
        Cutout = 3,
        /// <summary>The alpha-cutout pipeline with the inverted dither: a MASK caster that is the merged half of a
        /// crossfade.</summary>
        CutoutInverted = 4,
        /// <summary>The GPU-skinned depth pipeline.</summary>
        Skinned = 5,
        /// <summary>The dissolve-aware GPU-skinned depth pipeline (issue #387).</summary>
        SkinnedDissolve = 6,
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

        /// <summary>The pipeline one CPU-skinned caster draws through: its deformed vertices and per-draw instance
        /// use the rigid layouts, so it takes a rigid pipeline. Skinned meshes carry no cutoff, so never a cutout
        /// one. Refuses <see cref="ShadowCastKind.None"/>, which the pass skips.</summary>
        internal static ShadowDepthVariant ForCpuSkinned(ShadowCastKind kind) => kind switch
        {
            ShadowCastKind.Opaque => ShadowDepthVariant.Opaque,
            ShadowCastKind.Dissolving => ShadowDepthVariant.Dissolve,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a skinned caster kind"),
        };

        /// <summary>The pipeline one GPU-skinned caster draws through. Refuses <see cref="ShadowCastKind.None"/>,
        /// which the pass skips.</summary>
        internal static ShadowDepthVariant ForGpuSkinned(ShadowCastKind kind) => kind switch
        {
            ShadowCastKind.Opaque => ShadowDepthVariant.Skinned,
            ShadowCastKind.Dissolving => ShadowDepthVariant.SkinnedDissolve,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a skinned caster kind"),
        };

        /// <summary>How one queued skinned draw takes part in the depth pass (issue #387): opted out with
        /// <c>castsShadows: false</c>, dissolving, or plain. Opted out wins over dissolving, as on the rigid side.
        /// Never <see cref="ShadowCastKind.DissolvingInverted"/>: a character dissolve has no crossfade partner.</summary>
        internal static ShadowCastKind ClassifySkinnedCaster(in SkinnedSceneInstances.Instance instance)
            => !instance.CastsShadows ? ShadowCastKind.None
             : instance.Dissolving ? ShadowCastKind.Dissolving
             : ShadowCastKind.Opaque;
    }
}
