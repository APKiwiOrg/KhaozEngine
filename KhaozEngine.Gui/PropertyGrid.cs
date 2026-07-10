using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// One labeled row in a <see cref="PropertyGrid"/>: a <see cref="LocalizedText"/> label on the left, a typed
    /// editor on the right. Rows poll their getter every <see cref="Update"/> so external changes (undo,
    /// multi-source edits) stay in sync without change events. Explicit descriptors, no reflection.
    /// </summary>
    public abstract class PropertyRow
    {
        /// <summary>Build a row with a resolved-at-draw label. The default <see cref="Height"/> is 28.</summary>
        protected PropertyRow(LocalizedText label)
        {
            Label = label;
            Height = 28f;
        }

        /// <summary>The (lazily resolved) row label. Re-resolves on every draw, so a locale switch takes effect next frame.</summary>
        public LocalizedText Label { get; }

        /// <summary>Row height in pixels. Default 28.</summary>
        public float Height { get; protected set; }

        /// <summary>
        /// Sync the getter, run the child widget over <paramref name="editorRect"/> (the right-hand cell this frame),
        /// and write any change back through the setter. Returns true when the row changed the bound value this frame.
        /// </summary>
        public abstract bool Update(Rect editorRect, InputManager input, float dt);

        /// <summary>Draw the child editor into <paramref name="editorRect"/>. <paramref name="white"/> is a 1x1 white texture.</summary>
        public abstract void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect);

        /// <summary>
        /// Grid hook: the row ran last frame but is culled this frame (scrolled out of view), so the grid is tearing
        /// it down. Close any live interaction the row owns (a focus, an open edit) so a widget behind the cull can
        /// no longer consume input the grid never routes to it. No-op by default.
        /// </summary>
        public virtual void Deactivate() { }

        /// <summary>Grid hook: push the grid's fade into this row's child widget before it draws. No-op by default.</summary>
        internal virtual void ApplyOpacity(float opacity) { }
    }

    /// <summary>Float property backed by get/set delegates, edited with a <see cref="NumberField"/>.</summary>
    public sealed class FloatRow : PropertyRow
    {
        readonly Func<float> _get;
        readonly Action<float> _set;

        /// <summary>The numeric editor, exposed for style/inspection. Its bounds are driven by the grid each frame.</summary>
        public NumberField Field { get; }

        /// <summary>Build a float row. <paramref name="min"/>/<paramref name="max"/>/<paramref name="dragScale"/>/
        /// <paramref name="decimals"/> configure the <see cref="Field"/>.</summary>
        public FloatRow(LocalizedText label, Func<float> get, Action<float> set,
            float min = float.MinValue, float max = float.MaxValue, float dragScale = 0.01f, int decimals = 2)
            : base(label)
        {
            _get = get;
            _set = set;
            Field = new NumberField(default, get())
            {
                Min = min,
                Max = max,
                DragScale = dragScale,
                Decimals = decimals,
            };
        }

        /// <inheritdoc/>
        public override bool Update(Rect editorRect, InputManager input, float dt)
        {
            Field.Bounds = editorRect;
            // Poll the external value in, unless the user is actively editing or scrubbing, so a live gesture is never
            // stomped. The field owns both flags now: IsEditing while typing, IsScrubbing while the grab-gate drag is
            // held. Reading them (rather than re-deriving the press-origin rule) keeps the guard in one place.
            bool interacting = Field.IsScrubbing || Field.IsEditing;
            if (!interacting) Field.Value = _get();

            bool changed = Field.Update(input, dt);
            if (changed) _set(Field.Value);
            return changed;
        }

        /// <inheritdoc/>
        public override void Deactivate() => Field.CancelEdit();

        /// <inheritdoc/>
        public override void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Field.Bounds = editorRect;
            Field.Draw(batch, white, font);
        }

        internal override void ApplyOpacity(float opacity) => Field.Opacity = opacity;
    }

    /// <summary>Bool property backed by get/set delegates, edited with a <see cref="Gui.Toggle"/>.</summary>
    public sealed class BoolRow : PropertyRow
    {
        readonly Func<bool> _get;
        readonly Action<bool> _set;

        /// <summary>The switch, exposed for style/inspection. Its bounds are driven by the grid each frame.</summary>
        public Toggle Toggle { get; }

        /// <summary>Build a bool row over the given get/set delegates.</summary>
        public BoolRow(LocalizedText label, Func<bool> get, Action<bool> set) : base(label)
        {
            _get = get;
            _set = set;
            Toggle = new Toggle(default, get());
        }

        /// <inheritdoc/>
        public override bool Update(Rect editorRect, InputManager input, float dt)
        {
            Toggle.Bounds = editorRect;
            // A toggle flips instantly on a tap (no drag/edit state to protect), so polling every frame before Update
            // is safe: the flip happens inside Update, after the poll, and writes back the same frame.
            Toggle.IsOn = _get();

            bool changed = Toggle.Update(input.Pointer);
            if (changed) _set(Toggle.IsOn);
            return changed;
        }

        /// <inheritdoc/>
        public override void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Toggle.Bounds = editorRect;
            Toggle.Draw(batch, white);
        }

        internal override void ApplyOpacity(float opacity) => Toggle.Opacity = opacity;
    }

    /// <summary>
    /// Text property backed by get/set delegates, edited with a <see cref="TextInput"/>. The bound value is a raw
    /// data string (ids, names), not player-facing copy, so the sink is the string delegates.
    /// </summary>
    public sealed class TextRow : PropertyRow
    {
        readonly Func<string> _get;
        readonly Action<string> _set;

        /// <summary>The text field, exposed for style/inspection. Its bounds are driven by the grid each frame.</summary>
        public TextInput Input { get; }

        /// <summary>Build a text row over the given get/set delegates, capped at <paramref name="maxLength"/>.</summary>
        public TextRow(LocalizedText label, Func<string> get, Action<string> set, int maxLength = 64) : base(label)
        {
            _get = get;
            _set = set;
            Input = new TextInput(default) { MaxLength = maxLength };
        }

        /// <inheritdoc/>
        public override bool Update(Rect editorRect, InputManager input, float dt)
        {
            Input.Bounds = editorRect;
            // Poll the external value in only while unfocused, so the user's in-progress typing is never overwritten.
            if (!Input.IsFocused)
            {
                string external = _get();
                if (Input.Text != external) Input.SetText(external);
            }

            Input.Update(input.Pointer, input.State, dt);

            // TextInput edits its buffer live (no separate commit event; TextChanged is a per-frame flag), so
            // write-through is live: any frame the buffer became something new, push it to the setter.
            if (Input.TextChanged && Input.Text != _get())
            {
                _set(Input.Text);
                return true;
            }
            return false;
        }

        /// <inheritdoc/>
        public override void Deactivate() => Input.Unfocus();

        /// <inheritdoc/>
        public override void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Input.Bounds = editorRect;
            Input.Font ??= font;
            Input.Draw(batch, white);
        }

        internal override void ApplyOpacity(float opacity) => Input.Opacity = opacity;
    }

    /// <summary>Read-only display row: a label plus a polled value string (coordinates, counts). Ignores input.</summary>
    public sealed class ReadOnlyRow : PropertyRow
    {
        readonly Func<string> _getDisplay;
        float _opacity = 1f;

        /// <summary>Colour of the displayed value, captured from the ambient theme at construction.</summary>
        public Vector4 TextColor = GuiTheme.Default.TextMuted;

        /// <summary>The value string polled on the last <see cref="Update"/>. Exposed for hosts and tests to read.</summary>
        public string Display { get; private set; } = "";

        /// <summary>Build a read-only row over a display getter.</summary>
        public ReadOnlyRow(LocalizedText label, Func<string> getDisplay) : base(label)
        {
            _getDisplay = getDisplay;
        }

        /// <inheritdoc/>
        public override bool Update(Rect editorRect, InputManager input, float dt)
        {
            Display = _getDisplay();
            return false;   // display only, never a change
        }

        /// <inheritdoc/>
        public override void Draw(SpriteBatch batch, Texture2D white, SpriteFont font, Rect editorRect)
        {
            Vector2 measured = font.Measure(Display);
            Vector2 pos = GuiDraw.AlignedTextPos(editorRect, measured, font.LineHeight, GuiAlign.Left, 1f, LabelPad);
            batch.DrawString(font, Display, new Vector2(MathF.Floor(pos.X), MathF.Floor(pos.Y)),
                (Color)GuiDraw.WithOpacity(TextColor, _opacity));
        }

        internal override void ApplyOpacity(float opacity) => _opacity = opacity;

        // Left pad of the value text inside the editor cell.
        const float LabelPad = 6f;
    }

    /// <summary>
    /// A vertical stack of <see cref="PropertyRow"/>s split label/editor at <see cref="LabelFraction"/>, scrolling
    /// like a <see cref="ScrollablePanel"/> (wheel + scissor clip), with rows laid out by pure arithmetic. The editor
    /// inspector panel primitive: it owns layout, scroll, and the clip, and passes each visible row its editor cell;
    /// rows own their child widget and sync get()/set() around the child's Update. Follows the retained-widget
    /// anatomy - reserve the region on the pointer first (even when disabled), clip content to <see cref="Bounds"/>
    /// via the <see cref="SpriteBatch"/> scissor, and read a captured theme colour for the label.
    /// <para>
    /// Rows fully above or below <see cref="Bounds"/> are skipped entirely in <see cref="Update"/>: their editor cell
    /// is still computed (scroll-aware), but a skipped row never runs its child widget, so it neither hit-tests
    /// off-view geometry nor reserves an off-view region (block-region pollution). Combined with the scroll-aware
    /// cell, a scrolled-away row cannot act on a tap that lands where it used to sit. A row that ran last frame but is
    /// culled this frame is also <see cref="PropertyRow.Deactivate"/>d once as it leaves, so a focused/open editor
    /// cannot keep consuming input behind the cull (the dual-focus double-typing bug).
    /// </para>
    /// </summary>
    public sealed class PropertyGrid
    {
        // Nominal row height used for the wheel step when the grid has no rows to average.
        const float DefaultRowHeight = 28f;
        // Left pad of the label text inside its cell.
        const float LabelPad = 6f;

        // Rows that ran Update last frame. A row present here but culled this frame is Deactivated exactly once as it
        // leaves view. Two sets are swapped each frame so the bookkeeping allocates nothing after construction.
        HashSet<PropertyRow> _ranLastFrame = new();
        HashSet<PropertyRow> _ranThisFrame = new();

        /// <summary>The view rect the caller owns. Rows lay out downward from its top edge, offset by <see cref="ScrollOffset"/>.</summary>
        public Rect Bounds;

        /// <summary>The rows, top to bottom. Build the inspector by adding typed rows.</summary>
        public List<PropertyRow> Rows { get; } = new();

        /// <summary>Fraction of <see cref="Bounds"/>.Width given to the label column (the editor gets the rest). Default 0.45.</summary>
        public float LabelFraction { get; set; } = 0.45f;

        /// <summary>Vertical gap in pixels between stacked rows. Default 4.</summary>
        public float RowSpacing { get; set; } = 4f;

        /// <summary>
        /// Rows advanced per wheel notch. The wheel step is <c>notches * (average row height) * WheelRowsPerNotch</c>,
        /// so one notch moves this many rows. Default 3 matches <see cref="TreeView.WheelRowsPerNotch"/> for the same
        /// side-by-side feel (a <see cref="TreeView"/> notch moves 3 of its rows too).
        /// </summary>
        public float WheelRowsPerNotch { get; set; } = 3f;

        /// <summary>Vertical scroll in pixels. Wheel scrolling clamps this to the content, and every <see cref="Update"/> re-clamps it.</summary>
        public float ScrollOffset { get; set; }

        /// <summary>When false, <see cref="Update"/> reserves the region on the pointer then ignores all input. Default true.</summary>
        public bool Enabled = true;

        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Default 1 is a no-op.</summary>
        public float Opacity = 1f;

        /// <summary>Colour of the row labels, captured from the ambient theme at construction.</summary>
        public Vector4 LabelColor = GuiTheme.Default.Text;

        /// <summary>True on the frame any row changed its bound value (mirrors the widget <c>WasChanged</c> idiom).</summary>
        public bool WasChanged { get; private set; }

        /// <summary>Create a grid over the given screen rect. Add rows via <see cref="Rows"/>.</summary>
        public PropertyGrid(Rect bounds) { Bounds = bounds; }

        /// <summary>Total stacked height of all rows including inter-row spacing (the scroll content extent).</summary>
        public float ContentHeight
        {
            get
            {
                float h = 0f;
                foreach (PropertyRow row in Rows) h += row.Height + RowSpacing;
                return h;
            }
        }

        /// <summary>Max scroll = content extent minus the visible height, clamped &gt;= 0.</summary>
        public float MaxScroll => MathF.Max(0f, ContentHeight - Bounds.Height);

        /// <summary>
        /// The editor cell of row <paramref name="rowIndex"/> this frame: pure arithmetic from <see cref="Bounds"/>,
        /// the prior rows' heights + <see cref="RowSpacing"/>, <see cref="LabelFraction"/>, and
        /// <see cref="ScrollOffset"/>. May lie outside <see cref="Bounds"/> when scrolled. Public for tests and hosts.
        /// </summary>
        public Rect RowEditorBounds(int rowIndex)
        {
            float y = Bounds.Y - ScrollOffset;
            for (int i = 0; i < rowIndex; i++) y += Rows[i].Height + RowSpacing;
            float x = Bounds.X + Bounds.Width * LabelFraction;
            float w = Bounds.Width * (1f - LabelFraction);
            return new Rect(x, y, w, Rows[rowIndex].Height);
        }

        // The label cell (left column) of row `rowIndex` this frame, sharing the editor cell's Y/Height.
        Rect RowLabelBounds(int rowIndex, Rect editorCell) =>
            new(Bounds.X, editorCell.Y, Bounds.Width * LabelFraction, editorCell.Height);

        /// <summary>
        /// Reserve the region on the pointer, apply wheel scrolling (clamped), then run each in-view row so it can
        /// poll its getter and process input. Rows fully outside <see cref="Bounds"/> are skipped. Returns
        /// <see cref="WasChanged"/> - true when any row changed its bound value this frame.
        /// </summary>
        public bool Update(InputManager input, float dt)
        {
            WasChanged = false;
            input.BlockInputRegion(Bounds);
            if (!Enabled) return false;

            // Wheel scroll while the pointer is over the grid, clamped to the content. One notch moves
            // WheelRowsPerNotch rows (via the average row height), matching the TreeView feel side by side.
            int notches = input.GetScrollIn(Bounds);
            if (notches != 0) ScrollOffset -= notches * AverageRowHeight() * WheelRowsPerNotch;
            ScrollOffset = Math.Clamp(ScrollOffset, 0f, MaxScroll);

            _ranThisFrame.Clear();
            for (int i = 0; i < Rows.Count; i++)
            {
                Rect cell = RowEditorBounds(i);
                PropertyRow row = Rows[i];
                // Skip rows scrolled fully out of view: do not run their child widget, so it neither hit-tests
                // off-view geometry nor reserves an off-view region (block-region pollution). A row that ran last
                // frame but is culled now is Deactivated once as it leaves, so a focused/open editor cannot keep
                // consuming input behind the cull (the dual-focus double-typing bug).
                if (cell.Bottom <= Bounds.Y || cell.Y >= Bounds.Bottom)
                {
                    if (_ranLastFrame.Contains(row)) row.Deactivate();
                    continue;
                }
                if (row.Update(cell, input, dt)) WasChanged = true;
                _ranThisFrame.Add(row);
            }
            // This frame's in-view rows become next frame's reference set (swap, no allocation).
            (_ranLastFrame, _ranThisFrame) = (_ranThisFrame, _ranLastFrame);
            return WasChanged;
        }

        // Mean row height, used to size the wheel step so one notch moves WheelRowsPerNotch rows. Falls back to the
        // default row height when the grid is empty.
        float AverageRowHeight()
        {
            if (Rows.Count == 0) return DefaultRowHeight;
            float total = 0f;
            foreach (PropertyRow row in Rows) total += row.Height;
            return total / Rows.Count;
        }

        /// <summary>
        /// Draw the visible rows clipped to <see cref="Bounds"/>: each row's label in the left column, then its editor
        /// in the right cell. <paramref name="white"/> is a 1x1 white texture.
        /// </summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            batch.SetScissor(Bounds);
            for (int i = 0; i < Rows.Count; i++)
            {
                Rect cell = RowEditorBounds(i);
                if (cell.Bottom <= Bounds.Y || cell.Y >= Bounds.Bottom) continue;   // fully scrolled out of view

                PropertyRow row = Rows[i];
                Rect label = RowLabelBounds(i, cell);
                string text = row.Label.Resolve();
                Vector2 pos = GuiDraw.AlignedTextPos(label, font.Measure(text), font.LineHeight, GuiAlign.Left, 1f, LabelPad);
                batch.DrawString(font, text, new Vector2(MathF.Floor(pos.X), MathF.Floor(pos.Y)),
                    (Color)GuiDraw.WithOpacity(LabelColor, Opacity));

                row.ApplyOpacity(Opacity);
                row.Draw(batch, white, font, cell);
            }
            batch.ClearScissor();
        }
    }
}
