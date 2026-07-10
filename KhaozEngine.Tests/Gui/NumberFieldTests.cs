using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    public class NumberFieldTests
    {
        // The interactive field: X 100..220 (width 120), Y 100..128.
        static readonly Rect Field = new(100, 100, 120, 28);
        static readonly Vector2 Inside = new(160, 114);   // centre-ish, inside the field
        static readonly Vector2 Outside = new(500, 300);  // well clear of the field

        // One frame's input: mouse position + left-button state + this frame's typed keys.
        // Typed keys go in BOTH KeysDown and KeysPressed (the pressed edge) so WasTyped fires,
        // matching how TextInputTests drives text entry.
        static InputState Frame(Vector2 pos, bool leftDown, IEnumerable<Key>? keys = null)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var k = new HashSet<Key>(keys ?? System.Array.Empty<Key>());
            return new InputState(
                k, k, new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        // Drive one frame through the manager and the field.
        static void Step(InputManager input, NumberField f, Vector2 pos, bool leftDown, IEnumerable<Key>? keys = null)
        {
            input.Update(Frame(pos, leftDown, keys));
            f.Update(input, 0f);
        }

        // Press at `at`, then release at `at` with no travel = a tap that enters typing mode.
        static void TapToEdit(InputManager input, NumberField f, Vector2 at)
        {
            Step(input, f, at, false);   // up (establishes position + prevPos)
            Step(input, f, at, true);    // press (sets the press origin)
            Step(input, f, at, false);   // release (the tap fires here)
        }

        [Fact]
        public void Scrub_DragChangesValueByDragScale()
        {
            var f = new NumberField(Field, 5f) { DragScale = 0.1f };
            var input = new InputManager();
            Step(input, f, Inside, false);                 // up
            Step(input, f, Inside, true);                  // press at x=160
            Step(input, f, new Vector2(200, 114), true);   // drag +40px in one frame
            Assert.Equal(9f, f.Value, 3);                  // 5 + 40 * 0.1
            Assert.True(f.WasChanged);
        }

        [Fact]
        public void Scrub_ContinuesOutsideBounds()
        {
            var f = new NumberField(Field, 5f) { DragScale = 0.1f };
            var input = new InputManager();
            Step(input, f, Inside, false);
            Step(input, f, Inside, true);
            Step(input, f, new Vector2(200, 114), true);   // inside: 5 -> 9
            float afterInside = f.Value;
            Step(input, f, new Vector2(400, 114), true);   // x=400 is outside the field, grab-gate holds
            Assert.True(f.Value > afterInside);
            Assert.Equal(29f, f.Value, 3);                 // 9 + 200 * 0.1
        }

        [Fact]
        public void Scrub_ClampsToMax()
        {
            var f = new NumberField(Field, 5f) { DragScale = 0.1f, Max = 10f };
            var input = new InputManager();
            Step(input, f, Inside, false);
            Step(input, f, Inside, true);
            Step(input, f, new Vector2(400, 114), true);   // huge drag, clamps at Max
            Assert.Equal(10f, f.Value, 3);
        }

        [Fact]
        public void PressStartingOutside_DoesNotScrub()
        {
            var f = new NumberField(Field, 5f) { DragScale = 0.1f };
            var input = new InputManager();
            Step(input, f, new Vector2(50, 114), false);   // up outside
            Step(input, f, new Vector2(50, 114), true);    // press began OUTSIDE the field
            Step(input, f, new Vector2(200, 114), true);   // dragged into the field, still down
            Assert.Equal(5f, f.Value, 3);                  // unchanged
            Assert.False(f.WasChanged);
        }

        [Fact]
        public void Tap_EntersEditing()
        {
            var f = new NumberField(Field, 5f);
            var input = new InputManager();
            TapToEdit(input, f, Inside);                   // press + release, no travel
            Assert.True(f.IsEditing);
            Assert.Equal(5f, f.Value, 3);                  // value untouched by entering edit
        }

        [Fact]
        public void TypeDigitsAndEnter_CommitsClamped()
        {
            var f = new NumberField(Field, 5f) { Max = 100f };
            var input = new InputManager();
            TapToEdit(input, f, Inside);
            Step(input, f, Inside, false, new[] { Key.D2 });   // first keystroke replaces the seeded value
            Step(input, f, Inside, false, new[] { Key.D5 });
            Step(input, f, Inside, false, new[] { Key.Enter });
            Assert.False(f.IsEditing);
            Assert.Equal(25f, f.Value, 3);                 // 25 <= Max 100, commits as typed
            Assert.True(f.WasChanged);
        }

        // Numpad entry drives the same edit path as the top-row keys: the FIRST keypad keystroke must also end the
        // select-all seed (replace, not append), and keypad dot/digits type through the numeric filter.
        [Fact]
        public void NumpadDigits_ReplaceSeededValueAndCommit()
        {
            var f = new NumberField(Field, 5f) { Decimals = 1 };
            var input = new InputManager();
            TapToEdit(input, f, Inside);                              // buffer seeded "5.0", select-all armed
            Step(input, f, Inside, false, new[] { Key.Keypad9 });     // replaces the seed, buffer "9"
            Step(input, f, Inside, false, new[] { Key.KeypadDecimal });
            Step(input, f, Inside, false, new[] { Key.Keypad5 });     // buffer "9.5"
            Step(input, f, Inside, false, new[] { Key.Enter });
            Assert.False(f.IsEditing);
            Assert.Equal(9.5f, f.Value, 3);                           // NOT 5.095 rounded (append would give that)
        }

        [Fact]
        public void Escape_CancelsEdit()
        {
            var f = new NumberField(Field, 5f) { Max = 100f };
            var input = new InputManager();
            TapToEdit(input, f, Inside);
            Step(input, f, Inside, false, new[] { Key.D2 });
            Step(input, f, Inside, false, new[] { Key.D5 });
            Step(input, f, Inside, false, new[] { Key.Escape });
            Assert.False(f.IsEditing);
            Assert.Equal(5f, f.Value, 3);                  // reverted to the pre-edit value
            Assert.False(f.WasChanged);                    // no change on the cancel frame
        }

        [Fact]
        public void TapOutside_CommitsEdit()
        {
            var f = new NumberField(Field, 5f) { Max = 100f };
            var input = new InputManager();
            TapToEdit(input, f, Inside);
            Step(input, f, Inside, false, new[] { Key.D7 });
            // A tap outside the field commits like Enter.
            Step(input, f, Outside, false);
            Step(input, f, Outside, true);
            Step(input, f, Outside, false);
            Assert.False(f.IsEditing);
            Assert.Equal(7f, f.Value, 3);
        }

        [Fact]
        public void UnparseableCommit_Reverts()
        {
            var f = new NumberField(Field, 5f) { Max = 100f };
            var input = new InputManager();
            TapToEdit(input, f, Inside);
            Step(input, f, Inside, false, new[] { Key.Minus });   // a lone "-" is not a number
            Step(input, f, Inside, false, new[] { Key.Enter });
            Assert.False(f.IsEditing);
            Assert.Equal(5f, f.Value, 3);                  // reverts to the pre-edit value
        }

        [Fact]
        public void Disabled_IgnoresEverything()
        {
            var f = new NumberField(Field, 5f) { Enabled = false, DragScale = 0.1f };
            var input = new InputManager();
            // Drag sequence.
            Step(input, f, Inside, false);
            Step(input, f, Inside, true);
            Step(input, f, new Vector2(200, 114), true);
            // Tap sequence.
            Step(input, f, Inside, false);
            Assert.Equal(5f, f.Value, 3);
            Assert.False(f.IsEditing);
        }

        // IsScrubbing mirrors the grab-gate: true from the press inside through every drag frame (even once the
        // cursor strays off the field), and false once the button releases.
        [Fact]
        public void NumberField_IsScrubbing_TracksGrabGate()
        {
            var f = new NumberField(Field, 5f) { DragScale = 0.1f };
            var input = new InputManager();

            Step(input, f, Inside, false);                 // idle, button up
            Assert.False(f.IsScrubbing);
            Step(input, f, Inside, true);                  // press inside: the grab-gate is held
            Assert.True(f.IsScrubbing);
            Step(input, f, new Vector2(200, 114), true);   // drag, still inside
            Assert.True(f.IsScrubbing);
            Step(input, f, new Vector2(400, 114), true);   // dragged OUTSIDE the field, grab-gate still holds
            Assert.True(f.IsScrubbing);
            Step(input, f, new Vector2(400, 114), false);  // release
            Assert.False(f.IsScrubbing);
        }

        // CancelEdit exits typing mode without committing the buffer, leaving Value at the pre-edit value.
        [Fact]
        public void NumberField_CancelEdit_RevertsAndCloses()
        {
            var f = new NumberField(Field, 5f) { Max = 100f };
            var input = new InputManager();
            TapToEdit(input, f, Inside);
            Assert.True(f.IsEditing);
            Step(input, f, Inside, false, new[] { Key.D9 });   // buffer becomes "9"; value not committed
            Assert.Equal(5f, f.Value, 3);

            f.CancelEdit();                                    // close the edit without committing

            Assert.False(f.IsEditing);
            Assert.Equal(5f, f.Value, 3);                      // reverted to the pre-edit value, never committed to 9
            Assert.False(f.WasChanged);                        // a cancel is not a change
        }
    }
}
