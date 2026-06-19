using System.Numerics;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Headless tests for the clip-space-Y correction (pure matrix math; no GPU).</summary>
    public sealed class GpuClipTests
    {
        static readonly Matrix4x4 Vp = Matrix4x4.CreateOrthographicOffCenter(0, 100, 100, 0, -1, 1);

        [Fact]
        public void NotInverted_IsIdentityPassthrough()
        {
            var caps = new GpuCapabilities(clipSpaceYInverted: false, depthRangeZeroToOne: true);
            Assert.Equal(Vp, GpuClip.Correct(Vp, caps));
        }

        [Fact]
        public void Inverted_NegatesClipSpaceYOnly()
        {
            var caps = new GpuCapabilities(clipSpaceYInverted: true, depthRangeZeroToOne: true);
            var corrected = GpuClip.Correct(Vp, caps);

            var p = new Vector4(25f, 60f, 0f, 1f);
            var a = Vector4.Transform(p, Vp);
            var b = Vector4.Transform(p, corrected);

            Assert.Equal(a.X, b.X, 5);
            Assert.Equal(-a.Y, b.Y, 5);   // only clip-space Y flips
            Assert.Equal(a.Z, b.Z, 5);
            Assert.Equal(a.W, b.W, 5);
        }
    }
}
