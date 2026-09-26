using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// A perspective camera as a pinhole: the eye, the look basis, the tangent of half the vertical field of view and the
/// aspect, taken from a <see cref="FlyCamera3D"/>'s fields and none of its matrices, so a readback held to it shares no
/// projection code with the engine. It works in absolute world space. Pixels are internal pixels from the top-left
/// corner, x right and y down.
/// </summary>
internal readonly record struct Pinhole(Vector3 Eye, Vector3 Forward, Vector3 Right, Vector3 Up, float TanHalfFov, float Aspect)
{
    /// <summary>A detached copy of <paramref name="camera"/>'s state. The look direction is the camera's documented
    /// yaw and pitch basis. With world up, screen right is <c>forward x up</c>, which is -X while looking along +Z.</summary>
    public static Pinhole Of(FlyCamera3D camera)
    {
        float cp = MathF.Cos(camera.Pitch), sp = MathF.Sin(camera.Pitch);
        float cy = MathF.Cos(camera.Yaw), sy = MathF.Sin(camera.Yaw);
        Vector3 forward = Vector3.Normalize(new Vector3(cp * sy, sp, cp * cy));
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        return new Pinhole(camera.Position, forward, right, Vector3.Cross(right, forward),
            MathF.Tan(camera.FieldOfView / 2f), camera.AspectRatio);
    }

    /// <summary>How far in front of the eye <paramref name="point"/> lies along the look direction, which is its
    /// perspective clip w.</summary>
    public float Depth(Vector3 point) => Vector3.Dot(point - Eye, Forward);

    /// <summary>Metres one internal pixel covers vertically at a depth of one metre.</summary>
    public float MetresPerPixel(int height) => 2f * TanHalfFov / height;

    /// <summary>The pixel <paramref name="point"/> lands on. The point must lie in front of the eye.</summary>
    public Vector2 Project(Vector3 point, int width, int height)
    {
        Vector3 offset = point - Eye;
        float depth = Vector3.Dot(offset, Forward);
        Assert.True(depth > 0f, $"{point} is not in front of the eye at {Eye}");
        float x = Vector3.Dot(offset, Right) / (depth * TanHalfFov * Aspect);
        float y = Vector3.Dot(offset, Up) / (depth * TanHalfFov);
        return new Vector2((x + 1f) * .5f * width, (1f - y) * .5f * height);
    }

    /// <summary>The direction from the eye through <paramref name="pixel"/>, scaled to one metre of depth.</summary>
    public Vector3 Ray(Vector2 pixel, int width, int height)
    {
        float x = pixel.X / width * 2f - 1f, y = 1f - pixel.Y / height * 2f;
        return Forward + Right * (x * TanHalfFov * Aspect) + Up * (y * TanHalfFov);
    }
}

/// <summary>
/// Analytic screen motion under two <see cref="Pinhole"/> cameras, this frame's and last frame's. A pixel's ray is cast
/// through this frame's camera, meets the surface, and the point it meets is carried back to where it was last frame and
/// projected through last frame's camera. Motion is this frame's position minus last frame's, in internal pixels.
/// </summary>
internal static class PerspectiveExpectation
{
    /// <summary>The last-frame depth at or below which the motion write gives <see cref="OffScreen"/>, restated from
    /// the write's guard.</summary>
    public const float MinPreviousDepth = 1e-6f;

    /// <summary>The guarded motion, UV (2, 2), in internal pixels.</summary>
    public static Vector2 OffScreen(int width, int height) => new(2f * width, 2f * height);

    /// <summary>The motion under pixel (<paramref name="x"/>, <paramref name="y"/>) of a jittered image of the surface
    /// <paramref name="hit"/> finds. The rasteriser sampled the pixel's centre, which is <paramref name="jitter"/> away
    /// from the unjittered point the motion is measured at. <paramref name="hit"/> takes the eye and the ray and returns
    /// the ray parameter of the surface, or NaN for a miss, and <paramref name="pointThen"/> takes the point the ray meets
    /// and returns where it was last frame. A miss expects NaN, which fails the readback at that pixel.</summary>
    public static Vector2 Surface(Pinhole now, Pinhole then, int x, int y, int width, int height, Vector2 jitter,
        Func<Vector3, Vector3, float> hit, Func<Vector3, Vector3> pointThen)
    {
        Vector2 at = new Vector2(x + .5f, y + .5f) - jitter;
        Vector3 ray = now.Ray(at, width, height);
        float t = hit(now.Eye, ray);
        if (!(t > 0f)) return new Vector2(float.NaN, float.NaN);
        Vector3 before = pointThen(now.Eye + ray * t);
        return then.Depth(before) <= MinPreviousDepth ? OffScreen(width, height) : at - then.Project(before, width, height);
    }

    /// <summary>The motion of a still plane, the points <c>p</c> with <c>dot(normal, p) = offset</c>.</summary>
    public static Vector2 StaticPlane(Pinhole now, Pinhole then, int x, int y, int width, int height, Vector2 jitter,
        Vector3 normal, float offset) =>
        Surface(now, then, x, y, width, height, jitter, (eye, ray) => PlaneHit(eye, ray, normal, offset), p => p);

    /// <summary>The motion of an axis-aligned box of half-size <paramref name="half"/> that moved from
    /// <paramref name="centreThen"/> to <paramref name="centreNow"/>.</summary>
    public static Vector2 MovedBox(Pinhole now, Pinhole then, int x, int y, int width, int height, Vector2 jitter,
        Vector3 centreNow, Vector3 centreThen, Vector3 half) =>
        Surface(now, then, x, y, width, height, jitter, (eye, ray) => BoxHit(eye, ray, centreNow - half, centreNow + half),
            p => p - centreNow + centreThen);

    /// <summary>The motion of a point that moved from <paramref name="pointThen"/> to <paramref name="pointNow"/>.</summary>
    public static Vector2 Moved(Pinhole now, Pinhole then, Vector3 pointNow, Vector3 pointThen, int width, int height) =>
        now.Project(pointNow, width, height) - then.Project(pointThen, width, height);

    /// <summary>The ray parameter where <paramref name="origin"/> plus t times <paramref name="ray"/> meets the plane
    /// <c>dot(normal, p) = offset</c>, or NaN when the ray runs parallel to it.</summary>
    public static float PlaneHit(Vector3 origin, Vector3 ray, Vector3 normal, float offset)
    {
        float along = Vector3.Dot(normal, ray);
        return along == 0f ? float.NaN : (offset - Vector3.Dot(normal, origin)) / along;
    }

    /// <summary>The ray parameter where the ray enters the box from <paramref name="min"/> to <paramref name="max"/>,
    /// or NaN for a miss. A pixel centre on the rasterised silhouette can fall a rounding error outside the exact box,
    /// so a miss retries against the box grown by a millimetre, which moves no interior hit.</summary>
    public static float BoxHit(Vector3 origin, Vector3 ray, Vector3 min, Vector3 max)
    {
        float t = SlabEntry(origin, ray, min, max);
        return float.IsNaN(t) ? SlabEntry(origin, ray, min - new Vector3(1e-3f), max + new Vector3(1e-3f)) : t;
    }

    static float SlabEntry(Vector3 origin, Vector3 ray, Vector3 min, Vector3 max)
    {
        float enter = float.NegativeInfinity, leave = float.PositiveInfinity;
        for (int axis = 0; axis < 3; axis++)
        {
            float o = origin[axis], d = ray[axis];
            if (d == 0f)
            {
                if (o < min[axis] || o > max[axis]) return float.NaN;
                continue;
            }
            float a = (min[axis] - o) / d, b = (max[axis] - o) / d;
            enter = MathF.Max(enter, MathF.Min(a, b));
            leave = MathF.Min(leave, MathF.Max(a, b));
        }
        return enter <= leave && enter > 0f ? enter : float.NaN;
    }
}
