using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Pure packing for the starfield background pass: the clear colour reaches the shader's BgColor and
    /// the render size becomes the 1/size the fragment multiplies gl_FragCoord by to rebuild its UV.</summary>
    public class StarfieldPackUboTests
    {
        [Fact]
        public void Packs_the_background_colour_and_inverse_render_size()
        {
            var u = StarfieldRenderer.PackUbo(new Color(0.25f, 0.5f, 0.75f, 1f), 800, 400);
            Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 0f), u.BgColor);
            Assert.Equal(1f / 800f, u.Res.X, 6);
            Assert.Equal(1f / 400f, u.Res.Y, 6);
        }

        [Fact]
        public void Zero_render_size_packs_zero_rather_than_dividing_by_zero()
        {
            var u = StarfieldRenderer.PackUbo(new Color(0f, 0f, 0f, 1f), 0, 0);
            Assert.Equal(0f, u.Res.X);
            Assert.Equal(0f, u.Res.Y);
        }
    }
}
