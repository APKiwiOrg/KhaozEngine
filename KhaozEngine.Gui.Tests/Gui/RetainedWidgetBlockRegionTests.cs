using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Every interactive retained widget must reserve its rect on the pointer during Update (the click-through
    /// gate), so a layer beneath sees <see cref="Pointer.IsBlocked"/> and can't be clicked through the widget.
    /// This was the click-through bug the audit flagged for the retained path; <see cref="Button"/> already did
    /// it, these widgets now do too.
    /// </summary>
    public class RetainedWidgetBlockRegionTests
    {
        static readonly Rect R = new(100, 100, 120, 40);
        static readonly Vector2 Inside = new(160, 120);
        static readonly Vector2 Outside = new(10, 10);

        // One per test-class instance (xUnit builds a fresh instance per fact), so the mouse press and
        // release edges derive from this test's own frame sequence and nothing crosses between tests.
        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(b);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        Pointer At(Vector2 pos)
        {
            var p = new Pointer();
            p.Update(Frame(pos, false));
            return p;
        }

        // A press-origin tap (press and release both inside), the way the dropdown opens.
        void Tap(Dropdown d, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); d.Update(p);
            p.Update(Frame(at, true)); d.Update(p);
            p.Update(Frame(at, false)); d.Update(p);
        }

        static readonly List<DropdownOption> Opts = new() { new("a", 1), new("b", 2), new("c", 3) };

        [Fact]
        public void Toggle_reserves_bounds()
        {
            var p = At(Inside);
            new Toggle(R).Update(p);
            Assert.True(p.IsBlocked(Inside));
            Assert.False(p.IsBlocked(Outside));
        }

        [Fact]
        public void Disabled_Toggle_still_reserves_bounds()
        {
            var p = At(Inside);
            new Toggle(R) { Enabled = false }.Update(p);
            Assert.True(p.IsBlocked(Inside));
        }

        [Fact]
        public void Slider_reserves_bounds()
        {
            var p = At(Inside);
            new Slider(R).Update(p);
            Assert.True(p.IsBlocked(Inside));
            Assert.False(p.IsBlocked(Outside));
        }

        [Fact]
        public void TextInput_reserves_bounds()
        {
            var p = At(Inside);
            new TextInput(R).Update(p, Frame(Inside, false), 0.016f);
            Assert.True(p.IsBlocked(Inside));
            Assert.False(p.IsBlocked(Outside));
        }

        [Fact]
        public void Closed_Dropdown_reserves_only_the_trigger()
        {
            var p = At(Inside);
            var dd = new Dropdown(Opts, R);
            dd.Update(p);
            Assert.True(dd.IsOpen == false);
            Assert.True(p.IsBlocked(Inside));                          // trigger reserved
            Assert.False(p.IsBlocked(new Vector2(160, 200)));         // list area NOT reserved while closed
        }

        [Fact]
        public void Open_Dropdown_reserves_the_whole_expanded_list()
        {
            var dd = new Dropdown(Opts, R);
            var p = new Pointer();
            Tap(dd, p, Inside);                                        // open it
            Assert.True(dd.IsOpen);

            p.Update(Frame(Inside, false));
            dd.Update(p);                                              // an Update while open
            Assert.True(p.IsBlocked(Inside));                          // trigger
            Assert.True(p.IsBlocked(new Vector2(160, 160)));          // an option row below the trigger (R.Bottom=140)
        }
    }
}
