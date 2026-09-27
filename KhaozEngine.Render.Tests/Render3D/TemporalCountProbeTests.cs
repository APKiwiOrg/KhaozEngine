using System;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class TemporalCountProbeTests
    {
        [Fact]
        public void TheStoredShareDecodesToTheExactSampleCount()
        {
            // The probe writes k / 16 into an 8-bit channel. Round to nearest, either way at the half, must come
            // back as k for every k.
            for (int k = 0; k <= TemporalCountProbe.SamplesPerCell; k++)
            {
                double stored = k / 16.0 * 255.0;
                Assert.Equal(k, TemporalCountProbe.Samples((byte)Math.Floor(stored + 0.5)));
                Assert.Equal(k, TemporalCountProbe.Samples((byte)Math.Ceiling(stored - 0.5)));
            }
        }

        [Fact]
        public void TheGridIsTheDocumentedSize() => Assert.Equal(9216, TemporalCountProbe.TotalSamples);
    }
}
