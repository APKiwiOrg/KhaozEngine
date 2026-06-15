using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>Row kind in a <see cref="PopupPanel"/>.</summary>
    public enum PopupRowType { Header, Stat, Spacer }

    /// <summary>One content row in a <see cref="PopupPanel"/>: a section header, a label/value stat, or a spacer.</summary>
    public readonly record struct PopupRow(PopupRowType Type, string Label, string Value, Vector4 ValueColor)
    {
        public static PopupRow Header(string text) => new(PopupRowType.Header, text, "", Vector4.One);
        public static PopupRow Stat(string label, string value, Vector4 valueColor) => new(PopupRowType.Stat, label, value, valueColor);
        public static PopupRow Spacer() => new(PopupRowType.Spacer, "", "", Vector4.Zero);
    }

    /// <summary>
    /// A modal dialog: dimmed scrim, centered panel auto-sized to its rows (clamped between <see cref="MinHeight"/>
    /// and <see cref="MaxHeightFraction"/> of the viewport), a title bar, label/value content rows, and a footer
    /// with a dismiss button (plus an optional primary action). <see cref="Update"/> blocks the pointer over the
    /// panel so the screen beneath ignores clicks. Ported from the 4.x <c>UI.PopupPanel</c> (internal scroll
    /// dropped — that lives in <see cref="ScrollablePanel"/>; game LayoutConstants dropped).
    /// </summary>
    public sealed class PopupPanel
    {
        readonly List<PopupRow> _rows = new();

        public Vector2 Viewport = new(960, 540);
        public string Title = "";
        public float WidthFraction = 0.85f;
        public float MaxHeightFraction = 0.85f;
        public float MinHeight = 150f;
        public float TitleBarHeight = 36f;
        public float FooterHeight = 40f;
        public float ContentPadding = 12f;
        public float RowHeight = 24f;
        public float HeaderRowHeight = 28f;
        public float SpacerHeight = 12f;
        public float ScrimOpacity = 0.6f;

        public bool ShowPrimaryAction;
        public bool PrimaryActionEnabled = true;
        public string DismissText = "Close";
        public string PrimaryActionText = "OK";
        public bool WasPrimaryActionClicked { get; private set; }

        public Vector4 ScrimColor = new(0f, 0f, 0f, 1f);
        public Vector4 PanelColor = new(0.063f, 0.071f, 0.11f, 0.96f);
        public Vector4 PanelBorder = new(0.24f, 0.255f, 0.33f, 1f);
        public Vector4 TitleBarColor = new(0.086f, 0.098f, 0.149f, 1f);
        public Vector4 TitleColor = Vector4.One;
        public Vector4 HeaderColor = new(0.71f, 0.75f, 0.82f, 1f);
        public Vector4 LabelColor = new(0.67f, 0.69f, 0.73f, 1f);
        public Vector4 DismissColor = new(0.18f, 0.30f, 0.42f, 1f);
        public Vector4 PrimaryColor = new(0.20f, 0.44f, 0.30f, 1f);
        public Vector4 DisabledColor = new(0.14f, 0.14f, 0.17f, 1f);

        public SpriteFont? TitleFont, BodyFont;

        public PopupPanel() { }

        /// <summary>Replace the content rows. Call before <see cref="Update"/>/<see cref="Draw"/>.</summary>
        public void SetRows(IReadOnlyList<PopupRow> rows) { _rows.Clear(); _rows.AddRange(rows); }

        float ContentHeight()
        {
            float h = 0f;
            foreach (var r in _rows)
                h += r.Type switch { PopupRowType.Header => HeaderRowHeight, PopupRowType.Spacer => SpacerHeight, _ => RowHeight };
            return h;
        }

        /// <summary>The centered, auto-sized panel rectangle.</summary>
        public Rect PanelRect()
        {
            float panelW = Viewport.X * WidthFraction;
            float maxH = Viewport.Y * MaxHeightFraction;
            float totalH = Math.Clamp(TitleBarHeight + ContentHeight() + FooterHeight + ContentPadding * 2f, MinHeight, maxH);
            float x = (Viewport.X - panelW) * 0.5f;
            float y = (Viewport.Y - totalH) * 0.4f;   // slightly above center
            return new Rect(x, y, panelW, totalH);
        }

        /// <summary>The content area between the title bar and the footer.</summary>
        public Rect ContentRect()
        {
            Rect p = PanelRect();
            float top = p.Y + TitleBarHeight + ContentPadding;
            float bottom = p.Bottom - FooterHeight - ContentPadding;
            return new Rect(p.X + ContentPadding, top, p.Width - ContentPadding * 2f, MathF.Max(0f, bottom - top));
        }

        const float BtnW = 130f, BtnH = 30f, BtnGap = 10f;

        /// <summary>The dismiss button rectangle (left button when a primary action is shown, else centered).</summary>
        public Rect DismissBounds()
        {
            Rect p = PanelRect();
            float by = p.Bottom - FooterHeight + (FooterHeight - BtnH) * 0.5f;
            if (!ShowPrimaryAction)
                return new Rect(p.X + (p.Width - BtnW) * 0.5f, by, BtnW, BtnH);
            float total = BtnW * 2f + BtnGap;
            float bx = p.X + (p.Width - total) * 0.5f;
            return new Rect(bx, by, BtnW, BtnH);
        }

        /// <summary>The primary-action button rectangle (right of dismiss); empty when not shown.</summary>
        public Rect PrimaryBounds()
        {
            if (!ShowPrimaryAction) return new Rect(0, 0, 0, 0);
            Rect d = DismissBounds();
            return new Rect(d.Right + BtnGap, d.Y, BtnW, BtnH);
        }

        /// <summary>Reserve the panel region, hit-test the footer buttons. Returns true if dismiss was tapped.</summary>
        public bool Update(Pointer pointer)
        {
            WasPrimaryActionClicked = false;
            pointer.BlockRegion(PanelRect());

            if (ShowPrimaryAction && PrimaryActionEnabled && pointer.IsTapIn(PrimaryBounds()))
                WasPrimaryActionClicked = true;

            return pointer.IsTapIn(DismissBounds());
        }

        /// <summary>Draw the full popup. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white, Pointer pointer)
        {
            Rect p = PanelRect();
            GuiDraw.Fill(batch, white, new Rect(0, 0, Viewport.X, Viewport.Y), new Vector4(ScrimColor.X, ScrimColor.Y, ScrimColor.Z, ScrimOpacity));
            GuiDraw.Fill(batch, white, p, PanelColor);
            GuiDraw.Border(batch, white, p, 1f, PanelBorder);
            GuiDraw.Fill(batch, white, new Rect(p.X, p.Y, p.Width, TitleBarHeight), TitleBarColor);

            if (TitleFont != null)
                TextLayout.DrawAligned(batch, TitleFont, Title, p.X, p.Width,
                    p.Y + (TitleBarHeight - TitleFont.LineHeight) * 0.5f, TextAlign.Center, TitleColor);

            if (BodyFont != null) DrawRows(batch);

            DrawButton(batch, white, DismissBounds(), DismissText, DismissColor, true, pointer);
            if (ShowPrimaryAction)
                DrawButton(batch, white, PrimaryBounds(), PrimaryActionText, PrimaryColor, PrimaryActionEnabled, pointer);
        }

        void DrawRows(SpriteBatch batch)
        {
            Rect c = ContentRect();
            float y = c.Y;
            foreach (var r in _rows)
            {
                switch (r.Type)
                {
                    case PopupRowType.Header:
                        TextLayout.DrawAligned(batch, BodyFont!, r.Label, c.X, c.Width, y + 6f, TextAlign.Left, HeaderColor);
                        y += HeaderRowHeight;
                        break;
                    case PopupRowType.Stat:
                        float ty = y + (RowHeight - BodyFont!.LineHeight) * 0.5f;
                        TextLayout.DrawAligned(batch, BodyFont!, r.Label, c.X, c.Width, ty, TextAlign.Left, LabelColor);
                        TextLayout.DrawAligned(batch, BodyFont!, r.Value, c.X, c.Width, ty, TextAlign.Right, r.ValueColor);
                        y += RowHeight;
                        break;
                    case PopupRowType.Spacer:
                        y += SpacerHeight;
                        break;
                }
            }
        }

        void DrawButton(SpriteBatch batch, Texture2D white, Rect r, string text, Vector4 color, bool enabled, Pointer pointer)
        {
            Vector4 c = !enabled ? DisabledColor : pointer.IsHoveringIn(r) ? color * 1.3f : color;
            GuiDraw.Fill(batch, white, r, c);
            if (BodyFont != null)
                TextLayout.DrawAligned(batch, BodyFont, text, r.X, r.Width, r.Y + (r.Height - BodyFont.LineHeight) * 0.5f, TextAlign.Center, Vector4.One);
        }
    }
}
