using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// The RECEIVER half of point-light shadows: the frame-UBO tail every shader family reads its per-light slot out
/// of, and the atlas binding that sits directly after <c>ShadowSamp</c> in every one of their resource sets.
/// The atlas itself, the pass that fills it and the slot cache that decides which light owns which row are all
/// outside this renderer.
/// <para>
/// A SLOT BELOW ZERO IS THE WHOLE SAFETY PROPERTY. The receiver skips the sample, the multiply and the texture
/// read on <c>PointShadowParams[i].x &lt; 0</c>, so a frame in which no light carries a map renders exactly as it
/// did before this feature existed. That is why <see cref="_pointShadowParams"/> starts at -1 rather than at the
/// zero a fresh array would hold: zero is a VALID slot, and a renderer nobody has called
/// <see cref="SetPointShadowUniforms"/> on would otherwise have all sixteen lights sampling row 0.
/// </para>
/// </summary>
internal sealed partial class ModelRenderer
{
    /// <summary>Offset of the point-shadow tail in the frame block. It follows the render origin, which keeps
    /// every offset that existed before it exactly where it was.</summary>
    internal const uint PointShadowTailOffset = RenderOriginOffset + RenderOriginBytes;   // 1008

    /// <summary>Size of the point-shadow tail: one vec4 per point-light slot
    /// (<c>PointShadowParams[MaxPointLights]</c>) plus the single <c>PointShadowAtlas</c> vec4 = 272 bytes.</summary>
    internal const uint PointShadowTailBytes = MaxPointLights * 16 + 16;

    // (slot or -1, bias, slopeBias, 0) per point light, index-aligned with the two light arrays above, so the
    // receiver reads PointShadowParams[i] for the light it is already accumulating. -1 everywhere until a host
    // hands slots in.
    readonly Vector4[] _pointShadowParams = NoPointShadowSlots();
    // (1 / (6 * faceRes), 1 / (rows * faceRes), rows, 6): the atlas texel steps the four-tap compare offsets by,
    // and its shape, so the receiver can place a cell without a second uniform.
    Vector4 _pointShadowAtlas;

    // The 1x1 R32Float white the PointShadowMap slot binds until a real atlas exists. Sampled-only on purpose:
    // the receiver's own gate means nothing ever reads it, and a render-target usage would make it look like a
    // shadow atlas to anything walking a set's resources. White is "nothing occluded" if one ever did.
    IGpuTexture _pointShadowDefault = null!;
    IGpuTexture _pointShadowTexture = null!;

    // The texture whose rebind transaction threw, LATCHED. A failed rebuild leaves _pointShadowTexture where it
    // was, so without this a caller asking for the same atlas every frame re-enters the whole transaction, and its
    // _gd.WaitForIdle stall, once a frame forever. Cleared by a rebind that succeeds and by any request naming a
    // different texture, so dropping the failed atlas and handing over a fresh one (or null, which is the 1x1
    // default) gets a real attempt. Asking again for the exact texture that failed does not.
    IGpuTexture? _pointShadowBindFailure;

    static Vector4[] NoPointShadowSlots()
    {
        var slots = new Vector4[MaxPointLights];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Vector4(-1f, 0f, 0f, 0f);
        return slots;
    }

    void CreatePointShadowDefault(IGpuResourceFactory factory)
    {
        _pointShadowDefault = factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
        _gd.UpdateTexture(_pointShadowDefault, BitConverter.GetBytes(1f), 0, 0, 1, 1);
        _pointShadowTexture = _pointShadowDefault;
    }

    /// <summary>Set this frame's point-shadow tail. <paramref name="slots"/> is one atlas row index per point
    /// light in the order they were queued, with -1 for a light that carries no map (past the budget, not
    /// requested, or requested but never yet rendered). Lights past <paramref name="slots"/> read -1 too, so a
    /// short span is the ordinary case rather than an error. <paramref name="bias"/> and
    /// <paramref name="slopeBias"/> are in radius-normalized units, matching what the pass stores.
    /// <paramref name="faceResolution"/> and <paramref name="rows"/> are the live atlas layout.</summary>
    public void SetPointShadowUniforms(ReadOnlySpan<int> slots, float bias, float slopeBias,
        int faceResolution, int rows)
    {
        for (int i = 0; i < MaxPointLights; i++)
        {
            float slot = i < slots.Length ? slots[i] : -1f;
            _pointShadowParams[i] = new Vector4(slot, bias, slopeBias, 0f);
        }
        _pointShadowAtlas = new Vector4(
            1f / (PointShadowFaceCount * faceResolution), 1f / (rows * faceResolution), rows, PointShadowFaceCount);
        _frameImageDirty = true;
    }

    /// <summary>Clear the tail to "no light carries a map", which is the byte-identical path. Call it on every
    /// frame that runs no point shadow pass, exactly as <see cref="ClearShadowUniforms"/> is called for the
    /// cascade atlas.</summary>
    public void ClearPointShadowUniforms()
    {
        for (int i = 0; i < MaxPointLights; i++) _pointShadowParams[i] = new Vector4(-1f, 0f, 0f, 0f);
        _pointShadowAtlas = Vector4.Zero;
        _frameImageDirty = true;
    }

    /// <summary>
    /// Bind <paramref name="atlasOrNull"/> (or the 1x1 default when null) at the <c>PointShadowMap</c> slot of
    /// every receiver family, rebuilding each shadow-sampling resource set against it.
    /// <see cref="PointShadowBindResult.Unchanged"/> is the ordinary per-frame answer, because the binding
    /// already stands. <see cref="PointShadowBindResult.Failed"/> means a set could not be allocated, nothing
    /// moved and the previous atlas stays bound, and it is a SEPARATE answer from Unchanged on purpose: the two
    /// used to be one <c>false</c>, which made a permanent failure indistinguishable from the quiet case.
    /// </summary>
    /// <remarks>
    /// It takes the live material sets and their commit callback for the same reason
    /// <see cref="ReplaceShadowLayout"/> does: a resource set is immutable, so changing one bound texture means
    /// building a replacement for every set that carries it and handing the new ones back to whoever holds the
    /// old. The atlas is allocated lazily, so this fires at most once per allocation rather than per frame.
    /// <para>
    /// A failure is LATCHED against the texture that caused it, so a caller that asks for the same atlas on every
    /// frame pays the transaction (and the <c>WaitForIdle</c> inside it) once rather than once a frame forever.
    /// Any other texture, including the 1x1 default a null asks for, clears the latch and is attempted for real.
    /// </para>
    /// </remarks>
    internal PointShadowBindResult BindPointShadowAtlas(IGpuTexture? atlasOrNull,
        IReadOnlyList<IGpuResourceSet> liveMaterialSets,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        IGpuTexture wanted = atlasOrNull ?? _pointShadowDefault;
        if (ReferenceEquals(wanted, _pointShadowTexture)) return PointShadowBindResult.Unchanged;
        if (ReferenceEquals(wanted, _pointShadowBindFailure)) return PointShadowBindResult.Failed;

        var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
        var replacementBindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);
        try
        {
            _gd.WaitForIdle();
            AddReplacement(_defaultSet, _shadowMap.ShadowTexture, wanted, replacements, replacementBindings);
            AddReplacement(_skinnedDefaultFragSet, _shadowMap.ShadowTexture, wanted, replacements, replacementBindings);
            foreach (IGpuResourceSet set in liveMaterialSets)
                AddReplacement(set, _shadowMap.ShadowTexture, wanted, replacements, replacementBindings);
        }
        catch
        {
            foreach (IGpuResourceSet set in replacements.Values) set.Dispose();
            _pointShadowBindFailure = wanted;
            return PointShadowBindResult.Failed;
        }

        _pointShadowTexture = wanted;
        _pointShadowBindFailure = null;
        CommitShadowSamplingSets(replacements, replacementBindings, commitMaterialSets);
        return PointShadowBindResult.Rebound;
    }

    // Six faces to a row, the cube-map convention the pass and the receiver both bake in. Named here because the
    // receiver reads it out of PointShadowAtlas.w rather than compiling it in.
    const int PointShadowFaceCount = 6;
}

/// <summary>What <see cref="ModelRenderer.BindPointShadowAtlas"/> did, in the same three-way shape
/// <c>ShadowLayoutReplacementResult</c> carries for the cascade atlas. The caller needs Failed apart from
/// Unchanged because only one of them is worth reporting, and only one of them is a reason to stop asking.</summary>
internal enum PointShadowBindResult
{
    /// <summary>The wanted atlas was already bound. Nothing was allocated and nothing stalled.</summary>
    Unchanged,

    /// <summary>Every receiver set was rebuilt against the wanted atlas and the old ones were freed.</summary>
    Rebound,

    /// <summary>A set could not be allocated, so nothing moved and the previous atlas stays bound. Latched
    /// against the texture that failed, so asking for the same one again answers this without re-entering the
    /// transaction.</summary>
    Failed,
}
