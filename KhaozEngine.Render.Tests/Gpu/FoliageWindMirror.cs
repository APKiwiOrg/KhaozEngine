using System;
using System.Numerics;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// FoliageVert's wind, wind fade and interactor bend in C# (<c>ShaderSources.Foliage.cs</c>), statement for statement,
/// for the one case where the bend moves every drawn vertex alike: the top of an upright, unscaled, unrotated blade one
/// metre tall, fully faded in, with the render origin at zero.
/// </summary>
internal static class FoliageWindMirror
{
    const float BladeHeight = 1f;

    /// <summary>How far <paramref name="u"/>'s wind and interactors move the top of the blade rooted at
    /// <paramref name="root"/>. The wind fade reads the root's clip w under <paramref name="viewProjection"/>, the
    /// camera of the frame <paramref name="u"/> was uploaded for, and needs it only when the fade is on.</summary>
    public static Vector3 TopOffset(in FoliageUniforms u, Vector3 root, Matrix4x4? viewProjection = null)
    {
        Assert.True(u.WindFade.X <= 0f || viewProjection.HasValue, "the wind fade reads the root's clip w");
        return TopOffset(u, root, viewProjection is { } m ? Vector4.Transform(new Vector4(root, 1f), m).W : 0f);
    }

    /// <summary>As the matrix overload, with the root's clip w given directly: its depth in front of the eye under a
    /// perspective camera, 1 under an orthographic one. The wind fade reads it only when the fade is on.</summary>
    public static Vector3 TopOffset(in FoliageUniforms u, Vector3 root, float rootClipW)
    {
        // Fully faded in: no thinning band, and the root inside the distance fade's start, so heightFade is 1.
        Assert.True(u.Density.X <= u.Density.Y, "the mirror does not model the thinning band");
        float fadeStart = MathF.Max(0f, u.FocusRadius.W - u.WindTime.W);
        Assert.True(Vector2.Distance(new Vector2(root.X, root.Z), new Vector2(u.FocusRadius.X, u.FocusRadius.Z)) <= fadeStart,
            "the mirror models a blade fully faded in");
        float phase = (root.X * u.WindTime.X + root.Z * u.WindTime.Y) * u.FadeWind.W - u.WindTime.Z * u.FadeWind.Z;
        var direction = new Vector2(u.WindTime.X, u.WindTime.Y);
        Vector2 bend = direction * (MathF.Sin(phase) * .7f + MathF.Sin(phase * .43f + 1.7f) * .3f) * u.FadeWind.Y * BladeHeight;
        if (u.WindFade.X > 0f)
        {
            float fadeHeight = MathF.Max(rootClipW, 0f) * u.WindFade.Y * u.WindFade.X;
            bend *= fadeHeight > 0f ? SmoothStep(1f, 2f, BladeHeight / fadeHeight) : 1f;
        }
        ReadOnlySpan<Vector4> interactors = [u.Interactor0, u.Interactor1, u.Interactor2, u.Interactor3];
        ReadOnlySpan<float> strengths = [u.Strengths.X, u.Strengths.Y, u.Strengths.Z, u.Strengths.W];
        for (int i = 0; i < 4; i++)
        {
            if (interactors[i].W <= 0f || strengths[i] <= 0f) continue;
            Vector3 delta = root - new Vector3(interactors[i].X, interactors[i].Y, interactors[i].Z);
            float falloff = 1f - SmoothStep(0f, interactors[i].W, delta.Length());
            var across = new Vector2(delta.X, delta.Z);
            bend += across / MathF.Max(across.Length(), .05f) * falloff * strengths[i] * BladeHeight;
        }
        float length = bend.Length();
        if (length > BladeHeight * .65f && length > 0f) bend *= BladeHeight * .65f / length;
        float drop = BladeHeight - MathF.Sqrt(MathF.Max(0f, BladeHeight * BladeHeight - Vector2.Dot(bend, bend)));
        return new Vector3(bend.X, -drop, bend.Y);
    }

    static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
