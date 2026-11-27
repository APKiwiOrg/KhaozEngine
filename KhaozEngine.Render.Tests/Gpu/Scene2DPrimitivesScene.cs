using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The <c>scene2d_primitives</c> golden scene. Shared by the managed <c>Golden2D_Primitives</c> test and the
/// native core golden check, which link this one file so both render exactly the same draws.
/// </summary>
internal static class Scene2DPrimitivesScene
{
    public const int Width = 480;
    public const int Height = 320;
    public static readonly Color Clear = new(0.06f, 0.07f, 0.10f, 1f);

    public static void Draw(Render2DContext ctx)
    {
        // NOT 'using': prim owns a 1x1 white texture that the batch's recorded draws reference. Capture
        // submits the command list AFTER this callback returns, so disposing prim here use-after-frees the
        // texture (Vulkan rejects that at submit, Metal/D3D11 tolerate it). The pixel is a
        // ctx.CreateTexture underneath, so Capture frees it after the drain rather than leaking it (#618).
        var prim = new PrimitiveRenderer(ctx);
        ctx.Batch.Begin();

        // Filled rect + outline rect on top of it.
        prim.DrawFilledRect(ctx.Batch, new KhaozEngine.Primitives.Rect(30, 30, 130, 80), new Color(0.20f, 0.45f, 0.85f, 1f));
        prim.DrawRect(ctx.Batch, new KhaozEngine.Primitives.Rect(30, 30, 130, 80), new Color(0.95f, 0.95f, 0.95f, 1f), 3f);

        // A couple of diagonal lines (rotated quads).
        prim.DrawLine(ctx.Batch, new Vector2(40, 130), new Vector2(180, 210), new Color(0.95f, 0.35f, 0.2f, 1f), 4f);
        prim.DrawLine(ctx.Batch, new Vector2(40, 210), new Vector2(180, 130), new Color(0.2f, 0.9f, 0.4f, 1f), 4f);

        // Circle outline + ring, distinct radii.
        prim.DrawCircle(ctx.Batch, new Vector2(280, 80), 45f, new Color(0.9f, 0.8f, 0.2f, 1f), segments: 40, thickness: 2f);
        prim.DrawRing(ctx.Batch, new Vector2(400, 80), 50f, 6f, new Color(0.85f, 0.3f, 0.85f, 1f));

        // Filled circle.
        prim.DrawFilledCircle(ctx.Batch, new Vector2(280, 200), 42f, new Color(0.3f, 0.7f, 0.9f, 1f));

        // Vertical gradient panel.
        prim.DrawVerticalGradient(ctx.Batch, new KhaozEngine.Primitives.Rect(360, 150, 90, 110),
            new Color(0.9f, 0.9f, 0.95f, 1f), new Color(0.15f, 0.1f, 0.3f, 1f), bands: 16);

        // Progress bar near the bottom.
        prim.DrawProgressBar(ctx.Batch, new KhaozEngine.Primitives.Rect(40, 280, 400, 24), 0.62f,
            new Color(0.2f, 0.8f, 0.35f, 1f), new Color(0.15f, 0.15f, 0.18f, 1f),
            new Color(0.8f, 0.8f, 0.85f, 1f), 2f);

        ctx.Batch.End();
    }
}
