using System.Reflection;
using KhaozEngine.Gui;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The consumer-facing half of <see cref="GuiDraw"/>. A game drawing its own chrome in its own 2D pass needs
    /// the same rect primitives the widgets use, and this test assembly sees the internal ones through
    /// InternalsVisibleTo, so a plain call would compile whatever the accessibility is. The check is therefore
    /// reflection over the PUBLIC binding flags, which is exactly the surface a game outside the engine sees.
    /// </summary>
    public class GuiDrawPublicSurfaceTests
    {
        static MethodInfo? PublicStatic(string name) =>
            typeof(GuiDraw).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

        [Theory]
        [InlineData("Fill")]
        [InlineData("Border")]
        [InlineData("Line")]
        public void Primitive_is_public(string name) =>
            Assert.NotNull(PublicStatic(name));

        [Fact]
        public void Border_keeps_its_thickness_parameter()
        {
            MethodInfo? m = PublicStatic("Border");
            Assert.NotNull(m);
            Assert.Contains(m!.GetParameters(), p => p.Name == "thickness");
        }

        [Fact]
        public void Widget_plumbing_stays_internal()
        {
            // The type is not blanket-public: the styled fill and the skin path are widget plumbing that a
            // consumer has no contract with, so they must stay off the public surface.
            Assert.Null(PublicStatic("FillStyled"));
            Assert.Null(PublicStatic("DrawSkin"));
        }
    }
}
