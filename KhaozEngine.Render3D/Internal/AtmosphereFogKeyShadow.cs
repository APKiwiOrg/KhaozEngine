using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal;

/// <summary>CPU mirror of point shadow taps in the camera-to-receiver fog segment.</summary>
internal static class AtmosphereFogKeyShadow
{
    /// <summary>Inverts a uniform-density grey segment's normalized in-scatter integral, then the aerial
    /// onset. The fully opaque limit puts every finite share at the camera. Mirrors atmosphereFogTapDistance.</summary>
    internal static double TapDistance(double distanceWorld, double onsetWorld, double greyTransmission, double u)
    {
        if (distanceWorld == 0 || greyTransmission <= 0) return 0;
        double fraction = greyTransmission >= 1 - 1e-6 ? u
            : -Math.Log(1 - u * (1 - greyTransmission)) / -Math.Log(greyTransmission);
        double opticalDistance = AtmosphereFog.OnsetDistance(distanceWorld, onsetWorld) * fraction;
        return onsetWorld == 0 ? opticalDistance
            : Math.Min(distanceWorld, (opticalDistance + Math.Sqrt(opticalDistance * opticalDistance
                + 4 * opticalDistance * onsetWorld)) / 2);
    }

    /// <summary>Estimates key visibility with one point depth comparison per equal-weight tap. The callback
    /// reads the atlas at explicit LOD zero. Uncovered taps are lit. Mirrors atmosphereFogKeyVisibility.</summary>
    internal static float Visibility(Vector3 camera, Vector3 fragment, Vector3 transmission, float onsetWorld,
        int samples, KeyLightSource keySource, ReadOnlySpan<Matrix4x4> cascades, Vector4 shadowParams,
        Vector4 shadowParams2, Func<Vector2, float> pointDepth)
    {
        if (samples == 0 || shadowParams.Y <= 0 || keySource == KeyLightSource.None) return 1;
        var view = AtmosphereFog.SegmentView(camera, fragment, out float distance);
        double grey = ((double)transmission.X + transmission.Y + transmission.Z) / 3;
        int count = Math.Min((int)(shadowParams.X + .5f), cascades.Length), previous = 0;
        float sum = 0, margin = shadowParams2.X * 2;
        for (int i = 0; i < 16; i++)
        {
            if (i >= samples) break;
            var point = camera + view * (float)TapDistance(distance, onsetWorld, grey, (i + .5) / samples);
            float lit = 1;
            for (int c = previous; c < count; c++)
            {
                var clip = Vector4.Transform(new Vector4(point, 1), cascades[c]);
                if (clip.W <= 0) continue;
                var projection = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
                var uv = new Vector2(projection.X * .5f + .5f, 1 - (projection.Y * .5f + .5f));
                if (uv.X < margin || uv.X > 1 - margin || uv.Y < margin || uv.Y > 1 - margin
                    || projection.Z < 0 || projection.Z > 1) continue;
                previous = c;
                float atlasScale = 1f / count;
                var atlasUv = new Vector2(uv.X * atlasScale + c * atlasScale, uv.Y);
                float depth = projection.Z - (shadowParams.Z + shadowParams.W);
                lit = depth <= pointDepth(atlasUv) ? 1 : 0;
                if (lit < 1 && c == count - 1)
                {
                    float edge = MathF.Min(MathF.Min(uv.X, 1 - uv.X), MathF.Min(uv.Y, 1 - uv.Y));
                    float t = Math.Clamp(edge / shadowParams2.Z, 0, 1);
                    lit = 1 - t * t * (3 - 2 * t);
                }
                break;
            }
            sum += lit;
        }
        float visibility = sum / samples;
        return visibility < 1 ? 1 + (visibility - 1) * shadowParams.Y : 1;
    }
}
