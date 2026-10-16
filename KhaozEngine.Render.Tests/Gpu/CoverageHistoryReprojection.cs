using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The source's nearest/own motion branch, conditioned on captured motion, depth and resolve uniforms.</summary>
internal static class CoverageHistoryReprojection
{
    internal readonly record struct Site(Vector2 Uv, bool Own, bool OnScreen);

    internal static Site At(int x, int y, int cx, int cy, CoverageTargetReadback depth, CoverageHistoryInputs input)
    {
        var u = input.Resolve;
        Vector2 size = new(depth.Width, depth.Height), jitter = new(u.Jitter.X, u.Jitter.Y);
        Vector2 uv = new((x + .5f) / input.Previous!.Width, (y + .5f) / input.Previous!.Height);
        int nearest = cy * depth.Width + cx;
        float closest = 3e30f;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int at = Math.Clamp(cy + dy, 0, depth.Height - 1) * depth.Width + Math.Clamp(cx + dx, 0, depth.Width - 1);
                float d = ViewDepth(input.Motion.Motion[at], depth.Values[at], u.CurrentDepth);
                if (d < closest) { closest = d; nearest = at; }
            }
        Vector2 motion = input.Motion.Motion[nearest], centreMotion = input.Motion.Motion[cy * depth.Width + cx];
        bool background = MotionMath.IsBackground(motion), centreBackground = MotionMath.IsBackground(centreMotion);
        float centreDepth = ViewDepth(centreMotion, depth.Values[cy * depth.Width + cx], u.CurrentDepth);
        Vector2 sample = (new Vector2(nearest % depth.Width, nearest / depth.Width) + new Vector2(.5f) - jitter) / size;
        bool tested = true;
        if (!background)
        {
            var staticUv = TemporalResolveMath.StaticPreviousUv(u, new(sample.X * 2 - 1, 1 - sample.Y * 2), closest);
            float threshold = TemporalResolveTuning.MovingSurfaceInternalPixels
                + (motion * size).Length() * TemporalResolveTuning.MovingSurfaceMotionFraction;
            tested = staticUv is { } prior && ((prior - (sample - motion)) * size).Length() <= threshold;
        }
        Vector2 centreUv = (new Vector2(cx, cy) + new Vector2(.5f) - jitter) / size;
        Vector2 centreOwn = centreBackground ? centreUv - BackgroundUv(centreUv, u) : centreMotion;
        float edge = background ? 0 : ((motion - centreOwn) * size).Length();
        bool own = !tested && edge > TemporalResolveTuning.DilationReachInternalPixels
            && centreDepth > closest * (1 + TemporalResolveTuning.DisocclusionTolerance);
        if (own) { motion = centreMotion; background = centreBackground; }
        Vector2 previousUv = background ? BackgroundUv(uv, u) : uv - motion;
        return new(previousUv, own, previousUv.X >= 0 && previousUv.Y >= 0 && previousUv.X <= 1 && previousUv.Y <= 1);
    }

    static Vector2 BackgroundUv(Vector2 uv, TemporalResolveUniforms u)
    {
        if (u.Params.Y > .5f) return uv;
        var clip = Vector4.Transform(new Vector4(uv.X * 2 - 1, 1 - uv.Y * 2, 1, 1), u.BackgroundToPrevious);
        return clip.W > 1e-6f ? new(clip.X / clip.W * .5f + .5f, .5f - clip.Y / clip.W * .5f) : new(-1);
    }

    static float ViewDepth(Vector2 motion, float ndc, Vector4 p) => MotionMath.IsBackground(motion) ? 1e30f
        : p.X > .5f ? p.Y * p.Z / (p.Z - ndc * (p.Z - p.Y)) : p.Y + ndc * (p.Z - p.Y);
}
