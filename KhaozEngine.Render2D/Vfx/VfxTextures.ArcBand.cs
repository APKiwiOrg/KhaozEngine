using System;
using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    // The anti-aliased arc band bake. Split from VfxTextures.cs because the signed-distance field and its
    // bounding-box helper are a body of maths of their own, not another two-line radial falloff.
    public static partial class VfxTextures
    {
        // A sweep at or beyond a full turn is a closed ring: no caps, no cap distance, no bounding-box corners.
        const float FullTurn = MathF.Tau - 1e-4f;

        /// <summary>
        /// Bakes an anti-aliased arc band (annulus sector) into a tightly-packed RGBA8 buffer of
        /// <paramref name="width"/>x<paramref name="height"/> pixels (row-major, top-left origin). White RGB,
        /// alpha = coverage. Everything is in PIXELS of the target image, and the sample point for pixel
        /// (x, y) is its centre (x + 0.5, y + 0.5), so <paramref name="centre"/> (the centre of CURVATURE) may
        /// legitimately lie outside the image: that is how a shallow arc is baked without a texture the size of
        /// its whole circle. Use <see cref="ArcBandImageSize"/> to get that image size and offset.
        /// <para>
        /// Coverage comes from a signed distance to the sector: radial distance to the inner and outer edges,
        /// combined with the angular distance to the two end caps, then <c>smoothstep</c> over
        /// <paramref name="featherPixels"/> centred on the edge, so one pixel of feather gives a clean
        /// anti-aliased edge and a pixel sitting exactly on an edge comes out at half alpha.
        /// </para>
        /// <para>
        /// Angles follow the rest of Render2D: screen space with +y down, angle 0 along +x, and a POSITIVE
        /// <paramref name="sweepRadians"/> running clockwise on screen. Either sign is accepted and describes
        /// the same sector from the other end. A sweep of a full turn or more drops the caps and bakes a closed
        /// ring. <paramref name="roundCaps"/> rounds each end with a half disc of radius half the band
        /// thickness. Pure / headless.
        /// </para>
        /// </summary>
        public static byte[] BakeArcBandPixels(
            int width,
            int height,
            Vector2 centre,
            float innerRadius,
            float outerRadius,
            float startRadians,
            float sweepRadians,
            float featherPixels = 1f,
            bool roundCaps = false)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            NormalizeBand(ref innerRadius, ref outerRadius);
            NormalizeSweep(startRadians, sweepRadians, out float start, out float sweep);
            if (featherPixels < 0f) featherPixels = 0f;

            float midR = (innerRadius + outerRadius) * 0.5f;
            float halfThick = (outerRadius - innerRadius) * 0.5f;
            bool full = sweep >= FullTurn;
            float half = sweep * 0.5f;
            float mid = start + half;
            float cosMid = MathF.Cos(mid), sinMid = MathF.Sin(mid);
            // The one cap, in the mirrored frame: the segment from inner to outer radius at angle +half.
            float capX = MathF.Cos(half), capY = MathF.Sin(half);
            var capInner = new Vector2(capX * innerRadius, capY * innerRadius);
            var capOuter = new Vector2(capX * outerRadius, capY * outerRadius);
            var capCentre = new Vector2(capX * midR, capY * midR);

            var px = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                float wy = y + 0.5f - centre.Y;
                for (int x = 0; x < width; x++)
                {
                    float wx = x + 0.5f - centre.X;
                    // Rotate into the sector's own frame, then mirror about its axis: the sector is symmetric,
                    // so this halves the problem down to a single cap.
                    float qx = wx * cosMid + wy * sinMid;
                    float qy = MathF.Abs(-wx * sinMid + wy * cosMid);
                    float r = MathF.Sqrt(qx * qx + qy * qy);
                    float dRadial = MathF.Abs(r - midR) - halfThick;

                    float d;
                    if (full)
                    {
                        d = dRadial;
                    }
                    else
                    {
                        float angle = MathF.Atan2(qy, qx);
                        if (angle <= half)
                        {
                            // Angularly inside: the nearest circle point is at this same angle, so the radial
                            // distance is already exact. Inside the band as well, the cap segment may be nearer.
                            d = dRadial <= 0f
                                ? MathF.Max(dRadial, -SegmentDistance(qx, qy, capInner, capOuter))
                                : dRadial;
                        }
                        else
                        {
                            // Angularly outside: every boundary point that could be nearest lies on the cap.
                            d = SegmentDistance(qx, qy, capInner, capOuter);
                        }

                        if (roundCaps)
                        {
                            float rx = qx - capCentre.X, ry = qy - capCentre.Y;
                            d = MathF.Min(d, MathF.Sqrt(rx * rx + ry * ry) - halfThick);
                        }
                    }

                    int i = (y * width + x) * 4;
                    px[i] = 255; px[i + 1] = 255; px[i + 2] = 255;
                    px[i + 3] = Coverage(d, featherPixels);
                }
            }
            return px;
        }

        /// <summary>
        /// The tight image that holds the arc band plus its feather, and where the centre of CURVATURE lands
        /// inside it. A shallow arc off a large radius occupies a small slice of its circle, so this is what
        /// keeps the bake from being the size of the whole circle: radius 150 over 0.8 radians is a couple of
        /// thousand pixels, not ninety thousand. Feed the three values straight to
        /// <see cref="BakeArcBandPixels"/>, and DRAW the resulting texture at <c>anchor - Centre</c>, where
        /// <c>anchor</c> is where the centre of curvature belongs on screen.
        /// </summary>
        public static (int Width, int Height, Vector2 Centre) ArcBandImageSize(
            float innerRadius,
            float outerRadius,
            float startRadians,
            float sweepRadians,
            float featherPixels,
            bool roundCaps)
        {
            NormalizeBand(ref innerRadius, ref outerRadius);
            NormalizeSweep(startRadians, sweepRadians, out float start, out float sweep);
            if (featherPixels < 0f) featherPixels = 0f;

            float minX, maxX, minY, maxY;
            if (sweep >= FullTurn)
            {
                minX = minY = -outerRadius;
                maxX = maxY = outerRadius;
            }
            else
            {
                float end = start + sweep;
                minX = minY = float.MaxValue;
                maxX = maxY = float.MinValue;
                // The four corners. The caps are straight and the arcs are monotone in x and y between any two
                // axis crossings, so nothing else on the boundary can beat these plus the crossings below.
                Include(start, innerRadius, ref minX, ref maxX, ref minY, ref maxY);
                Include(start, outerRadius, ref minX, ref maxX, ref minY, ref maxY);
                Include(end, innerRadius, ref minX, ref maxX, ref minY, ref maxY);
                Include(end, outerRadius, ref minX, ref maxX, ref minY, ref maxY);
                for (int k = 0; k < 4; k++)
                {
                    float axis = k * MathF.PI * 0.5f;
                    float delta = axis - start;
                    delta -= MathF.Tau * MathF.Floor(delta / MathF.Tau);
                    if (delta <= sweep) Include(axis, outerRadius, ref minX, ref maxX, ref minY, ref maxY);
                }
                if (roundCaps)
                {
                    float midR = (innerRadius + outerRadius) * 0.5f;
                    float halfThick = (outerRadius - innerRadius) * 0.5f;
                    IncludeDisc(start, midR, halfThick, ref minX, ref maxX, ref minY, ref maxY);
                    IncludeDisc(end, midR, halfThick, ref minX, ref maxX, ref minY, ref maxY);
                }
            }

            // Half the feather is where coverage reaches zero. The extra pixel guarantees the border row and
            // column of the returned image sample beyond that, so nothing above zero alpha is ever clipped.
            float margin = featherPixels * 0.5f + 1f;
            minX -= margin; minY -= margin;
            maxX += margin; maxY += margin;
            int w = Math.Max(1, (int)MathF.Ceiling(maxX - minX));
            int h = Math.Max(1, (int)MathF.Ceiling(maxY - minY));
            return (w, h, new Vector2(-minX, -minY));
        }

        /// <summary>Bakes an anti-aliased arc band and uploads it to a sampleable texture on <paramref name="surface"/>'s device.</summary>
        public static Texture2D BakeArcBand(
            Render2DSurface surface,
            int width,
            int height,
            Vector2 centre,
            float innerRadius,
            float outerRadius,
            float startRadians,
            float sweepRadians,
            float featherPixels = 1f,
            bool roundCaps = false)
        {
            ArgumentNullException.ThrowIfNull(surface);
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            byte[] rgba = BakeArcBandPixels(width, height, centre, innerRadius, outerRadius, startRadians, sweepRadians, featherPixels, roundCaps);
            return surface.CreateTexture(rgba, width, height);
        }

        /// <summary>Bakes an anti-aliased arc band and uploads it to a sampleable texture on the snapshot <paramref name="context"/>'s device.</summary>
        public static Texture2D BakeArcBand(
            Render2DContext context,
            int width,
            int height,
            Vector2 centre,
            float innerRadius,
            float outerRadius,
            float startRadians,
            float sweepRadians,
            float featherPixels = 1f,
            bool roundCaps = false)
        {
            ArgumentNullException.ThrowIfNull(context);
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            byte[] rgba = BakeArcBandPixels(width, height, centre, innerRadius, outerRadius, startRadians, sweepRadians, featherPixels, roundCaps);
            return context.CreateTexture(rgba, width, height);
        }

        static void NormalizeBand(ref float innerRadius, ref float outerRadius)
        {
            if (innerRadius > outerRadius) (innerRadius, outerRadius) = (outerRadius, innerRadius);
            if (innerRadius < 0f) innerRadius = 0f;
            if (outerRadius < 0f) outerRadius = 0f;
        }

        // A negative sweep is the same sector walked from the other end, so fold it to a positive one.
        static void NormalizeSweep(float startRadians, float sweepRadians, out float start, out float sweep)
        {
            if (sweepRadians < 0f)
            {
                start = startRadians + sweepRadians;
                sweep = -sweepRadians;
            }
            else
            {
                start = startRadians;
                sweep = sweepRadians;
            }
            if (sweep > MathF.Tau) sweep = MathF.Tau;
        }

        static byte Coverage(float distance, float featherPixels)
        {
            float t = featherPixels > 0f
                ? Math.Clamp(0.5f - distance / featherPixels, 0f, 1f)
                : (distance <= 0f ? 1f : 0f);
            float a = t * t * (3f - 2f * t);
            return (byte)Math.Clamp((int)(a * 255f + 0.5f), 0, 255);
        }

        static float SegmentDistance(float px, float py, Vector2 a, Vector2 b)
        {
            float abx = b.X - a.X, aby = b.Y - a.Y;
            float apx = px - a.X, apy = py - a.Y;
            float lenSq = abx * abx + aby * aby;
            float t = lenSq > 1e-12f ? Math.Clamp((apx * abx + apy * aby) / lenSq, 0f, 1f) : 0f;
            float dx = apx - abx * t, dy = apy - aby * t;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        static void Include(float angle, float radius, ref float minX, ref float maxX, ref float minY, ref float maxY)
        {
            float x = MathF.Cos(angle) * radius, y = MathF.Sin(angle) * radius;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        static void IncludeDisc(float angle, float radius, float discRadius, ref float minX, ref float maxX, ref float minY, ref float maxY)
        {
            float x = MathF.Cos(angle) * radius, y = MathF.Sin(angle) * radius;
            if (x - discRadius < minX) minX = x - discRadius;
            if (x + discRadius > maxX) maxX = x + discRadius;
            if (y - discRadius < minY) minY = y - discRadius;
            if (y + discRadius > maxY) maxY = y + discRadius;
        }
    }
}
