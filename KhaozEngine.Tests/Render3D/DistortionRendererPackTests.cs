using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of the pure per-instance packing for the screen-space distortion pass. The GPU-side
    /// look is covered by the distortion showcase dumps, the distortion golden, and the behaviour GpuFacts.</summary>
    public sealed class DistortionRendererPackTests
    {
        static DistortionSprite Sprite() => new()
        {
            Position = new Vector3(1f, 2f, 3f),
            Size = 0.75f,
            Rotation = 1.25f,
            Shape = DistortionShape.Heat,
            ShapeParam = 0.4f,
            Strength = 0.6f,
            LifeNorm = 0.3f,
            Seed = 0.33f,
        };

        [Fact]
        public void PackInstance_MapsEveryField()
        {
            DistortionRenderer.DistortionInstance p = DistortionRenderer.PackInstance(Sprite());

            Assert.Equal(new Vector4(1f, 2f, 3f, 0.75f), p.CenterSize);
            Assert.Equal((int)DistortionShape.Heat, p.ShapeLife.X);
            Assert.Equal(0.4f, p.ShapeLife.Y);
            Assert.Equal(0.3f, p.ShapeLife.Z);
            Assert.Equal(0.33f, p.ShapeLife.W);
            Assert.Equal(0.6f, p.Extra.X);   // strength
            Assert.Equal(1.25f, p.Extra.Y);  // rotation
            // Defaults: camera-facing orientation, soft-fade scale 0 packs as the neutral 1.
            Assert.Equal(0f, p.Extra.Z);
            Assert.Equal(1f, p.Extra.W);
        }

        [Fact]
        public void PackInstance_OrientationAndFadeScale_Lanes()
        {
            DistortionSprite s = Sprite();
            s.Orientation = ParticleOrientation.FlatGround;
            s.SoftFadeScale = 0.12f;

            DistortionRenderer.DistortionInstance p = DistortionRenderer.PackInstance(s);
            Assert.Equal(1f, p.Extra.Z);
            Assert.Equal(0.12f, p.Extra.W);
        }

        [Fact]
        public void PackInstance_ShapeIds_MatchEnumValues()
        {
            DistortionSprite s = Sprite();
            foreach (DistortionShape shape in new[]
            {
                DistortionShape.Ripple, DistortionShape.Heat, DistortionShape.Lens,
            })
            {
                s.Shape = shape;
                Assert.Equal((byte)shape, DistortionRenderer.PackInstance(s).ShapeLife.X);
            }
        }

        [Fact]
        public void PackInstance_NegativeStrength_SurvivesForLensPinch()
        {
            // Lens reads a signed strength (magnify vs pinch), so a negative value must pass through unclamped.
            DistortionSprite s = Sprite();
            s.Shape = DistortionShape.Lens;
            s.Strength = -0.4f;
            Assert.Equal(-0.4f, DistortionRenderer.PackInstance(s).Extra.X);
        }
    }
}
