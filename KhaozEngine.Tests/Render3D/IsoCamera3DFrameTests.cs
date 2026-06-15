using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Framing an axis-aligned bounds into the orthographic iso camera: points the camera at the bounds
    /// center and sizes <c>OrthoSize</c> so the whole bounds fits the viewport (with a margin). Pure math.
    /// </summary>
    public class IsoCamera3DFrameTests
    {
        // The largest |x|,|y| of the 8 bounds corners in view space (what the ortho viewport must cover).
        static (float maxX, float maxY) CornerExtent(IsoCamera3D cam, Vector3 center, Vector3 size)
        {
            Matrix4x4 view = cam.View;
            Vector3 h = size * 0.5f;
            float mx = 0f, my = 0f;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        var v = Vector3.Transform(center + new Vector3(sx * h.X, sy * h.Y, sz * h.Z), view);
                        mx = MathF.Max(mx, MathF.Abs(v.X));
                        my = MathF.Max(my, MathF.Abs(v.Y));
                    }
            return (mx, my);
        }

        [Fact]
        public void Frame_PointsAtCenter_AndFitsTheBounds_Tightly()
        {
            var cam = new IsoCamera3D { AspectRatio = 16f / 9f };
            var center = new Vector3(3, 0, -2);
            var size = new Vector3(8, 2, 6);

            cam.Frame(center, size, margin: 1f);

            Assert.Equal(center, cam.Target);
            float halfH = cam.OrthoSize / cam.Zoom / 2f;
            float halfW = halfH * cam.AspectRatio;
            var (mx, my) = CornerExtent(cam, center, size);

            Assert.True(my <= halfH + 1e-3f, $"vertical {my} exceeds {halfH}");
            Assert.True(mx <= halfW + 1e-3f, $"horizontal {mx} exceeds {halfW}");
            // margin 1 => the bounds touch at least one axis (no wasted space).
            Assert.True(my >= halfH - 1e-3f || mx >= halfW - 1e-3f, "fit is not tight");
        }

        [Fact]
        public void Frame_MarginLeavesSlackOnAllSides()
        {
            var cam = new IsoCamera3D { AspectRatio = 1f };
            var center = Vector3.Zero;
            var size = new Vector3(5, 1, 5);

            cam.Frame(center, size, margin: 1.25f);

            float halfH = cam.OrthoSize / cam.Zoom / 2f;
            float halfW = halfH * cam.AspectRatio;
            var (mx, my) = CornerExtent(cam, center, size);

            Assert.True(my < halfH - 1e-3f, "no vertical slack");
            Assert.True(mx < halfW - 1e-3f, "no horizontal slack");
        }

        [Fact]
        public void Frame_WiderAspect_FitsAWideBoundsWithoutVerticalWaste()
        {
            var cam = new IsoCamera3D { AspectRatio = 21f / 9f };
            cam.Frame(Vector3.Zero, new Vector3(20, 1, 2), margin: 1f);
            float halfH = cam.OrthoSize / 2f;
            var (mx, my) = CornerExtent(cam, Vector3.Zero, new Vector3(20, 1, 2));
            float halfW = halfH * cam.AspectRatio;
            Assert.True(mx <= halfW + 1e-3f && my <= halfH + 1e-3f);
        }
    }
}
