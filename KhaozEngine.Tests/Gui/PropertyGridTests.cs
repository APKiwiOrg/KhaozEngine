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

        // The aggregate query a host (MapEditorScene's shortcut handler) polls to decide whether a keyboard chord
        // belongs to a focused inspector field instead of a global hotkey. True while ANY row owns a live edit
        // gesture: a FloatRow typing or scrubbing, a TextRow focused, or a ChoiceRow's list open. Each row type is
        // exercised independently so the aggregate is proven to OR across all three, not just report one row.
        [Fact]
        public void PropertyGrid_HasActiveEditor_TracksFloatTextChoiceRows()
        {
            float val = 5f;
            string id = "a";
            string kind = "disc";
            var grid = new PropertyGrid(Area);
            var floatRow = new FloatRow(LocalizedText.Raw("V"), () => val, v => val = v, min: 0f, max: 100f);
            var textRow = new TextRow(LocalizedText.Raw("Id"), () => id, v => id = v);
            var choiceRow = new ChoiceRow(LocalizedText.Raw("Kind"), new[] { "disc", "rect" }, () => kind, v => kind = v);
            grid.Rows.Add(floatRow);   // row 0: y 0..28
            grid.Rows.Add(textRow);    // row 1: y 32..60
            grid.Rows.Add(choiceRow);  // row 2: y 64..92

            Assert.False(grid.HasActiveEditor);   // nothing focused/editing/open yet

            var input = new InputManager();
            Tap(input, grid, new Vector2(200, 14));            // row 0: tap enters typing mode
            Assert.True(floatRow.Field.IsEditing);
            Assert.True(grid.HasActiveEditor);

            floatRow.Field.CancelEdit();
            Assert.False(grid.HasActiveEditor);                // back to none active

            Tap(input, grid, new Vector2(200, 46));            // row 1: tap focuses the text field
            Assert.True(textRow.Input.IsFocused);
            Assert.True(grid.HasActiveEditor);

            textRow.Input.Unfocus();
            Assert.False(grid.HasActiveEditor);

            Tap(input, grid, new Vector2(200, 78));            // row 2: tap opens the dropdown
            Assert.True(choiceRow.Dropdown.IsOpen);
            Assert.True(grid.HasActiveEditor);

            choiceRow.Dropdown.Close();
            Assert.False(grid.HasActiveEditor);
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

        // The wheel step is continuous (ScrollDelta * WheelSpeed), not rounded to an integer notch count: a
        // fractional delta (a trackpad, or a partial accumulated notch) moves the matching fraction of a whole
        // notch's distance, unlike the old GetScrollIn((int)MathF.Round(...)) path.
        [Fact]
        public void Wheel_ScrollsContinuously_NoNotchRounding()
        {
            var grid = new PropertyGrid(Area);
            for (int i = 0; i < 10; i++) grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));

            var input = new InputManager();
            Step(input, grid, new Vector2(150, 75), false, scroll: -0.5f);   // half a wheel unit down
            Assert.Equal(84f * 0.5f, grid.ScrollOffset, 3);   // half of the whole-unit 84 (28 avg row * 3/notch), not 0 or 84

            Step(input, grid, new Vector2(150, 75), false, scroll: -0.25f);   // another quarter unit
            Assert.Equal(84f * 0.75f, grid.ScrollOffset, 3);
        }

        // WheelSpeed is exposed under the same name/idiom as ScrollablePanel.WheelSpeed, computed from
        // AverageRowHeight * WheelRowsPerNotch so a grid with taller or shorter rows still feels aligned with a
        // TreeView using its own RowHeight * WheelRowsPerNotch.
        [Fact]
        public void WheelSpeed_MatchesAverageRowHeightTimesWheelRowsPerNotch()
        {
            var grid = new PropertyGrid(Area);
            for (int i = 0; i < 4; i++) grid.Rows.Add(new ReadOnlyRow(LocalizedText.Raw("R"), () => "-"));
            Assert.Equal(28f * 3f, grid.WheelSpeed, 3);   // default row height 28, default WheelRowsPerNotch 3

            grid.WheelRowsPerNotch = 5f;
            Assert.Equal(28f * 5f, grid.WheelSpeed, 3);
        }

        // ---- Partial-row input clamp: a row straddling Bounds' bottom edge is still visually clipped by the
        // grid's scissor at Draw time, so its INPUT reach must match - a tap in the sliver below Bounds must not
        // register, even though the row's own (unclamped) editor cell would otherwise cover that point. ----

        [Fact]
        public void PartiallyVisibleRow_DoesNotReactBelowBounds()
        {
            float val = 5f;
            // Bounds shorter (20px) than one row (28px): row 0's cell (Y 0..28) straddles Bounds.Bottom by 8px.
            var grid = new PropertyGrid(new Rect(0, 0, 300, 20));
            var row = new FloatRow(LocalizedText.Raw("V"), () => val, v => val = v, min: 0f, max: 100f);
            grid.Rows.Add(row);

            var input = new InputManager();
            // Inside the row's UNCLAMPED cell (X in the editor column, Y 0..28) but below Bounds.Bottom (20) -
            // exactly the sliver the scissor already clips at Draw time.
            var inSliver = new Vector2(200f, 25f);
            Tap(input, grid, inSliver);

            Assert.False(row.Field.IsEditing);   // the clamped cell keeps the sliver outside the field's hit-test
            Assert.False(grid.WasChanged);
        }

        // ---- FloatRow.GestureEnded: a direct pass-through of NumberField.GestureEnded, the hook MapEditorScene
        // wires to EditorDocument.SealGesture so scrubbing two different rows seals two separate undo steps. ----

        [Fact]
        public void FloatRow_GestureEnded_MirrorsFieldGestureEnded()
        {
            float val = 5f;
            var row = new FloatRow(LocalizedText.Raw("V"), () => val, v => val = v, min: 0f, max: 100f, dragScale: 0.1f);
            int fired = 0;
            row.GestureEnded += () => fired++;

            var cell = new Rect(0f, 0f, 200f, 28f);
            var input = new InputManager();
            var inside = new Vector2(100f, 14f);
            input.Update(Frame(inside, false)); row.Update(cell, input, 0f);
            input.Update(Frame(inside, true)); row.Update(cell, input, 0f);                       // press
            input.Update(Frame(new Vector2(140f, 14f), true)); row.Update(cell, input, 0f);       // real scrub, still held
            Assert.Equal(0, fired);

            input.Update(Frame(new Vector2(140f, 14f), false)); row.Update(cell, input, 0f);      // release: seals
            Assert.Equal(1, fired);
        }

        // ---- HeaderRow: a label-only row spanning the grid's full width, no editor cell. ----

        [Fact]
        public void HeaderRow_SpansFullWidth_NoEditorCell()
        {
            var grid = new PropertyGrid(Area);   // Area (0,0,300,150), LabelFraction default 0.45
            var header = new HeaderRow(LocalizedText.Raw("Group"));
            grid.Rows.Add(header);                                                        // row 0
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("A"), () => 0f, _ => { }));       // row 1

            Assert.True(header.SpansFullWidth);
            Assert.Equal(24f, header.Height, 3);

            // No distinct editor cell: the "editor" band for a spanning row is the FULL row width, not the
            // (1 - LabelFraction) slice a normal row's editor cell gets.
            Rect cell = grid.RowEditorBounds(0);
            Assert.Equal(0f, cell.X, 3);
            Assert.Equal(300f, cell.Width, 3);

            // Its label bounds coincide with that same full band: there is no separate label column to split.
            Rect label = grid.RowLabelBounds(0);
            Assert.Equal(cell.X, label.X, 3);
            Assert.Equal(cell.Width, label.Width, 3);

            // A sibling non-spanning row right below it still gets the normal label/editor split.
            Rect siblingCell = grid.RowEditorBounds(1);
            Assert.Equal(300f * 0.45f, siblingCell.X, 3);

            // A header owns no interactive widget: a tap on its band never reports a change.
            var input = new InputManager();
            Tap(input, grid, new Vector2(150, 12));   // inside the header's band (row 0, y 0..24)
            Assert.False(grid.WasChanged);

            // Never has an active editor. Deactivate is the inherited no-op (does not throw).
            Assert.False(header.HasActiveEditor);
            header.Deactivate();
        }

        // ---- PropertyGrid.HoveredRow: tracks the pointer across a row's FULL band (label + editor together). ----

        [Fact]
        public void HoveredRow_TracksPointer_NullOutside()
        {
            var grid = new PropertyGrid(Area);   // Area (0,0,300,150)
            var rowA = new FloatRow(LocalizedText.Raw("A"), () => 0f, _ => { });   // row 0: y 0..28
            var rowB = new BoolRow(LocalizedText.Raw("B"), () => false, _ => { }); // row 1: y 32..60
            grid.Rows.Add(rowA);
            grid.Rows.Add(rowB);

            Assert.Null(grid.HoveredRow);   // no frame run yet

            var input = new InputManager();

            // Over row 0's LABEL column (left of the label/editor split) still counts: the hover band is the
            // row's full width, not just RowEditorBounds.
            Step(input, grid, new Vector2(10, 14), false);
            Assert.Same(rowA, grid.HoveredRow);

            // Over row 0's editor cell too.
            Step(input, grid, new Vector2(200, 14), false);
            Assert.Same(rowA, grid.HoveredRow);

            // Over row 1.
            Step(input, grid, new Vector2(200, 46), false);
            Assert.Same(rowB, grid.HoveredRow);

            // In the inter-row gap (y 28..32, the 4px RowSpacing): no row's band covers it.
            Step(input, grid, new Vector2(200, 30), false);
            Assert.Null(grid.HoveredRow);

            // Outside the grid bounds entirely.
            Step(input, grid, new Vector2(400, 300), false);
            Assert.Null(grid.HoveredRow);
        }

        // ---- PropertyGrid.RowLabelBounds(int): the public label-cell accessor. ----

        [Fact]
        public void RowLabelBounds_MatchesLabelFraction()
        {
            var grid = new PropertyGrid(Area);   // Area width 300, LabelFraction default 0.45
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("A"), () => 0f, _ => { }));
            grid.Rows.Add(new FloatRow(LocalizedText.Raw("B"), () => 0f, _ => { }));

            Rect label0 = grid.RowLabelBounds(0);
            Rect editor0 = grid.RowEditorBounds(0);
            Assert.Equal(0f, label0.X, 3);
            Assert.Equal(300f * 0.45f, label0.Width, 3);
            Assert.Equal(editor0.Y, label0.Y, 3);
            Assert.Equal(editor0.Height, label0.Height, 3);
            // The label ends exactly where the editor cell begins: no gap, no overlap.
            Assert.Equal(editor0.X, label0.X + label0.Width, 3);

            // The same relationship holds for a lower row.
            Rect label1 = grid.RowLabelBounds(1);
            Rect editor1 = grid.RowEditorBounds(1);
            Assert.Equal(editor1.Y, label1.Y, 3);
            Assert.Equal(300f * 0.45f, label1.Width, 3);

            // A non-default LabelFraction still holds the relationship.
            grid.LabelFraction = 0.3f;
            Rect label2 = grid.RowLabelBounds(0);
            Assert.Equal(300f * 0.3f, label2.Width, 3);
        }

        // ---- PropertyGrid.EditorStyle: pushed into every row's inner widget. ----

        [Fact]
        public void EditorStyle_AppliesToRowWidgets()
        {
            var grid = new PropertyGrid(Area);
            var floatRow = new FloatRow(LocalizedText.Raw("F"), () => 0f, _ => { });
            var boolRow = new BoolRow(LocalizedText.Raw("B"), () => false, _ => { });
            var textRow = new TextRow(LocalizedText.Raw("T"), () => "", _ => { });
            var choiceRow = new ChoiceRow(LocalizedText.Raw("C"), new[] { "a", "b" }, () => "a", _ => { });
            var readOnlyRow = new ReadOnlyRow(LocalizedText.Raw("R"), () => "-");
            var header = new HeaderRow(LocalizedText.Raw("H"));
            grid.Rows.Add(floatRow);
            grid.Rows.Add(boolRow);
            grid.Rows.Add(textRow);
            grid.Rows.Add(choiceRow);
            grid.Rows.Add(readOnlyRow);   // no styled widget: must not throw when EditorStyle is applied
            grid.Rows.Add(header);        // same

            // Default: every row's widget already carries GuiStyle.Default (its own field default).
            Assert.Equal(GuiStyle.Default.CornerRadius, floatRow.Field.Style.CornerRadius, 3);

            var input = new InputManager();
            grid.EditorStyle = GuiStyle.Modern;   // set AFTER the rows are added

            Assert.Equal(GuiStyle.Modern.CornerRadius, floatRow.Field.Style.CornerRadius, 3);
            Assert.Equal(GuiStyle.Modern.CornerRadius, boolRow.Toggle.Style.CornerRadius, 3);
            Assert.Equal(GuiStyle.Modern.CornerRadius, textRow.Input.Style.CornerRadius, 3);
            Assert.Equal(GuiStyle.Modern.CornerRadius, choiceRow.Dropdown.Style.CornerRadius, 3);

            // A row added AFTER EditorStyle is set also picks it up, on its next Update, without a fresh assignment.
            var laterRow = new FloatRow(LocalizedText.Raw("L"), () => 0f, _ => { });
            grid.Rows.Add(laterRow);
            Assert.Equal(GuiStyle.Default.CornerRadius, laterRow.Field.Style.CornerRadius, 3);   // fresh widget default
            Step(input, grid, new Vector2(400, 300), false);
            Assert.Equal(GuiStyle.Modern.CornerRadius, laterRow.Field.Style.CornerRadius, 3);
        }
    }
}
