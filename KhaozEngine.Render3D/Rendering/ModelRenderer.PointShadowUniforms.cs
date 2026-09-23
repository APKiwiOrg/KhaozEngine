using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>
/// The RECEIVER half of point-light shadows: the full slot cache packed into each structured light record, the
/// legacy frame-UBO mirror for the first sixteen entries, and the atlas binding after <c>ShadowSamp</c>.
/// The atlas itself, the pass that fills it and the slot cache that decides which light owns which row are all
/// outside this renderer.
/// <para>
/// A SLOT BELOW ZERO IS THE WHOLE SAFETY PROPERTY. The receiver skips the sample, the multiply and the texture
/// read on <c>pointLight.ShadowParams.x &lt; 0</c>, so a frame in which no light carries a map renders exactly as it
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
    /// (<c>PointShadowParams[MaxPointLights]</c>) plus the <c>PointShadowAtlas</c> and <c>PointShadowFilter</c>
    /// vec4s = 288 bytes.</summary>
    internal const uint PointShadowTailBytes = MaxPointLights * 16 + 32;

    // The first sixteen (base row, bias, slopeBias, transient row) entries mirror the structured records.
    // Receiver shaders read the complete structured record list.
    readonly Vector4[] _pointShadowParams = NoPointShadowSlots();
    int[] _pointShadowSlots = new int[MaxPointLights];
    int[] _pointShadowTransientSlots = new int[MaxPointLights];
    int _pointShadowSlotCount;
    float _pointShadowBias;
    float _pointShadowSlopeBias;
    // (1 / (6 * faceRes), 1 / (rows * faceRes), rows, 6): the atlas texel steps the four-tap compare offsets by,
    // and its shape, so the receiver can place a cell without a second uniform.
    Vector4 _pointShadowAtlas;
    // (filter mode, lightSizeMetres, maxPenumbraTexels, faceResolution): which filter the receiver runs and the
    // two knobs its penumbra arithmetic reads. The FACE resolution rides here rather than being recovered from
    // the atlas texel steps above, which are atlas-wide: the soft filter converts a penumbra in metres into an
    // angle and then into texels of ONE face, and the host already knows that number.
    Vector4 _pointShadowFilter;

    // The 1x1 R32Float white the PointShadowMap slot binds until a real atlas exists. Sampled-only on purpose:
    // the receiver's own gate means nothing ever reads it, and a render-target usage would make it look like a
    // shadow atlas to anything walking a set's resources. White is "nothing occluded" if one ever did.
    IGpuTexture _pointShadowDefault = null!;
    IGpuTexture _pointShadowTexture = null!;

    // The texture whose rebind transaction threw, LATCHED. A failed rebuild leaves _pointShadowTexture where it
    // was, so without this a caller asking for the same atlas every frame re-enters the whole transaction, and its
    // _gd.WaitForIdle stall, once a frame forever. Cleared by a rebind that succeeds, by any request naming a
    // different texture, and by ForgetPointShadowBindFailure when the caller frees the texture it names, so
    // dropping the failed atlas and handing over a fresh one (or null, which is the 1x1 default) gets a real
    // attempt. Asking again for the exact texture that failed does not. It is never the reason a DISPOSED handle
    // stays reachable: a texture nobody can ask for again is a latch with nothing left to refuse.
    IGpuTexture? _pointShadowBindFailure;

    /// <summary>The texture every receiver set currently names at its <c>PointShadowMap</c> slot, which is the
    /// live atlas or the 1x1 default. A diagnostic, and the one a test asserts on after a refused rebind: the
    /// scene's own handle and this one disagreeing is the whole defect, so asserting the scene's alone would
    /// have missed it.</summary>
    internal IGpuTexture BoundPointShadowTexture => _pointShadowTexture;

    static Vector4[] NoPointShadowSlots()
    {
        var slots = new Vector4[MaxPointLights];
        for (int i = 0; i < slots.Length; i++) slots[i] = new Vector4(-1f, 0f, 0f, -1f);
        return slots;
    }

    internal void EnsurePointShadowSlotCapacity(int required)
    {
        if (required < 0)
            throw new ArgumentOutOfRangeException(nameof(required), required,
                "Point-shadow slot capacity cannot be negative.");
        if (_pointShadowSlots.Length >= required) return;

        int capacity = _pointShadowSlots.Length;
        while (capacity < required)
            capacity = capacity > int.MaxValue / 2 ? required : capacity * 2;
        Array.Resize(ref _pointShadowSlots, capacity);
        Array.Resize(ref _pointShadowTransientSlots, capacity);
    }

    void CreatePointShadowDefault(IGpuResourceFactory factory)
    {
        _pointShadowDefault = factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R32Float, GpuTextureUsage.Sampled));
        _gd.UpdateTexture(_pointShadowDefault, BitConverter.GetBytes(1f), 0, 0, 1, 1);
        _pointShadowTexture = _pointShadowDefault;
    }

    /// <summary>Set this frame's point-shadow tail. <paramref name="baseRows"/> and
    /// <paramref name="transientRows"/> carry atlas row indices in queued light order, with -1 when a light has
    /// no row. Lights past either span read -1 too, so a short span is valid. <paramref name="bias"/> and
    /// <paramref name="slopeBias"/> are in radius-normalized units, matching what the pass stores, and they are
    /// <see cref="PointShadowSettings.ResolvedBias"/> and <see cref="PointShadowSettings.ResolvedSlopeBias"/>
    /// rather than the raw fields: a negative bias inverts the receiver's compare into a light leak, so the
    /// clamped values are the ones that may reach this uniform. <paramref name="faceResolution"/> and
    /// <paramref name="baseRowsCount"/> are the live base atlas layout. <paramref name="transientRowsCount"/>
    /// is the transient layout count reserved for the receiver extension. <paramref name="filter"/> selects which receiver path
    /// runs, and <paramref name="lightSizeMetres"/> and <paramref name="maxPenumbraTexels"/> are
    /// <see cref="PointShadowSettings.ResolvedLightSizeMetres"/> and
    /// <see cref="PointShadowSettings.ResolvedMaxPenumbraTexels"/> rather than the raw fields, on the same rule
    /// the two biases follow: the soft filter divides by neither of them, but a NaN in either poisons every tap
    /// offset it computes.</summary>
    public void SetPointShadowUniforms(ReadOnlySpan<int> baseRows, ReadOnlySpan<int> transientRows,
        float bias, float slopeBias, int faceResolution, int baseRowsCount, int transientRowsCount,
        PointShadowFilter filter, float lightSizeMetres, float maxPenumbraTexels)
    {
        if (_pointShadowSlots.Length < baseRows.Length || _pointShadowTransientSlots.Length < transientRows.Length)
            throw new InvalidOperationException(
                $"The frame published {Math.Max(baseRows.Length, transientRows.Length)} point-shadow slots to receiver storage with capacity "
                + $"{_pointShadowSlots.Length}. Grow it during frame preparation before command recording.");
        if (transientRowsCount < 0)
            throw new ArgumentOutOfRangeException(nameof(transientRowsCount));
        Array.Fill(_pointShadowSlots, -1);
        Array.Fill(_pointShadowTransientSlots, -1);
        baseRows.CopyTo(_pointShadowSlots);
        transientRows.CopyTo(_pointShadowTransientSlots);
        _pointShadowSlotCount = Math.Max(baseRows.Length, transientRows.Length);
        _pointShadowBias = bias;
        _pointShadowSlopeBias = slopeBias;
        for (int i = 0; i < MaxPointLights; i++)
        {
            float baseRow = i < baseRows.Length ? baseRows[i] : -1f;
            float transientRow = i < transientRows.Length ? transientRows[i] : -1f;
            _pointShadowParams[i] = new Vector4(baseRow, bias, slopeBias, transientRow);
        }
        _pointShadowAtlas = new Vector4(
            1f / (PointShadowFaceCount * faceResolution), 1f / (baseRowsCount * faceResolution),
            baseRowsCount, PointShadowFaceCount);
        _pointShadowFilter = new Vector4((float)filter, lightSizeMetres, maxPenumbraTexels, faceResolution);
        _frameImageDirty = true;
    }

    /// <summary>Clear the tail to "no light carries a map", which is the byte-identical path. Call it on every
    /// frame that runs no point shadow pass, exactly as <see cref="ClearShadowUniforms"/> is called for the
    /// cascade atlas.</summary>
    public void ClearPointShadowUniforms()
    {
        _pointShadowSlotCount = 0;
        _pointShadowBias = 0f;
        _pointShadowSlopeBias = 0f;
        for (int i = 0; i < MaxPointLights; i++) _pointShadowParams[i] = new Vector4(-1f, 0f, 0f, -1f);
        _pointShadowAtlas = Vector4.Zero;
        _pointShadowFilter = Vector4.Zero;
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
    /// Any other texture, including the 1x1 default a null asks for, clears the latch and is attempted for real,
    /// and a caller freeing the texture it names says so through <see cref="ForgetPointShadowBindFailure"/>.
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

    /// <summary>Drop the bind-failure latch if it names <paramref name="discarded"/>, which the caller is about to
    /// free. The latch exists to refuse a REPEAT of the same request cheaply, and a freed texture cannot be
    /// requested again, so keeping it would buy nothing and would hold a disposed handle reachable for the rest of
    /// the session. Any other texture leaves the latch exactly where it is.</summary>
    internal void ForgetPointShadowBindFailure(IGpuTexture discarded)
    {
        if (ReferenceEquals(_pointShadowBindFailure, discarded)) _pointShadowBindFailure = null;
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
