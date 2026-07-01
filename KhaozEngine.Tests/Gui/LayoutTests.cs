using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Anchor-based layout: resolve a child <see cref="Rect"/> against a parent rect (usually the design
    /// viewport) from an <see cref="Anchor"/> + size + margin, so widgets stop hard-coding absolute pixels and
    /// stay placed correctly at any design size. Pure math, headless.
    /// </summary>
    public class LayoutTests
    {
        static readonly Rect Parent = new(0, 0, 960, 540);

        [Fact]
        public void Center_PlacesSizedRectInTheMiddle()
        {
            Rect r = Layout.Resolve(Parent, Anchor.Center, 200, 52);
            Assert.Equal(new Rect((960 - 200) / 2f, (540 - 52) / 2f, 200, 52), r);
        }

        [Fact]
        public void TopLeft_RespectsMargin()
        {
            Rect r = Layout.Resolve(Parent, Anchor.TopLeft, 100, 40, marginX: 16, marginY: 12);
            Assert.Equal(new Rect(16, 12, 100, 40), r);
        }

        [Fact]
        public void BottomRight_InsetsByMarginFromTheFarEdges()
        {
            Rect r = Layout.Resolve(Parent, Anchor.BottomRight, 100, 40, marginX: 16, marginY: 12);
            Assert.Equal(new Rect(960 - 100 - 16, 540 - 40 - 12, 100, 40), r);
        }

        [Fact]
        public void Top_CentersHorizontally_MarginPushesDown()
        {
            Rect r = Layout.Resolve(Parent, Anchor.Top, 300, 50, marginY: 20);
            Assert.Equal(new Rect((960 - 300) / 2f, 20, 300, 50), r);
        }

        [Fact]
        public void Right_CentersVertically_MarginInsetsFromRight()
        {
            Rect r = Layout.Resolve(Parent, Anchor.Right, 120, 60, marginX: 24);
            Assert.Equal(new Rect(960 - 120 - 24, (540 - 60) / 2f, 120, 60), r);
        }

        [Fact]
        public void Stretch_FillsParentMinusMargin()
        {
            Rect r = Layout.Resolve(Parent, Anchor.Stretch, 0, 0, marginX: 30, marginY: 20);
            Assert.Equal(new Rect(30, 20, 960 - 60, 540 - 40), r);
        }

        [Fact]
        public void Resolve_IsRelativeToAnyParent_NotJustTheViewport()
        {
            var panel = new Rect(100, 100, 400, 200);
            Rect r = Layout.Resolve(panel, Anchor.BottomRight, 80, 30, marginX: 10, marginY: 10);
            Assert.Equal(new Rect(100 + 400 - 80 - 10, 100 + 200 - 30 - 10, 80, 30), r);
        }
    }
}
