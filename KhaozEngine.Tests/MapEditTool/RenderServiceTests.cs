using System;
using System.IO;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEdit;
using KhaozEngine.Render2D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;
using SampleDocs = KhaozEngine.Tests.MapDoc.MapDocumentFileTests;

namespace KhaozEngine.Tests.MapEditTool
{
    /// <summary>Headless-render tests for <see cref="RenderService"/>. The GPU-touching cases are gated behind
    /// <see cref="GpuFactAttribute"/> (they need a real headless device, KE_GPU_TESTS=1 on the dev Mac's Metal), and
    /// assert structurally only: a decodable PNG of the requested size that is not a uniform fill, and that the
    /// overlay pass actually changes pixels. No goldens (the cross-backend golden bake is out of scope here). The
    /// error paths (zero look direction, no document open) are plain facts that fire before any GPU work.</summary>
    public class RenderServiceTests
    {
        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-mapedit-render-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static (MapEditSession session, RenderService render) OpenSample(string dir)
        {
            string path = Path.Combine(dir, "zone.map.json");
            MapDocumentFile.Save(SampleDocs.SampleDoc(), path);
            var session = new MapEditSession();
            session.Open(path);
            return (session, new RenderService(session));
        }

        // PNG signature: the eight-byte magic every PNG stream opens with.
        static void AssertPngMagic(byte[] png)
        {
            Assert.True(png.Length > 8, "render returned fewer than the 8 PNG signature bytes.");
            Assert.Equal(0x89, png[0]);
            Assert.Equal(0x50, png[1]); // P
            Assert.Equal(0x4E, png[2]); // N
            Assert.Equal(0x47, png[3]); // G
        }

        // Min and max of the red channel across the decoded image, the cheap "is this a flat fill" probe.
        static (int min, int max) RedRange(ImageRgba img)
        {
            int min = 255, max = 0;
            for (int i = 0; i < img.Pixels.Length; i += 4)
            {
                byte r = img.Pixels[i];
                if (r < min) min = r;
                if (r > max) max = r;
            }
            return (min, max);
        }

        [GpuFact]
        public void RenderTopDown_ProducesDecodablePngWithVariance()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession _, RenderService render) = OpenSample(dir);

                byte[] png = render.RenderTopDown(width: 256, height: 256);

                AssertPngMagic(png);
                ImageRgba img = ImageRgba.Decode(png);
                Assert.Equal(256, img.Width);
                Assert.Equal(256, img.Height);

                (int min, int max) = RedRange(img);
                Assert.True(max - min > 8, $"top-down render looks uniform (red min={min} max={max}).");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [GpuFact]
        public void RenderTopDown_OverlaysChangePixels()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession _, RenderService render) = OpenSample(dir);

                // SampleDoc carries a disc exclusion and a disc region, so the overlay pass must paint fills that
                // are absent when overlays are off. Same rect and size, only the overlay flag differs.
                byte[] withOverlays = render.RenderTopDown(width: 256, height: 256, includeOverlays: true);
                byte[] withoutOverlays = render.RenderTopDown(width: 256, height: 256, includeOverlays: false);

                AssertPngMagic(withOverlays);
                AssertPngMagic(withoutOverlays);
                Assert.False(BytesEqual(withOverlays, withoutOverlays),
                    "overlay pass did not change any pixels (exclusion/region fills missing).");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        [GpuFact]
        public void RenderView_ProducesDecodablePngWithVariance()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession session, RenderService render) = OpenSample(dir);
                TerrainField field = session.Field();

                // Eye above the flatten disc (centre -32, 22), looking across at the lake centre (34, -14).
                var eye = new Vector3(-32f, field.SampleHeight(-32f, 22f) + 45f, 22f);
                var target = new Vector3(34f, field.SampleHeight(34f, -14f), -14f);

                byte[] png = render.RenderView(eye.X, eye.Y, eye.Z, target.X, target.Y, target.Z,
                    width: 256, height: 256);

                AssertPngMagic(png);
                ImageRgba img = ImageRgba.Decode(png);
                Assert.Equal(256, img.Width);
                Assert.Equal(256, img.Height);

                (int min, int max) = RedRange(img);
                Assert.True(max - min > 8, $"perspective render looks uniform (red min={min} max={max}).");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void RenderView_ZeroDirection_Throws()
        {
            string dir = NewTempDir();
            try
            {
                (MapEditSession _, RenderService render) = OpenSample(dir);

                // Eye and target coincide: the look direction is zero, rejected before any GPU work.
                ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                    render.RenderView(5f, 10f, 5f, 5f, 10f, 5f));
                Assert.Contains("direction", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void RenderWithoutDocument_ThrowsNamingMapOpen()
        {
            var session = new MapEditSession();
            var render = new RenderService(session);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                render.RenderTopDown(width: 64, height: 64));
            Assert.Contains("map_open", ex.Message, StringComparison.Ordinal);
        }
    }
}
