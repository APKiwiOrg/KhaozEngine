using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    internal readonly record struct SlotGridCandidate(int Index, Rect SlotRect, Rect VisibleRect);

    /// <summary>
    /// Allocation-free slot traversal for <see cref="SlotGrid"/>. A visible clip narrows the traversal to the
    /// rows and columns that can intersect it, while a null clip retains the linear full-grid walk.
    /// </summary>
    internal readonly struct SlotGridCandidateEnumerable
    {
        readonly SlotGrid _grid;

        internal SlotGridCandidateEnumerable(SlotGrid grid) => _grid = grid;

        public Enumerator GetEnumerator() => new(_grid);

        internal static bool TryIntersect(Rect rect, Rect clip, out Rect visible)
        {
            float x = Math.Max(rect.X, clip.X);
            float y = Math.Max(rect.Y, clip.Y);
            float right = Math.Min(rect.Right, clip.Right);
            float bottom = Math.Min(rect.Bottom, clip.Bottom);
            visible = new Rect(x, y, Math.Max(0f, right - x), Math.Max(0f, bottom - y));
            return right > x && bottom > y;
        }

        internal struct Enumerator
        {
            const int Empty = 0;
            const int Linear = 1;
            const int ClippedLinear = 2;
            const int ClippedWindow = 3;

            readonly SlotGrid _grid;
            readonly Rect _clip;
            readonly int _count;
            readonly int _columns;
            readonly int _firstColumn;
            readonly int _lastColumn;
            readonly int _lastRow;
            readonly int _mode;
            int _nextIndex;
            int _row;
            int _column;

            internal Enumerator(SlotGrid grid)
            {
                _grid = grid;
                _clip = grid.VisibleBounds.GetValueOrDefault();
                _count = Math.Max(0, grid.Count);
                _columns = Math.Max(1, grid.Columns);
                _firstColumn = 0;
                _lastColumn = -1;
                _lastRow = -1;
                _mode = Empty;
                _nextIndex = 0;
                _row = 0;
                _column = -1;
                Current = default;

                if (_count == 0) return;
                if (grid.VisibleBounds is null)
                {
                    _mode = Linear;
                    return;
                }

                if (_clip.Width <= 0f || _clip.Height <= 0f || grid.SlotWidth <= 0f || grid.SlotHeight <= 0f)
                    return;

                float strideX = grid.SlotWidth + grid.Spacing;
                float strideY = grid.SlotHeight + grid.Spacing;
                if (strideX <= 0f || strideY <= 0f
                    || !float.IsFinite(strideX) || !float.IsFinite(strideY)
                    || !float.IsFinite(grid.Bounds.X) || !float.IsFinite(grid.Bounds.Y)
                    || !float.IsFinite(grid.SlotWidth) || !float.IsFinite(grid.SlotHeight)
                    || !float.IsFinite(_clip.X) || !float.IsFinite(_clip.Y)
                    || !float.IsFinite(_clip.Right) || !float.IsFinite(_clip.Bottom))
                {
                    _mode = ClippedLinear;
                    return;
                }

                int lastGridRow = (_count - 1) / _columns;
                _firstColumn = FirstIntersecting(_clip.X, grid.Bounds.X, grid.SlotWidth, strideX, _columns - 1);
                _lastColumn = LastIntersecting(_clip.Right, grid.Bounds.X, strideX, _columns - 1);
                _row = FirstIntersecting(_clip.Y, grid.Bounds.Y, grid.SlotHeight, strideY, lastGridRow);
                _lastRow = LastIntersecting(_clip.Bottom, grid.Bounds.Y, strideY, lastGridRow);
                _column = _firstColumn - 1;
                if (_firstColumn <= _lastColumn && _row <= _lastRow) _mode = ClippedWindow;
            }

            public SlotGridCandidate Current { get; private set; }

            public bool MoveNext()
            {
                if (_mode == Linear)
                {
                    if (_nextIndex >= _count) return false;
                    int index = _nextIndex++;
                    Rect slot = _grid.SlotRect(index);
                    Current = new SlotGridCandidate(index, slot, slot);
                    return true;
                }

                if (_mode == ClippedLinear)
                {
                    while (_nextIndex < _count)
                    {
                        int index = _nextIndex++;
                        Rect slot = _grid.SlotRect(index);
                        if (!TryIntersect(slot, _clip, out Rect visible)) continue;
                        Current = new SlotGridCandidate(index, slot, visible);
                        return true;
                    }
                    return false;
                }

                if (_mode != ClippedWindow) return false;
                while (_row <= _lastRow)
                {
                    _column++;
                    if (_column > _lastColumn)
                    {
                        _row++;
                        _column = _firstColumn;
                    }
                    if (_row > _lastRow) return false;

                    int index = _row * _columns + _column;
                    if (index >= _count)
                    {
                        _column = _lastColumn;
                        continue;
                    }

                    Rect slot = _grid.SlotRect(index);
                    if (!TryIntersect(slot, _clip, out Rect visible)) continue;
                    Current = new SlotGridCandidate(index, slot, visible);
                    return true;
                }
                return false;
            }

            static int FirstIntersecting(float clipStart, float origin, float slotSize, float stride, int maximum)
            {
                double value = Math.Floor(((double)clipStart - origin - slotSize) / stride) + 1d;
                if (value <= 0d) return 0;
                return value >= maximum ? maximum : (int)value;
            }

            static int LastIntersecting(float clipEnd, float origin, float stride, int maximum)
            {
                double value = Math.Ceiling(((double)clipEnd - origin) / stride) - 1d;
                if (value <= 0d) return 0;
                return value >= maximum ? maximum : (int)value;
            }
        }
    }
}
