using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Reusable scrollable panel component that slides up from the bottom nav bar.
/// Handles: panel background, header, scissor-clipped content area, scroll input.
///
/// Usage pattern:
/// 1. Create with item count, item height, header text
/// 2. Call <see cref="Update"/> each frame to handle scroll input
/// 3. Call <see cref="BeginDraw"/> to start rendering (sets up scissor clip)
/// 4. Iterate <see cref="GetVisibleRange"/> and draw your items
/// 5. Call <see cref="EndDraw"/> to restore state
///
/// This component does NOT own a SpriteBatch or know about specific item types.
/// The owning screen provides the SpriteBatch and draws items in the clipped region.
/// </summary>
public sealed class ScrollablePanel
{
    private readonly VirtualResolution _vr;
    private readonly PrimitiveRenderer _renderer;
    private readonly InputManager _input;
    private readonly SpriteFont _titleFont;

    private float _scrollOffset;
    private RasterizerState? _scissorRasterizer;

    // User-resizable panel height via header drag
    private int? _userPanelTop;
    private bool _isDraggingHeader;

    /// <summary>Header text displayed at the top of the panel.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// The actual rendered width of the header widget (e.g., from QuantitySelector.GetTotalWidth()).
    /// Used to exclude only the real button area from the drag region, not the entire widget bounds.
    /// Set to 0 if there is no widget.
    /// </summary>
    public int HeaderWidgetWidth { get; set; }

    /// <summary>Height of each item row in virtual pixels.</summary>
    public int ItemHeight { get; set; } = 58;

    /// <summary>Vertical padding between items.</summary>
    public int ItemPadding { get; set; } = 4;

    /// <summary>Horizontal margin from panel edges.</summary>
    public int Margin { get; set; } = 8;

    /// <summary>Height of the panel header (title + divider + widget area).</summary>
    public int HeaderHeight { get; set; } = 38;

    /// <summary>Maximum visible rows before scrolling kicks in.</summary>
    public int MaxVisibleRows { get; set; } = 6;

    /// <summary>Minimum content height so small lists don't look odd.</summary>
    public int MinContentHeight { get; set; } = 120;

    /// <summary>
    /// The current transition alpha (0 = hidden, 1 = fully visible).
    /// Set this from the owning GameScreen's TransitionAlpha each frame.
    /// </summary>
    public float TransitionAlpha { get; set; } = 1f;

    /// <summary>
    /// Total number of items in the list. Set this before Update/Draw.
    /// </summary>
    public int ItemCount { get; set; }

    /// <summary>
    /// Creates a new ScrollablePanel.
    /// </summary>
    public ScrollablePanel(VirtualResolution vr, PrimitiveRenderer renderer, InputManager input, SpriteFont titleFont)
    {
        _vr = vr;
        _renderer = renderer;
        _input = input;
        _titleFont = titleFont;
    }

    /// <summary>
    /// The panel top Y position (accounting for slide transition).
    /// </summary>
    public int PanelTop => GetPanelTop();

    /// <summary>
    /// The Y position where the nav bar starts (panel bottom edge).
    /// </summary>
    public int NavTop => _vr.Height - LayoutConstants.BottomNavHeight;

    /// <summary>
    /// The full panel bounds (from PanelTop to NavTop).
    /// </summary>
    public Rectangle PanelBounds => new(0, PanelTop, _vr.Width, Math.Max(0, NavTop - PanelTop));

    /// <summary>
    /// The scrollable content area (below header, above nav).
    /// </summary>
    public Rectangle ContentBounds
    {
        get
        {
            int top = PanelTop + HeaderHeight;
            return new Rectangle(0, top, _vr.Width, Math.Max(0, NavTop - top));
        }
    }

    /// <summary>
    /// The header widget area  -- the right portion of the header bar, to the right of the title.
    /// Use this to position additional controls (e.g., QuantitySelector) inside the header.
    /// </summary>
    public Rectangle HeaderWidgetBounds
    {
        get
        {
            int panelTop = PanelTop;
            Vector2 titleSize = _titleFont.MeasureString(Title);
            int leftEdge = Margin + 6 + (int)titleSize.X + 10;
            int widgetY = panelTop + 8;
            int widgetHeight = HeaderHeight - 14;
            int widgetWidth = Math.Max(0, _vr.Width - leftEdge - Margin);
            return new Rectangle(leftEdge, widgetY, widgetWidth, widgetHeight);
        }
    }

    /// <summary>
    /// The header drag-handle bounds (for input routing  -- the full header strip).
    /// </summary>
    public Rectangle HeaderBounds => new(0, PanelTop, _vr.Width, HeaderHeight);

    /// <summary>
    /// The scrim area (above the panel, below top bar).
    /// </summary>
    public Rectangle ScrimBounds
    {
        get
        {
            int panelTop = PanelTop;
            return new Rectangle(0, LayoutConstants.TopBarHeight, _vr.Width,
                Math.Max(0, panelTop - LayoutConstants.TopBarHeight));
        }
    }

    /// <summary>
    /// Handles header drag-resize, content scroll, and scrim tap. Call each frame.
    /// Returns true if the scrim was tapped (caller should close the panel).
    /// </summary>
    public bool Update()
    {
        // Block the panel area so lower screens (e.g. camera controls) don't
        // also respond to drags and scrolls that start inside this panel.
        _input.BlockInputRegion(PanelBounds);

        // -- Header drag to resize --------------------------------------
        // Drag region is the full header MINUS only the actual button area on the right
        int dragWidth = HeaderWidgetWidth > 0
            ? Math.Max(0, _vr.Width - HeaderWidgetWidth - Margin)
            : _vr.Width;
        Rectangle dragRegion = new(0, PanelTop, dragWidth, HeaderHeight);

        if (_input.IsDraggingIn(dragRegion) || _isDraggingHeader)
        {
            _isDraggingHeader = _input.IsPointerDown;

            if (_isDraggingHeader)
            {
                int minTop = LayoutConstants.TopBarHeight;
                int maxTop = NavTop - MinContentHeight - HeaderHeight;
                int currentTop = _userPanelTop ?? GetDefaultPanelTop();

                currentTop += (int)_input.PointerDelta.Y;
                _userPanelTop = Math.Clamp(currentTop, minTop, maxTop);

                // Reset scroll when resizing  -- content area changed
                ClampScroll();
            }
        }

        if (!_input.IsPointerDown)
            _isDraggingHeader = false;

        // -- Content scroll ---------------------------------------------
        Rectangle contentBounds = ContentBounds;

        int scrollDelta = _input.GetScrollIn(contentBounds);
        if (scrollDelta != 0)
        {
            _scrollOffset -= scrollDelta * 0.3f;
            ClampScroll();
        }

        if (!_isDraggingHeader)
        {
            Vector2 dragDelta = _input.GetDragDelta(contentBounds);
            if (dragDelta != Vector2.Zero)
            {
                _scrollOffset -= dragDelta.Y;
                ClampScroll();
            }
        }

        // -- Scrim tap = close ------------------------------------------
        return _input.IsTapIn(ScrimBounds);
    }

    /// <summary>
    /// Returns true if there was a tap inside the content area.
    /// If true, use <see cref="GetTappedItemIndex"/> to find which item.
    /// </summary>
    public bool WasContentTapped()
    {
        return _input.IsTapIn(ContentBounds);
    }

    /// <summary>
    /// Given a tap position, returns the item index that was tapped, or -1.
    /// </summary>
    public int GetTappedItemIndex(Vector2 tapPosition)
    {
        int contentTop = PanelTop + HeaderHeight;
        float relativeY = tapPosition.Y - contentTop + _scrollOffset;
        int index = (int)(relativeY / (ItemHeight + ItemPadding));

        if (index < 0 || index >= ItemCount) return -1;

        // Check the tap is within the item rect (not in the padding gap)
        float itemStartY = index * (ItemHeight + ItemPadding);
        float itemEndY = itemStartY + ItemHeight;
        if (relativeY < itemStartY || relativeY > itemEndY) return -1;

        return index;
    }

    /// <summary>
    /// Draws the scrim, panel background, header, and divider.
    /// Sets up a scissor rectangle for the content area.
    /// After this call, draw your items, then call <see cref="EndDraw"/>.
    /// </summary>
    public void BeginDraw(SpriteBatch spriteBatch, GraphicsDevice graphicsDevice)
    {
        int navTop = NavTop;
        int panelTop = PanelTop;

        // Scrim
        float scrimAlpha = TransitionAlpha * 0.4f;
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(0, LayoutConstants.TopBarHeight, _vr.Width,
                navTop - LayoutConstants.TopBarHeight),
            new Color(0, 0, 0) * scrimAlpha);

        // Panel background
        int panelHeight = Math.Max(0, navTop - panelTop);
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(0, panelTop, _vr.Width, panelHeight),
            new Color(12, 12, 20, 245));

        // Top border
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(0, panelTop, _vr.Width, 1),
            new Color(60, 60, 80));

        // Drag handle indicator (small bar at top of header)
        int handleWidth = 32;
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(_vr.Width / 2 - handleWidth / 2, panelTop + 2, handleWidth, 3),
            new Color(80, 80, 100));

        // Header title  -- left-aligned to make room for right-side widgets
        TextHelper.Draw(spriteBatch, _titleFont, Title, Margin + 6, panelTop + 12, Color.White);

        // Divider under header
        _renderer.DrawFilledRect(spriteBatch,
            new Rectangle(Margin, panelTop + HeaderHeight - 2, _vr.Width - Margin * 2, 1),
            new Color(50, 50, 65));

        // End current batch, set scissor for content area, restart batch
        spriteBatch.End();

        _scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };

        Rectangle contentBounds = ContentBounds;

        // Guard against negative dimensions during early transition frames
        // (panel slides up from bottom  -- content area can be zero/negative height)
        int scissorW = Math.Max(0, (int)(contentBounds.Width * _vr.Scale));
        int scissorH = Math.Max(0, (int)(contentBounds.Height * _vr.Scale));

        graphicsDevice.ScissorRectangle = new Rectangle(
            (int)(contentBounds.X * _vr.Scale),
            (int)(contentBounds.Y * _vr.Scale),
            scissorW,
            scissorH);

        spriteBatch.Begin(
            samplerState: SamplerState.PointClamp,
            rasterizerState: _scissorRasterizer,
            transformMatrix: _vr.ScaleMatrix);
    }

    /// <summary>
    /// Ends the scissor-clipped drawing. Restores normal SpriteBatch state.
    /// The caller should start a new SpriteBatch.Begin if they need to draw more.
    /// </summary>
    public void EndDraw(SpriteBatch spriteBatch)
    {
        spriteBatch.End();
    }

    /// <summary>
    /// Returns the Y position for a given item index, accounting for scroll offset.
    /// Returns the position in virtual coordinates.
    /// </summary>
    public int GetItemY(int index)
    {
        int contentTop = PanelTop + HeaderHeight;
        return contentTop + index * (ItemHeight + ItemPadding) - (int)_scrollOffset;
    }

    /// <summary>
    /// Returns the item width (panel width minus margins on both sides).
    /// </summary>
    public int GetItemWidth() => _vr.Width - Margin * 2;

    private int GetPanelHeight()
    {
        int rowsThatFit = Math.Min(ItemCount, MaxVisibleRows);
        int contentHeight = rowsThatFit * (ItemHeight + ItemPadding);
        contentHeight = Math.Max(contentHeight, MinContentHeight);

        int navTop = _vr.Height - LayoutConstants.BottomNavHeight;
        int maxHeight = navTop - LayoutConstants.TopBarHeight - 20;

        return Math.Min(HeaderHeight + contentHeight, maxHeight);
    }

    /// <summary>
    /// Returns the content-driven default top position (no user override).
    /// </summary>
    private int GetDefaultPanelTop()
    {
        int navTop = _vr.Height - LayoutConstants.BottomNavHeight;
        int panelHeight = GetPanelHeight();
        return navTop - panelHeight;
    }

    private int GetPanelTop()
    {
        int targetTop = _userPanelTop ?? GetDefaultPanelTop();
        int navTop = _vr.Height - LayoutConstants.BottomNavHeight;
        int panelHeight = Math.Max(0, navTop - targetTop);

        // Slide: alpha=0 -> panel at navTop (hidden), alpha=1 -> at targetTop
        float slideOffset = (1f - TransitionAlpha) * panelHeight;
        return targetTop + (int)slideOffset;
    }

    private void ClampScroll()
    {
        int totalHeight = ItemCount * (ItemHeight + ItemPadding);
        int contentHeight = ContentBounds.Height;
        float maxScroll = Math.Max(0, totalHeight - contentHeight);
        _scrollOffset = MathHelper.Clamp(_scrollOffset, 0, maxScroll);
    }
}
