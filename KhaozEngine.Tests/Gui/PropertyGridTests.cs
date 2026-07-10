using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage of the descriptor-driven <see cref="PropertyGrid"/> and its row types: the pure-arithmetic
    /// row layout (label/editor split at <see cref="PropertyGrid.LabelFraction"/>, stacked with spacing), getter
    /// polling (external edits reflected next frame without change events), write-through on each row type, wheel
    /// scroll clamping, and the scrolled/clipped hit-test edge (a scrolled-away row does not act on input, so it
    /// never pollutes the block regions). Update only, no texture or font drawing.
    /// </summary>
    public class PropertyGridTests
    {
        static readonly Rect Area = new(0, 0, 300, 150);

        // One frame's input: pointer position + left-button + this frame's scroll + typed keys. Typed keys go in
        // BOTH KeysDown and KeysPressed (the pressed edge), matching how NumberFieldTests/TextInputTests drive entry.
        static InputState Frame(Vector2 pos, bool leftDown, float scroll = 0f, IEnumerable<Key>? keys = null)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            var k = new HashSet<Key>(keys ?? System.Array.Empty<Key>());
            return new InputState(
                k, k, new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, scroll, 960, 540);
        }

        // Drive one frame through the manager and the grid.
        static void Step(InputManager input, PropertyGrid grid, Vector2 pos, bool leftDown, float scroll = 0f,
            IEnumerable<Key>? keys = null)
        {
            input.Update(Frame(pos, leftDown, scroll, keys));
            grid.Update(input, 0f);
        }

        // A press-origin tap (press and release both at `at`), the way the pointer fires taps.
        static void Tap(InputManager input, PropertyGrid grid, Vector2 at)
        {
            Step(input, grid, at, false);   // up (establishes position + prevPos)
            Step(input, grid, at, true);    // press (sets the press origin)
            Step(input, grid, at, false);   // release (the tap fires here)
        }

        [Fact]
        public void Rows_StackWithSpacing()
        {
            float a = 1f, b = 2f, c = 3f;
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("A"), () => a, v => a = v));
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("B"), () => b, v => b = v));
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("C"), () => c, v => c = v));

            // Row 1 sits one row height (28) + one spacing (4) below the top.
            Assert.Equal(28f + 4f, grid.RowEditorBounds(1).Y, 3);
            // Every editor cell starts at Bounds.X + Bounds.Width * LabelFraction.
            Assert.Equal(300f * 0.45f, grid.RowEditorBounds(0).X, 3);
            Assert.Equal(300f * 0.45f, grid.RowEditorBounds(1).X, 3);
            Assert.Equal(300f * 0.45f, grid.RowEditorBounds(2).X, 3);
        }

        [Fact]
        public void FloatRow_ScrubWritesThroughSetter()
        {
            float health = 50f;
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("Health"), () => health, v => health = v,
                min: 0f, max: 100f));   // default dragScale 0.01

            var input = new InputManager();
            var inside = new Vector2(200, 14);            // inside row 0's editor cell (x>=135, y in 0..28)
            Step(input, grid, inside, false);             // up
            Step(input, grid, inside, true);              // press inside the cell
            Step(input, grid, new Vector2(240, 14), true);// drag +40px -> +0.4 at dragScale 0.01

            Assert.Equal(50.4f, health, 3);
            Assert.True(grid.WasChanged);
        }

        [Fact]
        public void FloatRow_ExternalChangeIsPolled()
        {
            float health = 50f;
            var row = new FloatRow(LocalizedText.Raw("Health"), () => health, v => health = v, min: 0f, max: 100f);
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(row);

            var input = new InputManager();
            health = 75f;                                 // changed externally (undo, multi-edit)
            Step(input, grid, new Vector2(400, 300), false);   // idle, pointer off the grid

            Assert.Equal(75f, row.Field.Value, 3);        // polled into the field
            Assert.False(grid.WasChanged);                // a poll is not a change
        }

        [Fact]
        public void BoolRow_TapFlipsBacking()
        {
            bool visible = false;
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new BoolRow(LocalizedText.Raw("Visible"), () => visible, v => visible = v));

            var input = new InputManager();
            Tap(input, grid, new Vector2(200, 14));       // tap in row 0's toggle cell

            Assert.True(visible);
            Assert.True(grid.WasChanged);
        }

        // TextInput edits its Text buffer live each focused frame (no separate commit event; TextChanged is a
        // per-frame flag), so TextRow's write-through is LIVE - the setter fires the frame the buffer changes.
        [Fact]
        public void TextRow_TypingWritesLiveThroughSetter()
        {
            string id = "inn";
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new TextRow(LocalizedText.Raw("Id"), () => id, v => id = v));

            var input = new InputManager();
            var inside = new Vector2(200, 14);
            Tap(input, grid, inside);                     // focus the field (polls "inn" in first)
            Step(input, grid, inside, false, keys: new[] { Key.A });   // type 'a'

            Assert.Equal("inna", id);                     // written through live
            Assert.True(grid.WasChanged);
        }

        [Fact]
        public void ReadOnlyRow_JustDisplays()
        {
            string coord = "1,2";
            var row = new ReadOnlyRow(LocalizedText.Raw("Pos"), () => coord);
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(row);

            var input = new InputManager();
            Step(input, grid, new Vector2(400, 300), false);   // idle
            Assert.Equal("1,2", row.Display);

            coord = "3,4";                                 // backing changes
            Step(input, grid, new Vector2(400, 300), false);
            Assert.Equal("3,4", row.Display);              // polled display reflects it

            // A tap has no effect: a read-only row never reports a change.
            Tap(input, grid, new Vector2(200, 14));
            Assert.False(grid.WasChanged);
        }

        [Fact]
        public void Wheel_ScrollsAndClamps()
        {
            var grid = new PropertyGrid(Area);
            for (int i = 0; i < 10; i++)
            {
                int captured = i;
                grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "row" + captured));
            }
            // content = 10 * (28 + 4) = 320 px in a 150 px view -> max scroll 170.
            var input = new InputManager();
            var inside = new Vector2(150, 75);

            Step(input, grid, inside, false, scroll: -1f);     // wheel down one notch: 28 avg row * 3 rows/notch = 84
            Assert.Equal(84f, grid.ScrollOffset, 3);

            Step(input, grid, inside, false, scroll: -100f);   // overshoot down -> clamp to max
            Assert.Equal(170f, grid.ScrollOffset, 3);

            Step(input, grid, inside, false, scroll: 100f);    // overshoot up -> clamp to top
            Assert.Equal(0f, grid.ScrollOffset, 3);
        }

        [Fact]
        public void ScrolledAwayRow_DoesNotHitTest()
        {
            bool visible = false;
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new BoolRow(LocalizedText.Raw("Visible"), () => visible, v => visible = v));
            for (int i = 1; i < 10; i++)
                grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));

            grid.ScrollOffset = 100f;                     // scroll row 0 well above the view (its top -> -100)

            var input = new InputManager();
            // Tap where row 0's toggle cell WOULD sit unscrolled (top of the grid). A scroll-aware hit-test misses
            // row 0 (its rect moved off-screen) and the grid skips its Update entirely, so the backing stays put.
            Tap(input, grid, new Vector2(200, 14));

            Assert.False(visible);
        }

        [Fact]
        public void Disabled_IgnoresInput()
        {
            float health = 50f;
            bool visible = false;
            var grid = new PropertyGrid(Area) { Enabled = false };
            grid.Rows.Add(new BoolRow(LocalizedText.Raw("Visible"), () => visible, v => visible = v));
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("Health"), () => health, v => health = v, min: 0f, max: 100f));

            var input = new InputManager();
            Tap(input, grid, new Vector2(200, 14));       // would flip the toggle if enabled

            // Drag on the float row (row 1, y in 32..60).
            Step(input, grid, new Vector2(200, 46), false);
            Step(input, grid, new Vector2(200, 46), true);
            Step(input, grid, new Vector2(240, 46), true);

            Assert.False(visible);
            Assert.Equal(50f, health, 3);
            Assert.False(grid.WasChanged);
        }

        // The dual-focus double-typing bug from the B1 review, pinned. A focused TextRow that scroll-culls used to
        // keep its focus behind the cull (the grid skipped its Update, so it never saw the tap that would defocus it),
        // and a second focus elsewhere left BOTH fields focused, double-typing through both setters. The grid now
        // deactivates any row it culls that ran last frame, so the culled field is unfocused and cannot double-write.
        [Fact]
        public void PropertyGrid_CulledFocusedTextRow_IsDeactivated()
        {
            string id = "a";
            var grid = new PropertyGrid(Area);
            var row = new TextRow(LocalizedText.Raw("Id"), () => id, v => id = v);
            grid.Rows.Add(row);
            for (int i = 1; i < 10; i++)                       // fillers so row 0 can scroll fully out of view
                grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));

            var input = new InputManager();
            var inside = new Vector2(200, 14);                 // row 0's editor cell
            Tap(input, grid, inside);                          // focus the text field
            Assert.True(row.Input.IsFocused);                  // precondition: it took focus

            grid.ScrollOffset = 100f;                          // push row 0 fully above the view
            Step(input, grid, new Vector2(400, 300), false);   // one Update: row 0 is culled and deactivated

            Assert.False(row.Input.IsFocused);                 // the grid unfocused it on the cull

            // Typing frames afterward do NOT write through its setter: there is no phantom focused field behind the
            // cull. Bring it back into view and type, and the value is untouched (no double-typing).
            grid.ScrollOffset = 0f;
            Step(input, grid, inside, false, keys: new[] { Key.B });
            Assert.Equal("a", id);
        }

        // A FloatRow that scroll-culls mid-edit has its NumberField edit CANCELLED (revert + close), not committed.
        [Fact]
        public void PropertyGrid_CulledEditingFloatRow_CancelsEdit()
        {
            float val = 5f;
            var grid = new PropertyGrid(Area);
            var row = new FloatRow(LocalizedText.Raw("V"), () => val, v => val = v, min: 0f, max: 100f);
            grid.Rows.Add(row);
            for (int i = 1; i < 10; i++)
                grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));

            var input = new InputManager();
            var inside = new Vector2(200, 14);
            Tap(input, grid, inside);                          // tap enters typing mode on the NumberField
            Assert.True(row.Field.IsEditing);                  // precondition: it is editing
            Step(input, grid, inside, false, keys: new[] { Key.D9 });   // buffer becomes "9"; value not committed yet
            Assert.Equal(5f, val, 3);                          // still the pre-edit value

            grid.ScrollOffset = 100f;                          // push the row out of view
            Step(input, grid, new Vector2(400, 300), false);   // one Update: culled -> deactivated -> edit cancelled

            Assert.False(row.Field.IsEditing);                 // the edit was closed
            Assert.Equal(5f, val, 3);                          // reverted, NOT committed to 9
        }

        // ---- ChoiceRow: a Dropdown over string options with get/set delegates on the selected option string. ----

        // Row 0's editor cell is x 135..300, y 0..28 (LabelFraction 0.45 of 300). The dropdown trigger fills the
        // cell, so with two options the open list rows sit at y 28..56 (option 0) and y 56..84 (option 1).
        static readonly Vector2 ChoiceTrigger = new(200, 14);
        static readonly Vector2 ChoiceOption1 = new(200, 70);

        [Fact]
        public void ChoiceRow_SelectsAndWritesThrough()
        {
            string kind = "disc";
            var grid = new PropertyGrid(Area);
            var row = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => kind, v => kind = v);
            grid.Rows.Add(row);

            var input = new InputManager();
            Assert.Equal("disc", row.Selected);           // seeded from the getter

            Tap(input, grid, ChoiceTrigger);              // open the list
            Assert.True(row.Dropdown.IsOpen);

            Tap(input, grid, ChoiceOption1);              // pick "rect"
            Assert.Equal("rect", kind);                   // written through the setter
            Assert.Equal("rect", row.Selected);
            Assert.True(grid.WasChanged);                 // the pick frame reports the change
            Assert.False(row.Dropdown.IsOpen);            // picking closes the list
        }

        [Fact]
        public void ChoiceRow_PollsExternalChange()
        {
            string kind = "disc";
            var grid = new PropertyGrid(Area);
            var row = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => kind, v => kind = v);
            grid.Rows.Add(row);

            var input = new InputManager();
            kind = "rect";                                     // changed externally (undo, multi-edit)
            Step(input, grid, new Vector2(400, 300), false);   // idle frame

            Assert.Equal("rect", row.Selected);                // polled into the dropdown
            Assert.False(grid.WasChanged);                     // a poll is not a change

            // While the list is OPEN the poll is skipped, so an in-progress pick is never stomped.
            Tap(input, grid, ChoiceTrigger);                   // open
            kind = "disc";                                     // external change lands mid-pick
            Step(input, grid, ChoiceTrigger, false);           // idle frame with the list open
            Assert.Equal("rect", row.Selected);                // NOT polled while open

            Tap(input, grid, new Vector2(400, 300));           // release outside dismisses without selecting
            Step(input, grid, new Vector2(400, 300), false);   // next closed frame polls again
            Assert.Equal("disc", row.Selected);
        }

        // Re-picking the already selected option closes the list without firing the setter.
        [Fact]
        public void ChoiceRow_RepickIsNotAChange()
        {
            string kind = "disc";
            int sets = 0;
            var grid = new PropertyGrid(Area);
            grid.Rows.Add(new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" },
                () => kind, v => { kind = v; sets++; }));

            var input = new InputManager();
            Tap(input, grid, ChoiceTrigger);                   // open
            Tap(input, grid, new Vector2(200, 42));            // option 0 = "disc", already selected
            Assert.Equal(0, sets);
            Assert.False(grid.WasChanged);
        }

        // The open option list must draw ABOVE the rows below the selector, not get overpainted by them. The grid
        // draws in two passes (its DrawPlan): every visible row's label+editor first (the Row pass), then every
        // visible row again in a late overlay pass (the Overlay pass) where an open Dropdown paints its list. So the
        // ChoiceRow's overlay is emitted after EVERY sibling row's editor, including the rows beneath it. Pins the
        // ordering seam headlessly (the pixel compositing itself is GPU order, not asserted here).
        [Fact]
        public void ChoiceRow_OpenList_DrawsAfterSiblingRows()
        {
            string kind = "disc";
            var grid = new PropertyGrid(Area);
            var choice = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => kind, v => kind = v);
            grid.Rows.Add(choice);                                              // row 0: the selector
            grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("A"), () => "-"));  // row 1: a sibling below it
            grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("B"), () => "-"));  // row 2: a sibling below it

            var input = new InputManager();
            Tap(input, grid, ChoiceTrigger);                                   // open the list
            Assert.True(choice.Dropdown.IsOpen);

            var plan = new List<(int Row, PropertyGrid.DrawPass Pass)>(grid.DrawPlan());

            // Every Row-pass entry precedes every Overlay-pass entry: the whole grid of rows draws before any open
            // list, so no sibling row can overpaint the list.
            int lastRowPass = plan.FindLastIndex(e => e.Pass == PropertyGrid.DrawPass.Row);
            int firstOverlayPass = plan.FindIndex(e => e.Pass == PropertyGrid.DrawPass.Overlay);
            Assert.True(firstOverlayPass > lastRowPass);

            // Concretely, the selector's own overlay (row 0) comes after the Row-pass entries of the rows beneath it.
            int choiceOverlay = plan.FindIndex(e => e.Row == 0 && e.Pass == PropertyGrid.DrawPass.Overlay);
            int siblingBelowRow = plan.FindLastIndex(e => e.Row == 2 && e.Pass == PropertyGrid.DrawPass.Row);
            Assert.True(choiceOverlay > siblingBelowRow);
        }

        // A ChoiceRow that scroll-culls with its list open has the dropdown CLOSED by the grid's Deactivate hook,
        // so an off-view open list cannot keep swallowing taps (same hygiene as the culled TextRow focus).
        [Fact]
        public void ChoiceRow_CulledOpenDropdown_Closes()
        {
            string kind = "disc";
            var grid = new PropertyGrid(Area);
            var row = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => kind, v => kind = v);
            grid.Rows.Add(row);
            for (int i = 1; i < 10; i++)
                grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));

            var input = new InputManager();
            Tap(input, grid, ChoiceTrigger);                   // open the list
            Assert.True(row.Dropdown.IsOpen);

            grid.ScrollOffset = 100f;                          // push row 0 fully above the view
            Step(input, grid, new Vector2(400, 300), false);   // one Update: culled -> deactivated

            Assert.False(row.Dropdown.IsOpen);                 // the grid closed it on the cull
        }

        // ---- Cell text ellipsis: the pure width-based truncation helper the grid text draws go through. ----

        [Fact]
        public void GridCellText_TruncatesWithEllipsis()
        {
            // A deterministic 10-units-per-char measure keeps the math exact and font-free.
            static float Width(string s) => s.Length * 10f;

            // Fits: returned unchanged (exact fit included).
            Assert.Equal("abc", GuiDraw.TruncateWithEllipsis("abc", 100f, Width));
            Assert.Equal("abc", GuiDraw.TruncateWithEllipsis("abc", 30f, Width));

            // Too long: the longest prefix that fits WITH the trailing dots, then "..." (three ASCII dots,
            // never the single-glyph ellipsis, which may not be baked into a font atlas).
            Assert.Equal("cen...", GuiDraw.TruncateWithEllipsis("center (0, 18) radius 12", 60f, Width));

            // One char narrower cuts one more prefix char.
            Assert.Equal("ce...", GuiDraw.TruncateWithEllipsis("center (0, 18) radius 12", 59f, Width));

            // Floor: when not even the dots fit, the dots are still returned (the caller's scissor clips).
            Assert.Equal("...", GuiDraw.TruncateWithEllipsis("abcdef", 20f, Width));

            // Empty text passes through.
            Assert.Equal("", GuiDraw.TruncateWithEllipsis("", 50f, Width));
        }

        // The wheel rate is aligned across the two editor widgets: one notch scrolls each by WheelRowsPerNotch (3)
        // rows of its own row height, so they feel identical side by side. TreeView RowHeight 24 -> 72 px; a
        // PropertyGrid of default 28-tall rows -> 84 px. Both use the shared default of 3 rows per notch.
        [Fact]
        public void WheelRates_DefaultAligned()
        {
            var tree = new TreeView(new Rect(0, 0, 200, 120));
            for (int i = 0; i < 20; i++) tree.Roots.Add(new TreeNode(LocalizedText.Raw("R")));
            var treeInput = new InputManager();
            treeInput.Update(Frame(new Vector2(100, 60), false, scroll: -1f));   // one notch down over the tree
            tree.Update(treeInput);

            var grid = new PropertyGrid(new Rect(0, 0, 300, 120));
            for (int i = 0; i < 20; i++) grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));
            var gridInput = new InputManager();
            Step(gridInput, grid, new Vector2(150, 60), false, scroll: -1f);     // one notch down over the grid

            Assert.Equal(3f, tree.WheelRowsPerNotch, 3);        // shared default feel
            Assert.Equal(3f, grid.WheelRowsPerNotch, 3);
            Assert.Equal(3f * tree.RowHeight, tree.ScrollOffset, 3);   // 3 * 24 = 72
            Assert.Equal(3f * 28f, grid.ScrollOffset, 3);             // 3 * 28 = 84
        }
    }
}
