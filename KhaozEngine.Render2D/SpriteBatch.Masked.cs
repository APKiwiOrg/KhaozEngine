using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render2D;

public sealed partial class SpriteBatch
{
    readonly GpuOutputDescription _maskedOutput;
    readonly GpuVertexLayoutDescription _maskedVertexLayout;
    MaskedSpriteBatchResources? _maskedResources;

    /// <summary>
    /// Draw a flat tinted sprite through an independent alpha mask. <paramref name="destRect"/> is
    /// (x, y, width, height) in the current pass's authoring space. <paramref name="srcUV"/> and
    /// <paramref name="maskUV"/> each contain independent (u0, v0, u1, v1) endpoints.
    /// The source uses the current Begin sampler. Only the mask's alpha contributes coverage, sampled
    /// with <paramref name="maskSampler"/>. Both textures remain owned by the caller.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="tex"/> or <paramref name="mask"/> is null.</exception>
    public void DrawMasked(Texture2D tex, Vector4 destRect, Vector4 srcUV, Texture2D mask,
        Vector4 maskUV, Color color, SamplerMode maskSampler = SamplerMode.Linear)
    {
        ArgumentNullException.ThrowIfNull(tex);
        ArgumentNullException.ThrowIfNull(mask);
        var key = EnsureMaskedResources().KeyFor(tex.Handle, _sampler, mask.Handle,
            Resolve(maskSampler), _blend, _frame);
        float x = destRect.X, y = destRect.Y, w = destRect.Z, h = destRect.W;
        Vector4 tint = color;
        V tl = new V
        {
            Pos = new Vector2(x, y),
            Uv = new Vector2(srcUV.X, srcUV.Y),
            Color = tint,
            Local = new Vector2(maskUV.X, maskUV.Y),
            Shape = Vector4.Zero,
            Mode = Vector2.Zero
        };
        V tr = new V
        {
            Pos = new Vector2(x + w, y),
            Uv = new Vector2(srcUV.Z, srcUV.Y),
            Color = tint,
            Local = new Vector2(maskUV.Z, maskUV.Y),
            Shape = Vector4.Zero,
            Mode = Vector2.Zero
        };
        V br = new V
        {
            Pos = new Vector2(x + w, y + h),
            Uv = new Vector2(srcUV.Z, srcUV.W),
            Color = tint,
            Local = new Vector2(maskUV.Z, maskUV.W),
            Shape = Vector4.Zero,
            Mode = Vector2.Zero
        };
        V bl = new V
        {
            Pos = new Vector2(x, y + h),
            Uv = new Vector2(srcUV.X, srcUV.W),
            Color = tint,
            Local = new Vector2(maskUV.X, maskUV.W),
            Shape = Vector4.Zero,
            Mode = Vector2.Zero
        };
        _runs.Add(key, tl); _runs.Add(key, tr); _runs.Add(key, br);
        _runs.Add(key, tl); _runs.Add(key, br); _runs.Add(key, bl);
        _stats.Quads++;
    }

    private bool TryResolveMaskedKey(object key, out IGpuTexture source, out IGpuPipeline pipeline)
    {
        if (key is MaskedSpriteBatchResources.RunKey masked)
        {
            source = masked.Source;
            pipeline = _maskedResources!.PipelineFor(masked.Blend);
            return true;
        }
        source = null!;
        pipeline = null!;
        return false;
    }

    private bool TryBindMaskedAndDraw(object key, IGpuBuffer vb, uint vertexCount, uint vertexStart)
    {
        if (key is not MaskedSpriteBatchResources.RunKey masked) return false;
        _cl.SetGraphicsResourceSet(0, _maskedResources!.SetFor(masked, _frame));
        _cl.SetGraphicsResourceSet(1, _vpSet, _vpDynamicOffset);
        _cl.SetVertexBuffer(0, vb);
        _cl.Draw(vertexCount, 1, vertexStart, 0);
        return true;
    }

    // A failed construction releases its own resources and leaves the ordinary path usable for a retry.
    private MaskedSpriteBatchResources EnsureMaskedResources() =>
        _maskedResources ??= new MaskedSpriteBatchResources(_gd, _maskedOutput, _vpLayout, _maskedVertexLayout);
}
