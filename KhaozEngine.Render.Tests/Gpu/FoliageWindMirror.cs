using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Gpu;

/// <summary>An independent CPU evaluation of the shared foliage fade and displacement, including history and profiles.</summary>
internal static class FoliageWindMirror
{
    const float BladeHeight = 1f;

    /// <summary>How far wind and interactors move the top of an upright one-metre blade, fully faded in.
    /// The wind fade reads the root's clip w under the camera of the frame the uniforms were uploaded for.</summary>
    public static Vector3 TopOffset(in FoliageUniforms u, Vector3 root, Matrix4x4? viewProjection = null,
        FoliageMotionProfile motion = default)
    {
        Assert.True(u.WindFade.X <= 0f || viewProjection.HasValue, "the wind fade reads the root's clip w");
        return TopOffset(u, root, viewProjection is { } m ? Vector4.Transform(new Vector4(root, 1f), m).W : 0f, motion);
    }

    /// <summary>As the matrix overload, with the root's clip w given directly. The wind fade reads it only when on.</summary>
    public static Vector3 TopOffset(in FoliageUniforms u, Vector3 root, float rootClipW,
        FoliageMotionProfile motion = default)
    {
        Assert.True(u.Density.X <= u.Density.Y, "the mirror does not model the thinning band");
        float fadeStart = MathF.Max(0f, u.FocusRadius.W - u.WindTime.W);
        Assert.True(Vector2.Distance(new Vector2(root.X, root.Z), new Vector2(u.FocusRadius.X, u.FocusRadius.Z)) <= fadeStart,
            "the mirror models a blade fully faded in");
        float phase = (root.X * u.WindTime.X + root.Z * u.WindTime.Y) * u.FadeWind.W - u.WindTime.Z * u.FadeWind.Z;
        Vector2 bend = Bend(u, root, phase, BladeHeight, rootClipW, u.WindFade.Y, Vector3.Zero, false, motion.ToVector4());
        return Displacement(bend, BladeHeight);
    }

    /// <summary>Evaluates an authored vertex with the requested frame's focus, clock, interactors and pixel scale.
    /// The supplied fade matrix belongs to that frame and render origin rebases authored positions only.</summary>
    public static Vector3 VertexWorld(in FoliageUniforms u, in ModelRenderer.FoliageInstanceData instance,
        Vector3 localPosition, in Matrix4x4 fadeViewProjection, Vector3 renderOrigin, bool previous, out bool rejected)
    {
        Matrix4x4 model = instance.Model;
        var translation = new Vector3(model.M41, model.M42, model.M43);
        var axisY = new Vector3(model.M21, model.M22, model.M23);
        Vector4 focus = previous ? u.PrevFocus : u.FocusRadius;
        float rank = instance.Parameters.X;
        float distanceToFocus = Vector2.Distance(new Vector2(translation.X, translation.Z), new Vector2(focus.X, focus.Z));
        float cutoff = u.FocusRadius.W;
        float fadeStart = MathF.Max(0f, cutoff - u.WindTime.W);
        float inner = u.Density.Z - u.Density.W;
        if (u.Density.X > u.Density.Y && rank >= u.Density.Y)
        {
            cutoff = inner + (u.Density.X - rank) / (u.Density.X - u.Density.Y) * u.Density.W;
            fadeStart = MathF.Max(inner, cutoff - MathF.Min(u.FadeWind.X, u.Density.W));
        }
        float fade = cutoff > fadeStart ? Math.Clamp((distanceToFocus - fadeStart) / (cutoff - fadeStart), 0f, 1f) : 0f;
        float heightFade = 1f - fade * fade * (3f - 2f * fade);
        rejected = rank >= u.Density.X || distanceToFocus > cutoff || heightFade <= .0001f;
        translation -= renderOrigin;
        translation += axisY * instance.Parameters.Y * (1f - heightFade);
        model.M41 = translation.X;
        model.M42 = translation.Y;
        model.M43 = translation.Z;
        float scale = MathF.Max(heightFade, .00001f);
        model.M21 *= scale;
        model.M22 *= scale;
        model.M23 *= scale;
        Vector3 world = Vector3.Transform(localPosition, model);
        float invHeight = instance.Parameters.Z;
        float bladeHeight = invHeight > 0f ? axisY.Length() / invHeight * heightFade : 0f;
        float rootWeight = Math.Clamp((localPosition.Y - instance.Parameters.Y) * invHeight, 0f, 1f);
        rootWeight *= rootWeight;
        float windTime = previous ? u.PrevFocus.W : u.WindTime.Z;
        float phase = (instance.Model.M41 * u.WindTime.X + instance.Model.M43 * u.WindTime.Y) * u.FadeWind.W
            - windTime * u.FadeWind.Z;
        Vector3 root = Vector3.Transform(new Vector3(0f, instance.Parameters.Y, 0f), model);
        float rootClipW = Vector4.Transform(new Vector4(root, 1f), fadeViewProjection).W;
        float metresPerPixel = previous ? u.WindFade.Z : u.WindFade.Y;
        Vector2 bend = Bend(u, root, phase, bladeHeight, rootClipW, metresPerPixel, renderOrigin, previous, instance.Motion);
        return world + Displacement(bend, bladeHeight) * rootWeight;
    }

    static Vector2 Bend(in FoliageUniforms u, Vector3 root, float phase, float bladeHeight, float rootClipW,
        float metresPerPixel, Vector3 renderOrigin, bool previous, Vector4 motion)
    {
        var direction = new Vector2(u.WindTime.X, u.WindTime.Y);
        Vector2 bend = direction * (MathF.Sin(phase) * .7f + MathF.Sin(phase * .43f + 1.7f) * .3f)
            * u.FadeWind.Y * bladeHeight * motion.Z;
        if (u.WindFade.X > 0f)
        {
            float fadeHeight = MathF.Max(rootClipW, 0f) * metresPerPixel * u.WindFade.X;
            bend *= fadeHeight > 0f ? SmoothStep(1f, 2f, bladeHeight / fadeHeight) : 1f;
        }
        ReadOnlySpan<Vector4> interactors = previous
            ? [u.PrevInteractor0, u.PrevInteractor1, u.PrevInteractor2, u.PrevInteractor3]
            : [u.Interactor0, u.Interactor1, u.Interactor2, u.Interactor3];
        Vector4 packedStrengths = previous ? u.PrevStrengths : u.Strengths;
        ReadOnlySpan<float> strengths = [packedStrengths.X, packedStrengths.Y, packedStrengths.Z, packedStrengths.W];
        for (int i = 0; i < 4; i++)
        {
            if (interactors[i].W <= 0f || strengths[i] <= 0f) continue;
            Vector3 delta = root - (new Vector3(interactors[i].X, interactors[i].Y, interactors[i].Z) - renderOrigin);
            float falloff = 1f - SmoothStep(0f, interactors[i].W, delta.Length());
            var across = new Vector2(delta.X, delta.Z);
            bend += across / MathF.Max(across.Length(), .05f) * falloff * strengths[i] * bladeHeight * motion.X;
        }
        float length = bend.Length();
        float cap = bladeHeight * motion.Y;
        if (length > cap && length > 0f) bend *= cap / length;
        return bend;
    }

    static Vector3 Displacement(Vector2 bend, float bladeHeight)
    {
        float drop = bladeHeight - MathF.Sqrt(MathF.Max(0f, bladeHeight * bladeHeight - Vector2.Dot(bend, bend)));
        return new Vector3(bend.X, -drop, bend.Y);
    }

    static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
