using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui;

/// <summary>Resolved popup layout, scroll state and the identity of an option press.</summary>
internal sealed class DropdownPopup
{
    readonly ScrollablePanel _scroll = new(default) { ItemSpacing = 0, DragScrollingEnabled = false, BlocksPointer = false };
    int _pressedIndex = -1;
    Rect _pressedRow, _pressedTrigger, _pressedViewport;
    float _pressedOffset;
    PointerCoordinates _pressedCoordinates;
    bool _pressInvalid;

    public Rect Bounds => _scroll.ContentBounds;
    public int FirstVisibleIndex => Bounds.Width > 0 && Bounds.Height > 0 && _scroll.ItemHeight > 0
        ? Math.Min(_scroll.ItemCount - 1, (int)(_scroll.ScrollOffset / _scroll.ItemHeight)) : 0;
    public int VisibleOptionCount => Bounds.Width > 0 && Bounds.Height > 0 && _scroll.ItemHeight > 0
        ? Math.Min(_scroll.ItemCount, (int)MathF.Ceiling((_scroll.ScrollOffset + Bounds.Height) / _scroll.ItemHeight)) - FirstVisibleIndex
        : 0;

    public void Resolve(Rect trigger, Rect viewport, int count)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || trigger.Width <= 0 || trigger.Height <= 0)
        {
            _scroll.Bounds = new Rect(viewport.X, viewport.Y, 0, 0);
            _scroll.ItemHeight = 0;
            _scroll.ItemCount = count;
            _scroll.ScrollTo(0);
            return;
        }
        float width = MathF.Min(trigger.Width, viewport.Width);
        float x = Math.Clamp(trigger.X, viewport.X, viewport.Right - width);
        float belowTop = Math.Clamp(trigger.Bottom, viewport.Y, viewport.Bottom);
        float aboveBottom = Math.Clamp(trigger.Y, viewport.Y, viewport.Bottom);
        float below = MathF.Max(0, viewport.Bottom - belowTop);
        float above = MathF.Max(0, aboveBottom - viewport.Y);
        float contentHeight = trigger.Height * count;
        bool useBelow = contentHeight <= below || (contentHeight > above && below >= above);
        float height = MathF.Min(contentHeight, useBelow ? below : above);
        float y = useBelow ? belowTop : aboveBottom - height;
        _scroll.Bounds = new Rect(x, y, width, height);
        _scroll.ItemHeight = trigger.Height;
        _scroll.ItemCount = count;
        _scroll.ScrollTo(_scroll.ScrollOffset);
    }

    public void Reset(int index)
    {
        _pressedIndex = -1;
        _pressInvalid = false;
        _scroll.ScrollTo(0);
        Reveal(index);
    }

    public void Reveal(int index)
    {
        Rect row = _scroll.ItemBounds(index);
        if (row.Y < Bounds.Y) _scroll.ScrollTo(index * _scroll.ItemHeight);
        else if (row.Bottom > Bounds.Bottom) _scroll.ScrollTo((index + 1) * _scroll.ItemHeight - Bounds.Height);
    }

    public Rect RowBounds(int index) => _scroll.ItemBounds(index);

    public Rect VisibleBounds(int index)
    {
        if (index < 0 || index >= _scroll.ItemCount || Bounds.Width <= 0 || Bounds.Height <= 0) return default;
        Rect row = RowBounds(index);
        float top = MathF.Max(row.Y, Bounds.Y);
        float bottom = MathF.Min(row.Bottom, Bounds.Bottom);
        return bottom > top ? new Rect(row.X, top, row.Width, bottom - top) : default;
    }

    public bool Update(Pointer pointer, float wheelDelta, Rect trigger, Rect viewport)
    {
        if (pointer.IsPressOriginFresh)
        {
            _pressedIndex = -1;
            _pressInvalid = false;
            for (int i = FirstVisibleIndex; i < FirstVisibleIndex + VisibleOptionCount; i++)
            {
                Rect row = VisibleBounds(i);
                if (!pointer.IsPointerIn(row)) continue;
                _pressedIndex = i;
                _pressedRow = row;
                _pressedTrigger = trigger;
                _pressedViewport = viewport;
                _pressedOffset = _scroll.ScrollOffset;
                _pressedCoordinates = pointer.Coordinates;
                break;
            }
        }

        bool ownsWheel = float.IsFinite(wheelDelta) && wheelDelta != 0 && pointer.IsPointerIn(Bounds);
        if (ownsWheel) _scroll.ScrollTo(_scroll.ScrollOffset - wheelDelta * _scroll.ItemHeight);
        if (_pressedIndex >= 0 && (_pressedRow != VisibleBounds(_pressedIndex)
            || _pressedOffset != _scroll.ScrollOffset || _pressedTrigger != trigger || _pressedViewport != viewport
            || !_pressedCoordinates.HasSameMapping(pointer.Coordinates)))
            _pressInvalid = true;
        return ownsWheel;
    }

    public int ReleasedOption(Pointer pointer)
    {
        if (_pressedIndex < 0 || !pointer.IsJustReleased) return -1;
        int index = !_pressInvalid && pointer.IsTapIn(VisibleBounds(_pressedIndex)) ? _pressedIndex : -1;
        pointer.ConsumeGesture();
        _pressedIndex = -1;
        return index;
    }

    public void BeginClip(SpriteBatch batch) => _scroll.BeginClip(batch);
    public void EndClip(SpriteBatch batch) => _scroll.EndClip(batch);

    public void DrawScrollIndicator(SpriteBatch batch, Texture2D white, Vector4 color)
    {
        if (_scroll.MaxScroll <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        float width = MathF.Min(3, Bounds.Width);
        float height = MathF.Min(Bounds.Height, MathF.Max(12, Bounds.Height * Bounds.Height / _scroll.ContentHeight));
        float y = Bounds.Y + _scroll.ScrollOffset / _scroll.MaxScroll * (Bounds.Height - height);
        GuiDraw.Fill(batch, white, new Rect(Bounds.Right - width, y, width, height), color);
    }
}
