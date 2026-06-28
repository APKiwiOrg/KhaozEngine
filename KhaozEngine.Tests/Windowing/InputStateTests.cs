using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    public class InputStateTests
    {
        [Fact]
        public void Helpers_reflect_the_snapshot_sets()
        {
            var s = new InputState(
                down: new HashSet<Key> { Key.A, Key.Space },
                pressed: new HashSet<Key> { Key.Space },
                released: new HashSet<Key> { Key.W },
                mouseDown: new HashSet<MouseButton> { MouseButton.Left },
                mousePressed: new HashSet<MouseButton>(),
                mousePosition: new Vector2(10, 20), mouseDelta: new Vector2(1, 2), scrollDelta: 0.5f,
                width: 800, height: 600);

            Assert.True(s.IsDown(Key.A));
            Assert.False(s.WasPressed(Key.A));     // held, not newly pressed
            Assert.True(s.WasPressed(Key.Space));
            Assert.True(s.WasReleased(Key.W));
            Assert.True(s.IsDown(MouseButton.Left));
            Assert.False(s.WasPressed(MouseButton.Left));
            Assert.Equal(new Vector2(10, 20), s.MousePosition);
            Assert.Equal(0.5f, s.ScrollDelta);
            Assert.Equal(800, s.Width);
        }

        [Fact]
        public void Empty_is_all_false()
        {
            Assert.False(InputState.Empty.IsDown(Key.A));
            Assert.False(InputState.Empty.WasPressed(Key.Space));
            Assert.False(InputState.Empty.IsDown(MouseButton.Left));
        }

        [Fact]
        public void WindowFocused_defaults_true_when_the_param_is_omitted()
        {
            // Existing builders (and games) that never pass the new trailing arg keep reporting focused,
            // preserving the current hit-test behaviour.
            var s = new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 800, 600);

            Assert.True(s.WindowFocused);
        }

        [Fact]
        public void WindowFocused_round_trips_through_the_constructor()
        {
            var focused = new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 800, 600, windowFocused: true);
            var unfocused = new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 800, 600, windowFocused: false);

            Assert.True(focused.WindowFocused);
            Assert.False(unfocused.WindowFocused);
        }

        [Fact]
        public void Empty_is_not_focused()
        {
            // A blank snapshot is genuinely "no window / not focused".
            Assert.False(InputState.Empty.WindowFocused);
        }

        [Fact]
        public void KeysRepeated_is_empty_when_the_param_is_omitted()
        {
            // Existing builders (and games) that never pass the trailing repeat arg see no repeats.
            var s = new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                Vector2.Zero, Vector2.Zero, 0, 800, 600);

            Assert.Empty(s.KeysRepeated);
            Assert.False(s.WasRepeated(Key.A));
            Assert.False(s.WasTyped(Key.A));
        }

        [Fact]
        public void WasTyped_is_true_on_a_press_edge_and_a_repeat_tick_but_WasPressed_excludes_repeat()
        {
            // A held key past the OS repeat delay: repeated, still down, but NOT a fresh press edge.
            var s = new InputState(
                down: new HashSet<Key> { Key.A, Key.Backspace },
                pressed: new HashSet<Key> { Key.A },     // 'A' is the press edge this frame
                released: new HashSet<Key>(),
                mouseDown: new HashSet<MouseButton>(), mousePressed: new HashSet<MouseButton>(),
                mousePosition: Vector2.Zero, mouseDelta: Vector2.Zero, scrollDelta: 0,
                width: 800, height: 600, repeated: new HashSet<Key> { Key.Backspace });

            // Backspace is a repeat tick: WasRepeated/WasTyped true, but WasPressed (press edge only) false.
            Assert.True(s.WasRepeated(Key.Backspace));
            Assert.True(s.WasTyped(Key.Backspace));
            Assert.False(s.WasPressed(Key.Backspace));

            // 'A' is a press edge: WasPressed true and WasTyped true, but it is not in the repeat set.
            Assert.True(s.WasPressed(Key.A));
            Assert.True(s.WasTyped(Key.A));
            Assert.False(s.WasRepeated(Key.A));
        }
    }
}
