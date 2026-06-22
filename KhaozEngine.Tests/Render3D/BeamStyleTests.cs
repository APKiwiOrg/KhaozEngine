using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class BeamStyleTests
    {
        [Fact]
        public void Default_HasNullColours_AndSensibleShape()
        {
            var d = BeamStyle.Default;
            Assert.Null(d.CoreColor);    // null => the DrawBeam colour argument tints the beam
            Assert.Null(d.GlowColor);
            Assert.Equal(0.35f, d.CoreFraction, 4);
            Assert.Equal(2f, d.GlowSoftness, 4);
            Assert.Equal(0f, d.Taper, 4);
            Assert.Equal(0f, d.PulseSpeed, 4);
            Assert.Equal(0f, d.PulseAmount, 4);
            Assert.Equal(0f, d.ScrollSpeed, 4);
        }

        [Fact]
        public void With_OverridesSingleField_LeavingOthers()
        {
            var s = BeamStyle.Default with { PulseSpeed = 6f, PulseAmount = 0.3f, Taper = 0.2f };
            Assert.Equal(6f, s.PulseSpeed, 4);
            Assert.Equal(0.3f, s.PulseAmount, 4);
            Assert.Equal(0.2f, s.Taper, 4);
            Assert.Equal(0.35f, s.CoreFraction, 4);   // unchanged from Default
            Assert.Null(s.CoreColor);                  // unchanged from Default
        }
    }
}
