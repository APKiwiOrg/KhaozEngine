using System;
using System.Collections.Generic;

namespace KhaozEngine.Windowing
{
    /// <summary>A connected monitor's identity and bounds in virtual-desktop (window) coordinates.
    /// Silk-free plain data so placement math is headless-testable; <see cref="AppWindow"/> builds
    /// these from Silk's monitor enumeration.</summary>
    public readonly record struct MonitorInfo(int Index, string Name, int X, int Y, int Width, int Height)
    {
        /// <summary>X of the monitor centre.</summary>
        public int CenterX => X + Width / 2;
        /// <summary>Y of the monitor centre.</summary>
        public int CenterY => Y + Height / 2;
    }

    /// <summary>
    /// Pure window-placement policy: which monitor a window rect belongs to, where to centre a window
    /// on a monitor, and how to clamp a window back on-screen. No Silk / GPU access (mirrors
    /// <see cref="WindowModePlanner"/>), so it is fully headless-testable. <see cref="AppWindow"/>
    /// builds the <see cref="MonitorInfo"/> list from Silk and delegates all geometry here.
    /// </summary>
    public static class WindowPlacement
    {
        /// <summary>A window must keep at least this many points visible on both axes (or its whole
        /// extent, when smaller) to count as adequately on-screen.</summary>
        const int MinVisible = 48;

        /// <summary>The monitor a window rect belongs to: the one containing the window centre, else
        /// the one it overlaps most, else the nearest by centre distance. Returns -1 when
        /// <paramref name="monitors"/> is empty (headless / no display).</summary>
        public static int MonitorIndexFor(int wx, int wy, int ww, int wh, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return -1;
            int cx = wx + ww / 2, cy = wy + wh / 2;

            int bestOverlap = -1; long bestOverlapArea = 0;
            int nearest = 0; long nearestDist = long.MaxValue;
            for (int i = 0; i < monitors.Count; i++)
            {
                MonitorInfo m = monitors[i];
                if (cx >= m.X && cx < m.X + m.Width && cy >= m.Y && cy < m.Y + m.Height)
                    return i; // centre containment wins outright

                long area = OverlapArea(wx, wy, ww, wh, m);
                if (area > bestOverlapArea) { bestOverlapArea = area; bestOverlap = i; }

                long dx = cx - m.CenterX, dy = cy - m.CenterY, dist = dx * dx + dy * dy;
                if (dist < nearestDist) { nearestDist = dist; nearest = i; }
            }
            return bestOverlapArea > 0 ? bestOverlap : nearest;
        }

        /// <summary>The window top-left that centres a <paramref name="ww"/> x <paramref name="wh"/>
        /// window on <paramref name="m"/>.</summary>
        public static (int X, int Y) CenterOn(MonitorInfo m, int ww, int wh)
            => (m.X + (m.Width - ww) / 2, m.Y + (m.Height - wh) / 2);

        /// <summary>Clamp a window rect back on-screen. When the window already keeps at least
        /// <see cref="MinVisible"/> points visible on both axes it is returned unchanged; otherwise it
        /// is relocated onto its best monitor (greatest overlap, else nearest centre) with the top-left
        /// clamped so the window sits inside that monitor, or at the monitor origin when the window is
        /// larger than the monitor. Position only (never resizes). Returns the input unchanged when
        /// <paramref name="monitors"/> is empty.</summary>
        public static (int X, int Y) ClampVisible(int wx, int wy, int ww, int wh, IReadOnlyList<MonitorInfo> monitors)
        {
            if (monitors == null || monitors.Count == 0) return (wx, wy);

            int target = MonitorIndexFor(wx, wy, ww, wh, monitors);
            MonitorInfo m = monitors[target < 0 ? 0 : target];

            int visW = OverlapLength(wx, ww, m.X, m.Width);
            int visH = OverlapLength(wy, wh, m.Y, m.Height);
            if (visW >= Math.Min(MinVisible, ww) && visH >= Math.Min(MinVisible, wh))
                return (wx, wy);

            return (ClampAxis(wx, ww, m.X, m.Width), ClampAxis(wy, wh, m.Y, m.Height));
        }

        static long OverlapArea(int wx, int wy, int ww, int wh, MonitorInfo m)
            => (long)OverlapLength(wx, ww, m.X, m.Width) * OverlapLength(wy, wh, m.Y, m.Height);

        static int OverlapLength(int aStart, int aLen, int bStart, int bLen)
        {
            int lo = Math.Max(aStart, bStart), hi = Math.Min(aStart + aLen, bStart + bLen);
            return Math.Max(0, hi - lo);
        }

        // Clamp a window start so a wLen-long window sits inside [mStart, mStart+mLen); pin to the
        // monitor origin (title bar visible) when the window is larger than the monitor.
        static int ClampAxis(int wStart, int wLen, int mStart, int mLen)
            => wLen >= mLen ? mStart : Math.Clamp(wStart, mStart, mStart + mLen - wLen);
    }
}
