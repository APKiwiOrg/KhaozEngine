using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// THE BYTE WINDOW A NON-BLOCKING STAGING READ (<c>IGpuDevice.TryReadBuffer</c>) MAY TAKE, and the copy out of
    /// it, shared by the three native backends so they refuse the same windows with the same exceptions.
    /// <para>
    /// THE OFFSET IS A MULTIPLE OF FOUR for the reason <see cref="GpuCopyAlignment"/> gives for copies: the read
    /// names the same offsets the recorded <c>CopyBuffer</c> did, and that copy already requires it on every
    /// backend. The bounds are checked by SUBTRACTION, never by adding the count to the offset, so a window near
    /// <see cref="uint.MaxValue"/> cannot wrap round into range.
    /// </para>
    /// </summary>
    internal static class GpuReadWindow
    {
        /// <summary>
        /// Refuse a window of <paramref name="count"/> bytes at <paramref name="offsetBytes"/> that is unaligned or
        /// does not fit inside a buffer of <paramref name="sizeInBytes"/>. The offset is named for an unaligned or
        /// past-the-end start, and <c>destination</c> for a window that starts inside but runs off the end.
        /// </summary>
        internal static void Require(uint sizeInBytes, uint offsetBytes, int count)
        {
            if (!GpuCopyAlignment.IsAligned(offsetBytes))
            {
                throw new ArgumentOutOfRangeException("offsetBytes", offsetBytes,
                    "A staging read was given an offset of " + Text(offsetBytes) + ", which is not a multiple of "
                    + Text(GpuCopyAlignment.Bytes) + ". It names the same bytes the recorded CopyBuffer wrote, and "
                    + "that copy requires an aligned offset on every backend.");
            }

            if (offsetBytes > sizeInBytes)
            {
                throw new ArgumentOutOfRangeException("offsetBytes", offsetBytes,
                    "A staging read was given an offset of " + Text(offsetBytes) + ", past the end of a "
                    + Text(sizeInBytes) + "-byte staging buffer.");
            }

            if ((uint)count > sizeInBytes - offsetBytes)
            {
                throw new ArgumentOutOfRangeException("destination", count,
                    "A staging read of " + count.ToString(CultureInfo.InvariantCulture) + " bytes at offset "
                    + Text(offsetBytes) + " runs past the end of a " + Text(sizeInBytes) + "-byte staging buffer. "
                    + "Size the destination to the slice the copy wrote.");
            }
        }

        /// <summary>Copy <paramref name="destination"/>'s length of bytes from <paramref name="data"/> plus
        /// <paramref name="offsetBytes"/>. The window has been through <see cref="Require"/>, and the pointer is a
        /// live CPU mapping of the whole staging buffer.</summary>
        internal static unsafe void CopyOut(IntPtr data, uint offsetBytes, Span<byte> destination)
            => new ReadOnlySpan<byte>((byte*)data + offsetBytes, destination.Length).CopyTo(destination);

        static string Text(uint value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
