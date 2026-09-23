using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Showcase
{
    /// <summary>Page 6: drag and drop across widgets over one <see cref="GuiDragContext"/>, left at its defaults so
    /// this page is what those defaults get judged against. Two <see cref="SlotGrid"/>s trade items, the stash
    /// refuses one marked slot (the <see cref="GuiDragContext.RejectTint"/> wash shows while hovering it), a bare
    /// destroy rect accepts a drop and removes the item, and a release over nothing flies the ghost home.
    /// <para>
    /// Items paint through <see cref="SlotGrid.DrawSlotContent"/>, never <see cref="SlotGrid.SetContent"/>, so the
    /// grid has no <see cref="SlotContent"/> to substitute as a ghost. A solid item supplies its own painter and a
    /// hollow one supplies none, which is what makes the built-in placeholder frame the thing that flies.
    /// </para></summary>
    sealed class DragDropPage : ToolkitPage
    {
        internal const int SlotCount = 12, Columns = 4, RefusedSlot = 5;
        const float Slot = 56f, Gap = 6f, ColW = 280f, Inset = 9f;

        /// <summary>The opaque token a payload carries. The Gui layer only hands it back.</summary>
        internal sealed class Item
        {
            public readonly Vector4 Color;
            public readonly bool Painted;   // false: the payload carries no ghost painter
            public Item(Vector4 color, bool painted) { Color = color; Painted = painted; }
        }

        internal enum DropEvent { None, Moved, Swapped, Destroyed, Refused, Cancelled }

        readonly Item?[] _bag = new Item?[SlotCount], _stash = new Item?[SlotCount];
        readonly object _destroyId = new();
        readonly GuiDragContext _drag = new();
        SlotGrid _bagGrid = null!, _stashGrid = null!;
        Button _reset = null!;
        Rect _destroy;
        float _top, _headerY, _legendY, _statusY;

        internal GuiDragContext Drag => _drag;
        internal SlotGrid BagGrid => _bagGrid;
        internal SlotGrid StashGrid => _stashGrid;
        internal Rect DestroyRect => _destroy;
        internal DropEvent Last { get; private set; }
        internal Item? BagAt(int slot) => _bag[slot];
        internal Item? StashAt(int slot) => _stash[slot];

        protected override void OnLoad()
        {
            _bagGrid = BuildGrid(_bag);
            _stashGrid = BuildGrid(_stash);
            _stashGrid.CanAcceptDrop = (slot, _) => slot != RefusedSlot;
            _reset = new Button(default, ShowcaseStrings.DragReset, A.Small, Reset);
            Reset();
        }

        protected override void OnLayout(Rect bounds)
        {
            float x = bounds.X, gridY = bounds.Y + 92f;
            _top = bounds.Y;
            _headerY = bounds.Y + 60f;
            _bagGrid.Bounds = new Rect(x, gridY, 0, 0);          // sized by SlotSize / Spacing
            _stashGrid.Bounds = new Rect(x + 320f, gridY, 0, 0);
            _destroy = new Rect(x + 640f, gridY, ColW, _bagGrid.ContentBounds.Height);
            _legendY = _destroy.Bottom + 20f;
            _statusY = _legendY + 32f;
            _reset.Bounds = new Rect(x, _statusY + 40f, 200f, 40f);
        }

        // Leaving the tab mid-drag would strand a live drag whose release never arrives, so it flies home instead.
        public override void Deactivated() => Abandon();

        protected override bool OnUpdate(float dt, bool receivesInput, Rect bounds, InputManager input)
        {
            if (!receivesInput) { Abandon(); return false; }

            // The fixed order: BeginFrame, every source and target, EndFrame. None of these regions overlap, so the
            // first-offer-wins rule never has to pick between them.
            Pointer p = input.Pointer;
            _drag.BeginFrame(p, dt);
            _bagGrid.Update(p, _drag);
            _stashGrid.Update(p, _drag);
            if (_drag.OfferTargetIn(_destroy, _destroyId, accepted: true)) Destroy(_drag.LastDrop.Payload);
            bool reset = _reset.Update(p);
            _drag.EndFrame();

            // A release over the refused slot is still over a target, which is how refused reads apart from nothing.
            if (_drag.WasCancelled) Last = _drag.IsOverTarget ? DropEvent.Refused : DropEvent.Cancelled;
            return reset || _drag.IsDragging || _drag.WasDropped || _drag.WasCancelled;
        }

        protected override void OnDraw(SpriteBatch batch, Rect bounds)
        {
            Texture2D white = A.White;
            SpriteFont font = A.Small;
            Vector4 muted = GuiTheme.Default.TextMuted;

            TextLayout.DrawWrapped(batch, font, Res(ShowcaseStrings.DragHint), new Vector2(bounds.X, _top),
                bounds.Width, TextAlign.Left, (Color)GuiTheme.Default.Text);
            DrawSectionHeader(batch, ShowcaseStrings.DragSectionBag, _bagGrid.Bounds.X, _headerY, ColW);
            DrawSectionHeader(batch, ShowcaseStrings.DragSectionStash, _stashGrid.Bounds.X, _headerY, ColW);
            DrawSectionHeader(batch, ShowcaseStrings.DragSectionDestroy, _destroy.X, _headerY, ColW);

            _bagGrid.Draw(batch, white, font);
            _stashGrid.Draw(batch, white, font);

            bool armed = ReferenceEquals(_drag.HoveredTargetId, _destroyId);
            Vector4 danger = GuiTheme.Default.Danger;
            GuiDraw.Fill(batch, white, _destroy, danger with { W = armed ? 0.35f : 0.12f });
            GuiDraw.Border(batch, white, _destroy, armed ? 3f : 1f, danger);
            string drop = Res(ShowcaseStrings.DragDestroyHere);
            Vector2 m = font.Measure(drop);
            batch.DrawString(font, drop, new Vector2(_destroy.X + (_destroy.Width - m.X) * 0.5f,
                _destroy.Y + (_destroy.Height - font.LineHeight) * 0.5f), (Color)GuiTheme.Default.Text);

            batch.DrawString(font, Res(ShowcaseStrings.DragLegend), new Vector2(bounds.X, _legendY), (Color)muted);
            batch.DrawString(font, Res(StatusText(Last)), new Vector2(bounds.X, _statusY), (Color)GuiTheme.Default.Text);
            _reset.Draw(batch, white);

            _drag.Draw(batch, white, font);   // last: the ghost floats over everything it crosses
        }

        SlotGrid BuildGrid(Item?[] items)
        {
            var grid = new SlotGrid(default, SlotCount, Columns) { SlotSize = Slot, Spacing = Gap };
            grid.BeginDragPayload = slot => items[slot] is { } item
                ? new DragPayload(item, grid, slot, item.Painted ? Painter(item) : null)
                : null;
            grid.OnSlotDropped = (slot, payload) => Place(items, slot, payload);
            grid.DrawSlotContent = (slot, rect, batch) => DrawSlot(grid, items, slot, rect, batch);
            return grid;
        }

        static DragGhostPainter Painter(Item item) => (batch, white, _, rect) => PaintItem(batch, white, item, rect, 1f);

        void DrawSlot(SlotGrid grid, Item?[] items, int slot, Rect rect, SpriteBatch batch)
        {
            Texture2D white = A.White;
            if (grid == _stashGrid && slot == RefusedSlot)
            {
                GuiDraw.Fill(batch, white, rect, GuiTheme.Default.Danger with { W = 0.22f });
                GuiDraw.Border(batch, white, rect, 2f, GuiTheme.Default.Danger);
            }

            // Blank the origin slot for the whole flight, return included, so the fly-home visibly lands.
            bool inFlight = _drag.IsActive && ReferenceEquals(_drag.Payload.SourceId, grid) && _drag.Payload.SourceIndex == slot;
            if (items[slot] is { } item) PaintItem(batch, white, item, rect, inFlight ? 0.2f : 1f);

            if (grid.DropTargetSlot == slot)
                GuiDraw.Border(batch, white, rect, 3f, grid.DropTargetAccepted ? GuiTheme.Default.AccentBright : GuiTheme.Default.DangerBright);
        }

        static void PaintItem(SpriteBatch batch, Texture2D white, Item item, Rect slot, float alpha)
        {
            var r = new Rect(slot.X + Inset, slot.Y + Inset, slot.Width - Inset * 2f, slot.Height - Inset * 2f);
            Vector4 c = item.Color with { W = item.Color.W * alpha };
            if (item.Painted) GuiDraw.Fill(batch, white, r, c);
            else GuiDraw.Border(batch, white, r, 3f, c);
        }

        void Place(Item?[] target, int slot, in DragPayload payload)
        {
            Item?[] source = ReferenceEquals(payload.SourceId, _bagGrid) ? _bag : _stash;
            int from = payload.SourceIndex;
            if (ReferenceEquals(source, target) && from == slot) { Last = DropEvent.Moved; return; }
            Item? displaced = target[slot];
            target[slot] = source[from];
            source[from] = displaced;   // a swap: the displaced item takes the vacated slot
            Last = displaced is null ? DropEvent.Moved : DropEvent.Swapped;
        }

        void Destroy(in DragPayload payload)
        {
            Item?[] source = ReferenceEquals(payload.SourceId, _bagGrid) ? _bag : _stash;
            source[payload.SourceIndex] = null;
            Last = DropEvent.Destroyed;
        }

        void Abandon()
        {
            if (!_drag.IsDragging) return;
            _drag.Cancel();
            Last = DropEvent.Cancelled;
        }

        void Reset()
        {
            Array.Clear(_bag);
            Array.Clear(_stash);
            _bag[0] = new Item(new Vector4(0.35f, 0.62f, 1f, 1f), painted: true);
            _bag[1] = new Item(new Vector4(0.95f, 0.6f, 0.3f, 1f), painted: true);
            _bag[2] = new Item(new Vector4(0.45f, 0.85f, 0.5f, 1f), painted: true);
            _bag[5] = new Item(new Vector4(0.85f, 0.88f, 0.95f, 1f), painted: false);
            _stash[2] = new Item(new Vector4(0.7f, 0.5f, 0.95f, 1f), painted: true);
            _stash[8] = new Item(new Vector4(0.95f, 0.85f, 0.4f, 1f), painted: false);
            Last = DropEvent.None;
        }

        static StringId StatusText(DropEvent e) => e switch
        {
            DropEvent.Moved => ShowcaseStrings.DragStatusMoved,
            DropEvent.Swapped => ShowcaseStrings.DragStatusSwapped,
            DropEvent.Destroyed => ShowcaseStrings.DragStatusDestroyed,
            DropEvent.Refused => ShowcaseStrings.DragStatusRefused,
            DropEvent.Cancelled => ShowcaseStrings.DragStatusCancelled,
            _ => ShowcaseStrings.DragStatusNone,
        };
    }
}
