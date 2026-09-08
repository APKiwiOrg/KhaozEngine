using System;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>One Direct3D 11 scissor rectangle in native left, top, right, bottom form.</summary>
    internal readonly record struct D3D11ScissorRect(int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// The complete scissor array bound on the device context. Direct3D 11 always binds a prefix starting at
    /// slot zero, so changing slot N returns every retained rectangle from zero through N for one reissue.
    /// </summary>
    internal sealed class D3D11ScissorState
    {
        internal const int MaximumCount = 16;

        readonly D3D11ScissorRect[] _rects = new D3D11ScissorRect[MaximumCount];
        int _count;

        internal ReadOnlySpan<D3D11ScissorRect> Set(
            uint index, uint x, uint y, uint width, uint height)
        {
            if (index >= MaximumCount)
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    $"Direct3D 11 supports {MaximumCount} scissor rectangles, indexed 0 through "
                    + $"{MaximumCount - 1}.");

            _rects[index] = new D3D11ScissorRect(
                (int)x, (int)y, (int)(x + width), (int)(y + height));
            _count = Math.Max(_count, (int)index + 1);
            return _rects.AsSpan(0, _count);
        }

        internal ReadOnlySpan<D3D11ScissorRect> SetFull(uint width, uint height)
        {
            var full = new D3D11ScissorRect(0, 0, (int)width, (int)height);
            Array.Fill(_rects, full);
            _count = 1;
            return _rects.AsSpan(0, _count);
        }

        internal void Reset()
        {
            Array.Clear(_rects);
            _count = 0;
        }

        internal static string Describe(ReadOnlySpan<D3D11ScissorRect> rects)
        {
            var text = new StringBuilder("count:");
            text.Append(rects.Length.ToString(CultureInfo.InvariantCulture));
            foreach (ref readonly D3D11ScissorRect rect in rects)
            {
                text.Append(",[")
                    .Append(rect.Left.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(rect.Top.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(rect.Right.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(rect.Bottom.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            return text.ToString();
        }
    }
}
