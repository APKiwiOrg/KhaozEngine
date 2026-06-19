using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Pure (GPU-free) coverage of Render3DPreview.ClampSize: the offscreen target sizing decision, so a bogus
    // panel size can't allocate an unbounded (or zero/negative) GPU texture. Mirrors RenderScaleTests over
    // Scene3D.ComputeTargetSize.
    public sealed class Render3DPreviewSizingTests
    {
        [Fact]
        public void ClampSize_passes_through_a_normal_size()
        {
            Assert.Equal((128, 128), Render3DPreview.ClampSize(128, 128));
            Assert.Equal((256, 192), Render3DPreview.ClampSize(256, 192));
        }

        [Fact]
        public void ClampSize_floors_zero_and_negative_to_one()
        {
            Assert.Equal((1, 1), Render3DPreview.ClampSize(0, 0));
            Assert.Equal((1, 1), Render3DPreview.ClampSize(-10, -5));
            Assert.Equal((1, 64), Render3DPreview.ClampSize(0, 64));
        }

        [Fact]
        public void ClampSize_caps_oversized_dimensions()
        {
            Assert.Equal((Render3DPreview.MaxDimension, Render3DPreview.MaxDimension),
                Render3DPreview.ClampSize(Render3DPreview.MaxDimension + 1000, 99999));
            Assert.Equal((Render3DPreview.MaxDimension, 200),
                Render3DPreview.ClampSize(Render3DPreview.MaxDimension + 1, 200));
        }
    }
}
