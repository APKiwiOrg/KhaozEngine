using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>
/// Headless-testable presenter for a parsed <see cref="PatchNotesDocument"/>: a modal, centered panel with a
/// title row, a close button, and a scissor-clipped scrollable column of collapsible per-build sections. Pure
/// layout plus input, no file IO and (outside <see cref="Draw"/>) no GPU, so the layout and interaction rules
/// are unit-testable with a fake <see cref="ITextMeasurer"/>. <see cref="Update"/> / <see cref="Draw"/> mirror
/// the shape <see cref="UpdateOverlayView"/> established (input, dt, viewport / batch, font, white, viewport);
/// wrap it in a <see cref="Screen"/> for stack-based games. Newest build (index 0) starts expanded, the rest
/// collapsed.
/// <para>
/// The changelog BODY (version, build name, date, and note text) is authored per game in
/// <c>docs/PLAY_CHANGELOG.md</c> and is intentionally NOT localized, so it is drawn straight from the
/// document; only the specific members that draw it (<see cref="DrawBuildHeader"/>, <see cref="DrawNote"/>)
/// carry <see cref="LocalizationExemptAttribute"/>. The CHROME (title, close, category labels, empty state)
/// still resolves through <see cref="PatchNotesStrings"/> and stays under analyzer coverage.
/// </para>
/// </summary>
public sealed class PatchNotesView
{
    // Panel sizing: roughly 70% wide, 80% tall, clamped to sane minimums and never past the viewport.
    const float WidthFraction = 0.70f;
    const float HeightFraction = 0.80f;
    const float MinPanelWidth = 360f;
    const float MinPanelHeight = 240f;

    const float TitleBarHeight = 40f;
    const float ContentPadding = 16f;
    const float CloseButtonSize = 24f;

    // Content column metrics (design units; row text height comes from the measurer's LineHeight).
    const float BuildHeaderHeight = 32f;
    const float CategoryRowHeight = 24f;
    const float GroupGap = 6f;
    const float BuildGap = 12f;
    const float NoteGap = 4f;
    const float BulletIndent = 18f;
    const float ChevronBox = 18f;
    const float BadgeSize = 12f;

    const float ScrollbarWidth = 6f;
    const float ScrollbarGap = 4f;
    const float ScrollbarGutter = ScrollbarWidth + ScrollbarGap;

    const float ScrollWheelSpeed = 40f;
    const float KeyScrollSpeed = 400f;   // design units per second while Up / Down is held

    readonly PatchNotesDocument _document;
    readonly bool[] _expanded;

    float _scrollOffset;
    bool _closeHover;

    /// <summary>The scrim behind the panel (dims the screen below). Fixed dark, non-themed.</summary>
    readonly Color _scrimColor = new(0f, 0f, 0f, 0.6f);

    /// <summary>The look (panel, header, body, muted dates, code spans, category tags). Retune at runtime.</summary>
    public PatchNotesTheme Theme { get; set; }

    /// <summary>True once the close button was tapped or Escape was pressed. A one-way latch the host reads to dismiss.</summary>
    public bool CloseRequested { get; private set; }

    /// <summary>The current vertical scroll offset in design units (0 at the top), clamped to the content overflow.</summary>
    public float ScrollOffset => _scrollOffset;

    /// <summary>Create a view over <paramref name="document"/> (null is treated as <see cref="PatchNotesDocument.Empty"/>),
    /// with an optional <paramref name="theme"/> (defaults to <see cref="PatchNotesTheme.Default"/>). The newest
    /// build (index 0) starts expanded, every other build collapsed.</summary>
    public PatchNotesView(PatchNotesDocument document, PatchNotesTheme? theme = null)
    {
        _document = document ?? PatchNotesDocument.Empty;
        Theme = theme ?? PatchNotesTheme.Default;
        _expanded = new bool[_document.Builds.Count];
        if (_expanded.Length > 0) _expanded[0] = true;
    }

    /// <summary>True when build <paramref name="buildIndex"/> is expanded. An out-of-range index returns false (no throw).</summary>
    public bool IsExpanded(int buildIndex) =>
        buildIndex >= 0 && buildIndex < _expanded.Length && _expanded[buildIndex];

    /// <summary>Flip the expanded state of build <paramref name="buildIndex"/>. An out-of-range index is a no-op (no throw).</summary>
    public void Toggle(int buildIndex)
    {
        if (buildIndex < 0 || buildIndex >= _expanded.Length) return;
        _expanded[buildIndex] = !_expanded[buildIndex];
    }

    /// <summary>
    /// Total height (design units) the scrollable column occupies at <paramref name="contentWidth"/>, given the
    /// current expansion state and the wrap metrics of <paramref name="measurer"/>. Collapsed builds contribute
    /// only their header row; an expanded build adds its category rows and word-wrapped notes. The empty document
    /// reports a single line (the centered empty-state message). Pure: no GPU, no side effects.
    /// </summary>
    public float MeasureContentHeight(ITextMeasurer measurer, float contentWidth)
    {
        if (_document.IsEmpty) return measurer.LineHeight;

        float h = 0f;
        for (int i = 0; i < _document.Builds.Count; i++)
        {
            h += BuildHeaderHeight;
            if (_expanded[i]) h += ExpandedBuildHeight(_document.Builds[i], measurer, contentWidth);
            h += BuildGap;
        }
        return h;
    }

    float ExpandedBuildHeight(PatchNotesBuild build, ITextMeasurer measurer, float contentWidth)
    {
        float noteWidth = MathF.Max(1f, contentWidth - BulletIndent);
        float h = 0f;
        foreach (PatchNoteGroup group in build.Groups)
        {
            h += CategoryRowHeight;
            foreach (PatchNote note in group.Notes)
                h += LayoutNote(note, measurer, noteWidth, null) * measurer.LineHeight + NoteGap;
            h += GroupGap;
        }
        return h;
    }

    /// <summary>
    /// Advance input for a frame: block the pointer over the whole viewport (modal), handle the close button and
    /// Escape, toggle a build header on tap, and scroll on wheel / drag / held Up-Down keys, clamping to range.
    /// Returns true while the view is still open, false the frame it requests close. The <paramref name="pointer"/>
    /// is already updated for the frame (design space); <paramref name="input"/> carries the wheel + key signals;
    /// <paramref name="measurer"/> drives the scroll clamp (a real <see cref="SpriteFont"/> in game, a fake in tests).
    /// </summary>
    public bool Update(Pointer pointer, InputState input, float dt, Rect viewport, ITextMeasurer measurer)
    {
        pointer.BlockRegion(viewport);

        Rect closeButton = CloseButtonRect(viewport);
        _closeHover = pointer.IsHoveringIn(closeButton);
        if (pointer.IsTapIn(closeButton) || input.WasPressed(Key.Escape))
            CloseRequested = true;

        Rect content = ContentViewport(viewport);

        if (!_document.IsEmpty && pointer.IsJustReleased)
            ToggleTappedHeader(pointer, content, measurer);

        // Wheel while hovering the content, plus drag-to-scroll and held Up / Down keys.
        if (input.ScrollDelta != 0f && pointer.IsPointerIn(content))
            _scrollOffset -= input.ScrollDelta * ScrollWheelSpeed;

        float dragY = pointer.GetDragDelta(content).Y;
        if (dragY != 0f)
            _scrollOffset -= dragY;

        if (input.IsDown(Key.Down)) _scrollOffset += KeyScrollSpeed * dt;
        if (input.IsDown(Key.Up)) _scrollOffset -= KeyScrollSpeed * dt;

        ClampScroll(content, measurer);
        return !CloseRequested;
    }

    void ToggleTappedHeader(Pointer pointer, Rect content, ITextMeasurer measurer)
    {
        float y = content.Y - _scrollOffset;
        for (int i = 0; i < _document.Builds.Count; i++)
        {
            var header = new Rect(content.X, y, content.Width, BuildHeaderHeight);
            // Require the tap to fall inside the visible content region so a header scrolled under the title
            // bar (or below the fold) is not toggled by a click over the chrome outside the scroll area.
            if (pointer.IsTapIn(header)
                && content.Contains(pointer.PressOrigin)
                && content.Contains(pointer.Position))
            {
                Toggle(i);
                return;
            }
            y += BuildHeaderHeight;
            if (_expanded[i]) y += ExpandedBuildHeight(_document.Builds[i], measurer, content.Width);
            y += BuildGap;
        }
    }

    void ClampScroll(Rect content, ITextMeasurer measurer)
    {
        float max = MathF.Max(0f, MeasureContentHeight(measurer, content.Width) - content.Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0f, max);
    }

    /// <summary>
    /// Draw the scrim, panel, title row, close button, and the scissor-clipped scrollable column into
    /// <paramref name="viewport"/>. <paramref name="white"/> is a 1x1 white texture; <paramref name="font"/> both
    /// measures and renders the text. Call once per frame after <see cref="Update"/>.
    /// </summary>
    public void Draw(SpriteBatch batch, SpriteFont font, Texture2D white, Rect viewport)
    {
        GuiDraw.Fill(batch, white, viewport, _scrimColor);

        Rect panel = PanelRect(viewport);
        GuiDraw.Fill(batch, white, panel, Theme.PanelFill);

        var titleBar = new Rect(panel.X, panel.Y, panel.Width, TitleBarHeight);
        GuiDraw.Fill(batch, white, titleBar, Theme.HeaderFill);

        // Re-stroke the border after the title-bar fill: the fill above spans the panel's full width from
        // its top-left, so drawing the border first let it paint over the top (and upper-side) border
        // pixels, leaving the border appearing to start below the title bar. Mirrors PopupPanel.Draw's
        // border re-stroke for the same reason, so the border wraps the whole panel, title bar included.
        GuiDraw.Border(batch, white, panel, 1f, GuiDraw.WithOpacity(Theme.MutedText, 0.5f));

        string title = PatchNotesStrings.Resolve(PatchNotesStrings.Title);
        float titleY = panel.Y + (TitleBarHeight - font.LineHeight) * 0.5f;
        TextLayout.DrawAligned(batch, font, title, panel.X + ContentPadding, panel.Width - ContentPadding,
            titleY, TextAlign.Left, Theme.HeaderText);

        DrawCloseButton(batch, white, viewport);

        Rect content = ContentViewport(viewport);
        if (_document.IsEmpty)
        {
            string empty = PatchNotesStrings.Resolve(PatchNotesStrings.Empty);
            float ey = content.Y + (content.Height - font.LineHeight) * 0.5f;
            TextLayout.DrawAligned(batch, font, empty, content.X, content.Width, ey, TextAlign.Center, Theme.MutedText);
            return;
        }

        batch.SetScissor(content);
        DrawColumn(batch, font, white, content);
        batch.ClearScissor();

        DrawScrollbar(batch, white, content, MeasureContentHeight(font, content.Width));
    }

    void DrawColumn(SpriteBatch batch, SpriteFont font, Texture2D white, Rect content)
    {
        float lineH = font.LineHeight;
        float noteWidth = MathF.Max(1f, content.Width - BulletIndent);
        var placements = new List<WordPlacement>();
        float y = content.Y - _scrollOffset;

        for (int i = 0; i < _document.Builds.Count; i++)
        {
            PatchNotesBuild build = _document.Builds[i];
            if (RowVisible(y, BuildHeaderHeight, content))
                DrawBuildHeader(batch, font, white, build, _expanded[i], new Rect(content.X, y, content.Width, BuildHeaderHeight));
            y += BuildHeaderHeight;

            if (_expanded[i])
            {
                foreach (PatchNoteGroup group in build.Groups)
                {
                    if (RowVisible(y, CategoryRowHeight, content))
                        DrawCategoryRow(batch, font, white, group.Category, new Rect(content.X, y, content.Width, CategoryRowHeight));
                    y += CategoryRowHeight;

                    foreach (PatchNote note in group.Notes)
                    {
                        placements.Clear();
                        int lines = LayoutNote(note, font, noteWidth, placements);
                        float rowH = lines * lineH + NoteGap;
                        if (RowVisible(y, rowH, content))
                            DrawNote(batch, font, white, content, placements, y, lineH);
                        y += rowH;
                    }
                    y += GroupGap;
                }
            }
            y += BuildGap;

            if (y > content.Bottom) break;   // everything below is off the fold
        }
    }

    static bool RowVisible(float y, float rowHeight, Rect content) =>
        y + rowHeight >= content.Y && y <= content.Bottom;

    // Draws the changelog build's version, build name, and date straight from the document: intentionally
    // unlocalized per-game changelog body content, not chrome.
    [LocalizationExempt]
    void DrawBuildHeader(SpriteBatch batch, SpriteFont font, Texture2D white, PatchNotesBuild build, bool expanded, Rect header)
    {
        // Chevron: up when expanded (tap to collapse), down when collapsed (tap to expand).
        var center = new Vector2(header.X + ChevronBox * 0.5f, header.Y + header.Height * 0.5f);
        GuiDraw.Caret(batch, white, center, 5f, 3f, pointingUp: expanded, thickness: 2f, Theme.MutedText);

        string heading = build.BuildName.Length > 0
            ? $"{build.Version} ({build.BuildName})"
            : build.Version;
        float ty = header.Y + (header.Height - font.LineHeight) * 0.5f;
        batch.DrawString(font, heading, new Vector2(MathF.Floor(header.X + ChevronBox), MathF.Floor(ty)), Theme.HeaderText);

        if (build.Date.Length > 0)
            TextLayout.DrawAligned(batch, font, build.Date, header.X, header.Width, ty, TextAlign.Right, Theme.MutedText);
    }

    void DrawCategoryRow(SpriteBatch batch, SpriteFont font, Texture2D white, PatchNoteCategory category, Rect row)
    {
        Color color = Theme.CategoryColor(category);
        var badge = new Rect(row.X, row.Y + (row.Height - BadgeSize) * 0.5f, BadgeSize, BadgeSize);
        GuiDraw.Fill(batch, white, badge, color);

        string label = PatchNotesStrings.Resolve(PatchNotesStrings.CategoryLabel(category));
        float lx = badge.Right + 6f;
        float ly = row.Y + (row.Height - font.LineHeight) * 0.5f;
        batch.DrawString(font, label, new Vector2(MathF.Floor(lx), MathF.Floor(ly)), color);
    }

    // Draws a changelog note's wrapped word spans straight from the document: intentionally unlocalized
    // per-game changelog body content, not chrome.
    [LocalizationExempt]
    void DrawNote(SpriteBatch batch, SpriteFont font, Texture2D white, Rect content, List<WordPlacement> placements, float y, float lineH)
    {
        var dot = new Rect(content.X + 4f, y + (lineH - 3f) * 0.5f, 3f, 3f);
        GuiDraw.Fill(batch, white, dot, Theme.BodyText);

        float ox = content.X + BulletIndent;
        foreach (WordPlacement wp in placements)
            batch.DrawString(font, wp.Text,
                new Vector2(MathF.Floor(ox + wp.X), MathF.Floor(y + wp.Y)),
                wp.IsCode ? Theme.CodeText : Theme.BodyText);
    }

    void DrawCloseButton(SpriteBatch batch, Texture2D white, Rect viewport)
    {
        Rect r = CloseButtonRect(viewport);
        if (_closeHover)
            GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(Theme.HeaderText, 0.15f));
        float pad = r.Width * 0.3f;
        GuiDraw.Line(batch, white, new Vector2(r.X + pad, r.Y + pad), new Vector2(r.Right - pad, r.Bottom - pad), 2f, Theme.HeaderText);
        GuiDraw.Line(batch, white, new Vector2(r.Right - pad, r.Y + pad), new Vector2(r.X + pad, r.Bottom - pad), 2f, Theme.HeaderText);
    }

    void DrawScrollbar(SpriteBatch batch, Texture2D white, Rect content, float contentHeight)
    {
        if (contentHeight <= content.Height) return;   // no overflow, no bar

        float trackX = content.Right + ScrollbarGap;
        GuiDraw.Fill(batch, white, new Rect(trackX, content.Y, ScrollbarWidth, content.Height), GuiDraw.WithOpacity(Theme.MutedText, 0.25f));

        float max = contentHeight - content.Height;
        float thumbH = MathF.Max(24f, content.Height * (content.Height / contentHeight));
        float travel = MathF.Max(0f, content.Height - thumbH);
        float t = max > 0f ? Math.Clamp(_scrollOffset / max, 0f, 1f) : 0f;
        GuiDraw.Fill(batch, white, new Rect(trackX, content.Y + t * travel, ScrollbarWidth, thumbH), Theme.MutedText);
    }

    /// <summary>The centered, clamped panel rectangle within <paramref name="viewport"/>.</summary>
    internal Rect PanelRect(Rect viewport)
    {
        float w = MathF.Min(viewport.Width, MathF.Max(MinPanelWidth, viewport.Width * WidthFraction));
        float h = MathF.Min(viewport.Height, MathF.Max(MinPanelHeight, viewport.Height * HeightFraction));
        float x = viewport.X + (viewport.Width - w) * 0.5f;
        float y = viewport.Y + (viewport.Height - h) * 0.5f;
        return new Rect(x, y, w, h);
    }

    /// <summary>The scissor-clipped scroll region below the title bar (inside the padding, minus the scrollbar gutter).</summary>
    internal Rect ContentViewport(Rect viewport)
    {
        Rect p = PanelRect(viewport);
        float x = p.X + ContentPadding;
        float top = p.Y + TitleBarHeight + ContentPadding;
        float bottom = p.Bottom - ContentPadding;
        float w = p.Width - ContentPadding * 2f - ScrollbarGutter;
        return new Rect(x, top, MathF.Max(1f, w), MathF.Max(0f, bottom - top));
    }

    Rect CloseButtonRect(Rect viewport)
    {
        Rect p = PanelRect(viewport);
        float margin = (TitleBarHeight - CloseButtonSize) * 0.5f;
        return new Rect(p.Right - margin - CloseButtonSize, p.Y + margin, CloseButtonSize, CloseButtonSize);
    }

    /// <summary>
    /// Lay a note's spans out inline, wrapping at word granularity (per <see cref="TextLayout"/> conventions: a
    /// single word wider than <paramref name="maxWidth"/> keeps its own line). Words carry their span's
    /// <see cref="PatchNoteSpan.IsCode"/> so code words draw in the code color. When <paramref name="into"/> is
    /// non-null it receives each word's placement relative to the note's top-left. Returns the line count.
    /// </summary>
    static int LayoutNote(PatchNote note, ITextMeasurer measurer, float maxWidth, List<WordPlacement>? into)
    {
        float lineH = measurer.LineHeight;
        float spaceW = measurer.Measure(" ").X;
        float x = 0f, y = 0f;
        int lineCount = 1;
        bool haveWord = false, pendingSpace = false;

        void Place(string word, bool isCode)
        {
            float wordW = measurer.Measure(word).X;
            if (haveWord && x + (pendingSpace ? spaceW : 0f) + wordW > maxWidth)
            {
                y += lineH;
                lineCount++;
                x = 0f;
                haveWord = false;
                pendingSpace = false;
            }
            if (pendingSpace)
            {
                x += spaceW;
                pendingSpace = false;
            }
            into?.Add(new WordPlacement(word, isCode, x, y));
            x += wordW;
            haveWord = true;
        }

        foreach (PatchNoteSpan span in note.Spans)
        {
            string t = span.Text;
            int i = 0;
            while (i < t.Length)
            {
                if (char.IsWhiteSpace(t[i]))
                {
                    if (haveWord) pendingSpace = true;
                    while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
                }
                else
                {
                    int start = i;
                    while (i < t.Length && !char.IsWhiteSpace(t[i])) i++;
                    Place(t.Substring(start, i - start), span.IsCode);
                }
            }
        }
        return lineCount;
    }

    /// <summary>One laid-out word of a note: its text, whether it is a code span, and its offset from the note origin.</summary>
    readonly record struct WordPlacement(string Text, bool IsCode, float X, float Y);
}
