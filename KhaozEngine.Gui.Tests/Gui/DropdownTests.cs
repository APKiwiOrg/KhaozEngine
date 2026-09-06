using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class DropdownTests
    {
        static readonly Rect Trigger = new(100, 100, 160, 30);
        // Options render below the trigger, each 30px tall: option 0 at y100..? no -> bottom=130.
        static readonly Vector2 TriggerPt = new(150, 115);
        static readonly Vector2 Option0Pt = new(150, 145);   // 130..160
        static readonly Vector2 Option1Pt = new(150, 175);   // 160..190
        static readonly Vector2 FarOutside = new(500, 500);

        static readonly List<DropdownOption> Opts = new()
        {
            new(LocalizedText.Raw("Low"), 1), new(LocalizedText.Raw("Medium"), 2), new(LocalizedText.Raw("High"), 3),
        };

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

        void Tap(Dropdown d, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); d.Update(p);
            p.Update(Frame(at, true)); d.Update(p);
            p.Update(Frame(at, false)); d.Update(p);
        }

        [Fact]
        public void Tap_trigger_opens_then_closes()
        {
            var d = new Dropdown(Opts, Trigger);
            var p = new Pointer();
            Tap(d, p, TriggerPt);
            Assert.True(d.IsOpen);
            Tap(d, p, TriggerPt);
            Assert.False(d.IsOpen);
        }

        [Fact]
        public void Tap_option_selects_and_closes()
        {
            var d = new Dropdown(Opts, Trigger);
            var p = new Pointer();
            Tap(d, p, TriggerPt);              // open
            Tap(d, p, Option1Pt);              // pick "High" (index 2? no -> option index 1 = "Medium")
            Assert.False(d.IsOpen);
            Assert.True(d.WasChanged);
            Assert.Equal(2, d.SelectedValue);  // "Medium"
        }

        [Fact]
        public void Tap_outside_dismisses_without_changing()
        {
            var d = new Dropdown(Opts, Trigger);
            var p = new Pointer();
            d.SelectByValue(1);
            Tap(d, p, TriggerPt);              // open
            Tap(d, p, FarOutside);             // dismiss
            Assert.False(d.IsOpen);
            Assert.False(d.WasChanged);
            Assert.Equal(1, d.SelectedValue);
        }

        [Fact]
        public void Selecting_the_already_selected_option_closes_without_change()
        {
            var d = new Dropdown(Opts, Trigger);
            var p = new Pointer();
            d.SelectByValue(1);                // "Low" at option index 0
            Tap(d, p, TriggerPt);              // open
            Tap(d, p, Option0Pt);              // re-pick index 0
            Assert.False(d.IsOpen);
            Assert.False(d.WasChanged);
            Assert.Equal(1, d.SelectedValue);
        }

        [Fact]
        public void Opening_the_trigger_does_not_activate_a_covered_control()
        {
            var dropdown = new Dropdown(Opts, Trigger);
            var covered = new Toggle(Trigger);
            var pointer = new Pointer();

            pointer.Update(Frame(TriggerPt, true));
            dropdown.Update(pointer);
            covered.Update(pointer);
            pointer.Update(Frame(TriggerPt, false));
            dropdown.Update(pointer);
            bool coveredChanged = covered.Update(pointer);

            Assert.True(dropdown.IsOpen);
            Assert.False(coveredChanged);
            Assert.False(covered.IsOn);
        }

        [Fact]
        public void Selecting_an_option_does_not_activate_a_covered_toggle()
        {
            var dropdown = new Dropdown(Opts, Trigger);
            var covered = new Toggle(dropdown.OptionBounds(1));
            var pointer = new Pointer();
            Tap(dropdown, pointer, TriggerPt);

            pointer.Update(Frame(Option1Pt, true));
            dropdown.Update(pointer);
            covered.Update(pointer);
            pointer.Update(Frame(Option1Pt, false));
            bool selectionChanged = dropdown.Update(pointer);
            bool coveredChanged = covered.Update(pointer);

            Assert.True(selectionChanged);
            Assert.Equal(2, dropdown.SelectedValue);
            Assert.False(coveredChanged);
            Assert.False(covered.IsOn);
        }

        [Fact]
        public void Selecting_an_option_does_not_open_a_covered_dropdown()
        {
            var dropdown = new Dropdown(Opts, Trigger);
            var covered = new Dropdown(Opts, dropdown.OptionBounds(1));
            var pointer = new Pointer();
            Tap(dropdown, pointer, TriggerPt);

            pointer.Update(Frame(Option1Pt, true));
            dropdown.Update(pointer);
            covered.Update(pointer);
            pointer.Update(Frame(Option1Pt, false));
            dropdown.Update(pointer);
            covered.Update(pointer);

            Assert.False(dropdown.IsOpen);
            Assert.False(covered.IsOpen);
        }

        [Fact]
        public void Dismissing_an_open_list_does_not_activate_the_control_under_the_release()
        {
            var dropdown = new Dropdown(Opts, Trigger);
            var covered = new Toggle(new Rect(480, 485, 40, 30));
            var pointer = new Pointer();
            Tap(dropdown, pointer, TriggerPt);

            pointer.Update(Frame(FarOutside, true));
            dropdown.Update(pointer);
            covered.Update(pointer);
            pointer.Update(Frame(FarOutside, false));
            dropdown.Update(pointer);
            bool coveredChanged = covered.Update(pointer);

            Assert.False(dropdown.IsOpen);
            Assert.False(coveredChanged);
            Assert.False(covered.IsOn);
        }

        [Fact]
        public void SelectByValue_sets_the_selection()
        {
            var d = new Dropdown(Opts, Trigger);
            d.SelectByValue(3);
            Assert.Equal("High", d.SelectedLabel);
        }
    }
}
