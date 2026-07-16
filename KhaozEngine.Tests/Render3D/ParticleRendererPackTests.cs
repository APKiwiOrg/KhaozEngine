using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless coverage of the pure per-instance packing for the modern particle pass. The GPU-side
    /// look is covered by the particle showcase dumps and the particles golden.</summary>
    public sealed class ParticleRendererPackTests
    {
        static ParticleSprite Sprite() => new()
        {
            Position = new Vector3(1f, 2f, 3f),
            Velocity = new Vector3(4f, 5f, 6f),
            Size = 0.75f,
            Rotation = 1.25f,
            Color = new Color(0.9f, 0.5f, 0.25f, 0.8f),
            Shape = ParticleShape.Wisp,
            ShapeParam = 0.4f,
            LifeNorm = 0.6f,
            Seed = 0.33f,
            Stretch = 2f,
            Blend = BillboardBlend.Alpha,
        };

        [Fact]
        public void PackInstance_MapsEveryField()
        {
            ParticleRenderer.ParticleInstance p = ParticleRenderer.PackInstance(Sprite());

            Assert.Equal(new Vector4(1f, 2f, 3f, 0.75f), p.CenterSize);
            Assert.Equal(new Vector4(4f, 5f, 6f, 1.25f), p.VelocityRot);
            Assert.Equal((Vector4)new Color(0.9f, 0.5f, 0.25f, 0.8f), p.Color);
            Assert.Equal((int)ParticleShape.Wisp, p.Shape.X);
            Assert.Equal(0.4f, p.Shape.Y);
            Assert.Equal(0.6f, p.Shape.Z);
            Assert.Equal(0.33f, p.Shape.W);
            Assert.Equal(2f, p.Extra.X);
        }

        [Fact]
        public void PackInstance_AdditivityLane_FollowsBlend()
        {
            ParticleSprite s = Sprite();

            s.Blend = BillboardBlend.Alpha;
            Assert.Equal(0f, ParticleRenderer.PackInstance(s).Extra.Y);

            s.Blend = BillboardBlend.Additive;
            Assert.Equal(1f, ParticleRenderer.PackInstance(s).Extra.Y);
        }

        [Fact]
        public void PackInstance_ShapeIds_MatchEnumValues()
        {
            ParticleSprite s = Sprite();
            foreach (ParticleShape shape in new[]
            {
                ParticleShape.SoftGlow, ParticleShape.Ember, ParticleShape.Spark,
                ParticleShape.Wisp, ParticleShape.Ring, ParticleShape.Star,
            })
            {
                s.Shape = shape;
                Assert.Equal((byte)shape, ParticleRenderer.PackInstance(s).Shape.X);
            }
        }
    }
}
