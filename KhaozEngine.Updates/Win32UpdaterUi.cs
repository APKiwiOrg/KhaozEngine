using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

#nullable enable

namespace KhaozEngine.Updates;

/// <summary>
/// A tiny native Win32 progress window for the updater shim, drawn with GDI via P/Invoke only: no
/// WinForms/WPF, no common-controls dependency (the progress bar is an owner-drawn rectangle in
/// <c>WM_PAINT</c>, not <c>PROGRESS_CLASS</c>), and no KhaozEngine GUI/GPU stack. That keeps it
/// self-contained and trim/AOT friendly, so it fits inside a single-file trimmed shim. The window runs on
/// its own dedicated UI thread with its own message pump; the apply thread pushes updates through
/// thread-safe methods that marshal to the UI thread with <c>PostMessage</c>. Everything is best-effort:
/// any failure disables the window and degrades to a no-op, so a broken window can never fail the apply.
/// Windows-only; <see cref="SystemUpdaterUi"/> hands out <see cref="NullUpdaterUi"/> elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class Win32UpdaterUi : IUpdaterUi
{
    // There is only ever one updater window per process, so the static WndProc dispatches to this single
    // active instance. Set when the window thread starts, cleared when it exits.
    private static Win32UpdaterUi? s_active;

    private const string ClassName = "KhaozEngineUpdaterWindow";

    // Logical window size. The process is left DPI-unaware, so the OS bitmap-scales this on high-DPI
    // displays - acceptable for a transient progress window and it avoids a manifest / DPI-awareness call.
    private const int WindowWidth = 460;
    private const int WindowHeight = 210;

    private const nuint MarqueeTimerId = 1;
    private const uint MarqueeIntervalMs = 33;

    private readonly object gate = new();
    private Thread? thread;
    private readonly ManualResetEventSlim ready = new(false);

    private nint hwnd;
    private volatile bool disabled;
    private volatile bool closed;

    // Painted state (guarded by gate; read on the UI thread inside the paint handler).
    private UpdaterUiTheme theme = new();
    private UpdaterPhase phase = UpdaterPhase.Install;
    private string status = string.Empty;
    private int done;
    private int total;
    private int marquee;

    // GDI resources, all created and destroyed on the UI thread.
    private nint backgroundBrush;
    private nint trackBrush;
    private nint accentBrush;
    private nint headingFont;
    private nint statusFont;
    private nint classNamePtr;
    private nint logoBitmap;
    private int logoWidth;
    private int logoHeight;
    private nuint gdiplusToken;
    private bool marqueeRunning;

    public void Show(UpdaterUiTheme theme)
    {
        if (disabled || thread is not null)
        {
            return;
        }
        try
        {
            lock (gate)
            {
                this.theme = theme;
                status = theme.InstallingText;
            }
            thread = new Thread(RunMessageLoop)
            {
                IsBackground = true,
                Name = "ke-updater-ui",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            // Give the window thread a moment to create the HWND so the first PostMessage has a target.
            // If it never signals, we simply proceed - the updates are best-effort.
            ready.Wait(2000);
        }
        catch
        {
            disabled = true;
        }
    }

    public void SetPhase(UpdaterPhase phase)
    {
        lock (gate) { this.phase = phase; }
        // A phase change flips the bar between determinate (Install) and marquee (Finishing); let the UI
        // thread start/stop its timer accordingly, then repaint.
        Post(WmAppPhase, (nint)(int)phase);
    }

    public void SetProgress(int done, int total)
    {
        lock (gate) { this.done = done; this.total = total; }
        Post(WmAppRefresh, 0);
    }

    public void SetStatus(string status)
    {
        lock (gate) { this.status = status ?? string.Empty; }
        Post(WmAppRefresh, 0);
    }

    public void Close()
    {
        if (closed)
        {
            return;
        }
        closed = true;
        try
        {
            nint h = hwnd;
            if (h != 0)
            {
                PostMessageW(h, WmClose, 0, 0);
            }
            thread?.Join(2000);
        }
        catch
        {
            // Best-effort teardown; the OS reclaims the window when the process exits regardless.
        }
    }

    private void Post(uint message, nint wParam)
    {
        if (disabled)
        {
            return;
        }
        nint h = hwnd;
        if (h != 0)
        {
            try { PostMessageW(h, message, wParam, 0); } catch { /* window gone; ignore */ }
        }
    }

    // ---- UI thread ----

    private void RunMessageLoop()
    {
        try
        {
            s_active = this;
            if (!CreateWindow())
            {
                disabled = true;
                ready.Set();
                return;
            }
            ready.Set();

            while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch
        {
            disabled = true;
        }
        finally
        {
            ready.Set();
            s_active = null;
        }
    }

    private bool CreateWindow()
    {
        nint hInstance = GetModuleHandleW(null);
        classNamePtr = Marshal.StringToHGlobalUni(ClassName);

        UpdaterUiTheme t;
        lock (gate) { t = theme; }

        backgroundBrush = CreateSolidBrush(Rgb(t.Background));
        trackBrush = CreateSolidBrush(Rgb(Mix(t.Background, t.Text, 0.18)));
        accentBrush = CreateSolidBrush(Rgb(t.Accent));
        headingFont = CreateFontW(-22, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        statusFont = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        TryLoadLogo(t);

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = 0,
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&StaticWndProc,
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = hInstance,
            hIcon = 0,
            hCursor = LoadCursorW(0, 32512),      // IDC_ARROW
            hbrBackground = backgroundBrush,
            lpszMenuName = 0,
            lpszClassName = classNamePtr,
            hIconSm = 0,
        };
        RegisterClassExW(ref wc);   // benign if the class already exists in this process

        int screenW = GetSystemMetrics(0);        // SM_CXSCREEN
        int screenH = GetSystemMetrics(1);        // SM_CYSCREEN
        int x = screenW > 0 ? (screenW - WindowWidth) / 2 : 200;
        int y = screenH > 0 ? (screenH - WindowHeight) / 2 : 200;

        const uint wsPopup = 0x80000000;
        const uint wsVisible = 0x10000000;
        const uint exTopmost = 0x00000008;
        const uint exToolwindow = 0x00000080;

        hwnd = CreateWindowExW(
            exTopmost | exToolwindow,
            ClassName,
            t.WindowTitle,
            wsPopup | wsVisible,
            x, y, WindowWidth, WindowHeight,
            0, 0, hInstance, 0);

        if (hwnd == 0)
        {
            return false;
        }
        ShowWindow(hwnd, 5);   // SW_SHOW
        UpdateWindow(hwnd);
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint StaticWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        Win32UpdaterUi? self = s_active;
        if (self is not null)
        {
            try
            {
                if (self.HandleMessage(hwnd, msg, wParam, lParam, out nint result))
                {
                    return result;
                }
            }
            catch
            {
                // Never let a managed exception cross back into Win32; fall through to the default.
            }
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private bool HandleMessage(nint hwnd, uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        switch (msg)
        {
            case WmPaint:
                Paint(hwnd);
                return true;

            case WmAppRefresh:
                InvalidateRect(hwnd, 0, 1);
                return true;

            case WmAppPhase:
                bool finishing = (int)wParam == (int)UpdaterPhase.Finishing;
                if (finishing && !marqueeRunning)
                {
                    SetTimer(hwnd, MarqueeTimerId, MarqueeIntervalMs, 0);
                    marqueeRunning = true;
                }
                else if (!finishing && marqueeRunning)
                {
                    KillTimer(hwnd, MarqueeTimerId);
                    marqueeRunning = false;
                }
                InvalidateRect(hwnd, 0, 1);
                return true;

            case WmTimer:
                marquee = (marquee + 4) % 200;
                InvalidateRect(hwnd, 0, 1);
                return true;

            case WmEraseBkgnd:
                // Reported handled with a non-zero result so the default grey erase never flashes; the
                // paint handler fills the whole client area itself.
                result = 1;
                return true;

            case WmClose:
                DestroyWindow(hwnd);
                return true;

            case WmDestroy:
                if (marqueeRunning)
                {
                    KillTimer(hwnd, MarqueeTimerId);
                    marqueeRunning = false;
                }
                ReleaseResources();
                PostQuitMessage(0);
                return true;
        }
        return false;
    }

    private void Paint(nint hwnd)
    {
        nint hdc = BeginPaint(hwnd, out PAINTSTRUCT ps);
        if (hdc == 0)
        {
            return;
        }
        try
        {
            UpdaterUiTheme t;
            UpdaterPhase ph;
            string statusText;
            int d, tot, mq;
            lock (gate)
            {
                t = theme;
                ph = phase;
                statusText = status;
                d = done;
                tot = total;
                mq = marquee;
            }

            GetClientRect(hwnd, out RECT client);
            FillRect(hdc, ref client, backgroundBrush);

            SetBkMode(hdc, 1);          // TRANSPARENT
            uint textColor = Rgb(t.Text);

            int contentTop = 24;
            if (logoBitmap != 0 && logoWidth > 0 && logoHeight > 0)
            {
                contentTop = DrawLogo(hdc, client) + 14;
            }

            // Heading.
            SelectObject(hdc, headingFont);
            SetTextColor(hdc, textColor);
            var headingRect = new RECT { Left = 28, Top = contentTop, Right = client.Right - 28, Bottom = contentTop + 34 };
            DrawTextW(hdc, t.Heading, -1, ref headingRect, DtSingleLine | DtLeft | DtNoPrefix | DtEndEllipsis);

            // Status line.
            SelectObject(hdc, statusFont);
            SetTextColor(hdc, Rgb(Mix(t.Background, t.Text, 0.75)));
            var statusRect = new RECT { Left = 28, Top = contentTop + 42, Right = client.Right - 28, Bottom = contentTop + 84 };
            DrawTextW(hdc, statusText, -1, ref statusRect, DtWordBreak | DtLeft | DtTop | DtNoPrefix | DtEndEllipsis);

            // Progress bar.
            int barHeight = 14;
            int barY = client.Bottom - 34;
            var track = new RECT { Left = 28, Top = barY, Right = client.Right - 28, Bottom = barY + barHeight };
            FillRect(hdc, ref track, trackBrush);

            int trackW = track.Right - track.Left;
            if (ph == UpdaterPhase.Finishing)
            {
                // Indeterminate marquee: a fixed-width accent block sweeping across the track.
                int blockW = trackW / 4;
                int span = trackW + blockW;
                int pos = (int)((long)mq * span / 200) - blockW;
                int left = Math.Max(track.Left, track.Left + pos);
                int right = Math.Min(track.Right, track.Left + pos + blockW);
                if (right > left)
                {
                    var fill = new RECT { Left = left, Top = barY, Right = right, Bottom = barY + barHeight };
                    FillRect(hdc, ref fill, accentBrush);
                }
            }
            else
            {
                double fraction = tot > 0 ? (double)d / tot : 0.0;
                if (fraction < 0) fraction = 0;
                if (fraction > 1) fraction = 1;
                int fillW = (int)(trackW * fraction);
                if (fillW > 0)
                {
                    var fill = new RECT { Left = track.Left, Top = barY, Right = track.Left + fillW, Bottom = barY + barHeight };
                    FillRect(hdc, ref fill, accentBrush);
                }
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    private int DrawLogo(nint hdc, RECT client)
    {
        // Aspect-fit the logo into a top band, centered.
        int maxH = 56;
        int maxW = client.Right - 56;
        double scale = Math.Min((double)maxW / logoWidth, (double)maxH / logoHeight);
        if (scale > 1) scale = 1;
        int w = (int)(logoWidth * scale);
        int h = (int)(logoHeight * scale);
        int x = (client.Right - w) / 2;
        int y = 18;

        nint memDc = CreateCompatibleDC(hdc);
        if (memDc == 0)
        {
            return y;
        }
        nint prev = SelectObject(memDc, logoBitmap);
        SetStretchBltMode(hdc, 4);   // HALFTONE
        StretchBlt(hdc, x, y, w, h, memDc, 0, 0, logoWidth, logoHeight, 0x00CC0020); // SRCCOPY
        SelectObject(memDc, prev);
        DeleteDC(memDc);
        return y + h;
    }

    private void TryLoadLogo(UpdaterUiTheme t)
    {
        if (string.IsNullOrEmpty(t.LogoPath))
        {
            return;
        }
        try
        {
            if (!System.IO.File.Exists(t.LogoPath))
            {
                return;
            }
            var input = new GdiplusStartupInput { GdiplusVersion = 1 };
            if (GdiplusStartup(out gdiplusToken, ref input, 0) != 0)
            {
                gdiplusToken = 0;
                return;
            }
            if (GdipCreateBitmapFromFile(t.LogoPath!, out nint image) != 0 || image == 0)
            {
                return;
            }
            try
            {
                GdipGetImageWidth(image, out uint w);
                GdipGetImageHeight(image, out uint h);
                logoWidth = (int)w;
                logoHeight = (int)h;
                // Composite transparency against the panel background so a plain BitBlt looks correct
                // (no per-pixel alpha needed at paint time).
                uint bg = 0xFF000000u | ((uint)t.Background.R << 16) | ((uint)t.Background.G << 8) | t.Background.B;
                if (GdipCreateHBITMAPFromBitmap(image, out nint hbitmap, bg) == 0)
                {
                    logoBitmap = hbitmap;
                }
            }
            finally
            {
                GdipDisposeImage(image);
            }
        }
        catch
        {
            // No logo; the window renders fine without it.
            logoBitmap = 0;
        }
    }

    private void ReleaseResources()
    {
        try
        {
            if (logoBitmap != 0) { DeleteObject(logoBitmap); logoBitmap = 0; }
            if (backgroundBrush != 0) { DeleteObject(backgroundBrush); backgroundBrush = 0; }
            if (trackBrush != 0) { DeleteObject(trackBrush); trackBrush = 0; }
            if (accentBrush != 0) { DeleteObject(accentBrush); accentBrush = 0; }
            if (headingFont != 0) { DeleteObject(headingFont); headingFont = 0; }
            if (statusFont != 0) { DeleteObject(statusFont); statusFont = 0; }
            if (classNamePtr != 0) { Marshal.FreeHGlobal(classNamePtr); classNamePtr = 0; }
            if (gdiplusToken != 0) { GdiplusShutdown(gdiplusToken); gdiplusToken = 0; }
        }
        catch
        {
            // Teardown is best-effort; the process is exiting immediately after in the shim.
        }
    }

    private static uint Rgb((byte R, byte G, byte B) c) => (uint)(c.R | (c.G << 8) | (c.B << 16));

    private static (byte R, byte G, byte B) Mix((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double t)
    {
        byte L(byte x, byte y) => (byte)(x + (y - x) * t);
        return (L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    // ---- Win32 message + draw constants ----

    private const uint WmDestroy = 0x0002;
    private const uint WmPaint = 0x000F;
    private const uint WmClose = 0x0010;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmTimer = 0x0113;
    private const uint WmApp = 0x8000;
    private const uint WmAppRefresh = WmApp + 1;
    private const uint WmAppPhase = WmApp + 2;

    private const uint DtLeft = 0x0000;
    private const uint DtTop = 0x0000;
    private const uint DtWordBreak = 0x0010;
    private const uint DtSingleLine = 0x0020;
    private const uint DtNoPrefix = 0x0800;
    private const uint DtEndEllipsis = 0x8000;

    // ---- Structs ----

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        private fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GdiplusStartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    // ---- P/Invoke (source-generated, trim/AOT-safe) ----

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll")]
    private static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial int ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    private static partial int UpdateWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial int PostMessageW(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial void PostQuitMessage(int nExitCode);

    [LibraryImport("user32.dll")]
    private static partial int DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint BeginPaint(nint hWnd, out PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll")]
    private static partial int EndPaint(nint hWnd, ref PAINTSTRUCT lpPaint);

    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    private static partial int FillRect(nint hDC, ref RECT lprc, nint hbr);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial int InvalidateRect(nint hWnd, nint lpRect, int bErase);

    [LibraryImport("user32.dll")]
    private static partial nuint SetTimer(nint hWnd, nuint nIDEvent, uint uElapse, nint lpTimerFunc);

    [LibraryImport("user32.dll")]
    private static partial int KillTimer(nint hWnd, nuint uIDEvent);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int DrawTextW(nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    [LibraryImport("user32.dll")]
    private static partial nint LoadCursorW(nint hInstance, nint lpCursorName);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint ho);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision,
        uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint hdc, nint h);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteDC(nint hdc);

    [LibraryImport("gdi32.dll")]
    private static partial int SetStretchBltMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial int StretchBlt(
        nint hdcDest, int xDest, int yDest, int wDest, int hDest,
        nint hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(out nuint token, ref GdiplusStartupInput input, nint output);

    [LibraryImport("gdiplus.dll")]
    private static partial void GdiplusShutdown(nuint token);

    [LibraryImport("gdiplus.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GdipCreateBitmapFromFile(string filename, out nint bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageWidth(nint image, out uint width);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageHeight(nint image, out uint height);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateHBITMAPFromBitmap(nint bitmap, out nint hbmReturn, uint background);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImage(nint image);
}
