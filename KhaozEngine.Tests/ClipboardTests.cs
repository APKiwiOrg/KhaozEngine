using System;
using KhaozEngine.Platform;
using Xunit;

namespace KhaozEngine.Tests;

// The native clipboard backends (SDL2 / Windows GDI / macOS NSPasteboard / mobile bridge) cannot run
// headless. These tests drive the pure dispatch/fallback spine with fake backends, exercise the pure
// CF_DIB packing, and cover the public input guards that short-circuit before any native call.
public class ClipboardTests
{
    private static ClipboardInterop.TryGetTextBackend GetReturning(bool result, string text)
        => (out string t) => { t = text; return result; };

    private static readonly ClipboardInterop.TryGetTextBackend GetNotCalled =
        (out string t) => throw new Xunit.Sdk.XunitException("backend should not have been consulted");

    // ---- DispatchGetText ----

    [Fact]
    public void GetText_returns_sdl_result_without_consulting_fallbacks()
    {
        string result = ClipboardInterop.DispatchGetText(
            () => (true, "from-sdl"),
            isMacOs: true, GetNotCalled,
            isMobile: true, GetNotCalled);

        Assert.Equal("from-sdl", result);
    }

    [Fact]
    public void GetText_returns_empty_sdl_value_when_sdl_produced_an_empty_string()
    {
        // SDL "produced" a value (non-null pointer) that happens to be empty: that wins, no fallback.
        string result = ClipboardInterop.DispatchGetText(
            () => (true, string.Empty),
            isMacOs: true, GetNotCalled,
            isMobile: true, GetNotCalled);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void GetText_falls_back_to_macos_when_sdl_did_not_produce()
    {
        string result = ClipboardInterop.DispatchGetText(
            () => (false, string.Empty),
            isMacOs: true, GetReturning(true, "from-mac"),
            isMobile: true, GetNotCalled);

        Assert.Equal("from-mac", result);
    }

    [Fact]
    public void GetText_skips_macos_when_not_macos_and_uses_mobile()
    {
        string result = ClipboardInterop.DispatchGetText(
            () => (false, string.Empty),
            isMacOs: false, GetNotCalled,
            isMobile: true, GetReturning(true, "from-mobile"));

        Assert.Equal("from-mobile", result);
    }

    [Fact]
    public void GetText_falls_through_to_mobile_when_macos_backend_fails()
    {
        string result = ClipboardInterop.DispatchGetText(
            () => (false, string.Empty),
            isMacOs: true, GetReturning(false, "ignored"),
            isMobile: true, GetReturning(true, "from-mobile"));

        Assert.Equal("from-mobile", result);
    }

    [Fact]
    public void GetText_returns_empty_when_no_backend_produces()
    {
        string result = ClipboardInterop.DispatchGetText(
            () => (false, string.Empty),
            isMacOs: false, GetNotCalled,
            isMobile: false, GetNotCalled);

        Assert.Equal(string.Empty, result);
    }

    // ---- DispatchSetText ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SetText_returns_false_for_null_or_empty_without_touching_backends(string? text)
    {
        bool result = ClipboardInterop.DispatchSetText(
            text,
            () => throw new Xunit.Sdk.XunitException("SDL must not be called for empty input"),
            isMacOs: true, _ => throw new Xunit.Sdk.XunitException("macOS must not be called"),
            isMobile: true, _ => throw new Xunit.Sdk.XunitException("mobile must not be called"));

        Assert.False(result);
    }

    [Fact]
    public void SetText_returns_true_when_sdl_succeeds_without_fallback()
    {
        bool result = ClipboardInterop.DispatchSetText(
            "hello",
            () => 0,
            isMacOs: true, _ => throw new Xunit.Sdk.XunitException("macOS must not be called"),
            isMobile: true, _ => throw new Xunit.Sdk.XunitException("mobile must not be called"));

        Assert.True(result);
    }

    [Fact]
    public void SetText_falls_back_to_macos_when_sdl_returns_nonzero()
    {
        string? captured = null;
        bool result = ClipboardInterop.DispatchSetText(
            "hello",
            () => 1,
            isMacOs: true, t => { captured = t; return true; },
            isMobile: true, _ => throw new Xunit.Sdk.XunitException("mobile must not be called"));

        Assert.True(result);
        Assert.Equal("hello", captured);
    }

    [Fact]
    public void SetText_falls_back_when_sdl_throws()
    {
        bool result = ClipboardInterop.DispatchSetText(
            "hello",
            () => throw new DllNotFoundException("no SDL"),
            isMacOs: false, _ => throw new Xunit.Sdk.XunitException("macOS must not be called"),
            isMobile: true, _ => true);

        Assert.True(result);
    }

    [Fact]
    public void SetText_returns_false_when_no_platform_backend_applies()
    {
        bool result = ClipboardInterop.DispatchSetText(
            "hello",
            () => 1,
            isMacOs: false, _ => throw new Xunit.Sdk.XunitException("macOS must not be called"),
            isMobile: false, _ => throw new Xunit.Sdk.XunitException("mobile must not be called"));

        Assert.False(result);
    }

    // ---- DispatchSetImagePng ----

    [Fact]
    public void SetImagePng_returns_false_for_null_or_empty()
    {
        Assert.False(ClipboardInterop.DispatchSetImagePng(null!, true, _ => true, true, _ => true));
        Assert.False(ClipboardInterop.DispatchSetImagePng(Array.Empty<byte>(), true, _ => true, true, _ => true));
    }

    [Fact]
    public void SetImagePng_prefers_macos_then_mobile_then_false()
    {
        byte[] png = { 1, 2, 3 };
        Assert.True(ClipboardInterop.DispatchSetImagePng(png, isMacOs: true, _ => true, isMobile: true, _ => throw new Xunit.Sdk.XunitException("mobile must not be called")));
        Assert.True(ClipboardInterop.DispatchSetImagePng(png, isMacOs: false, _ => throw new Xunit.Sdk.XunitException("macOS must not be called"), isMobile: true, _ => true));
        Assert.False(ClipboardInterop.DispatchSetImagePng(png, isMacOs: false, _ => true, isMobile: false, _ => true));
    }

    // ---- DispatchSetImageRgba32 ----

    [Theory]
    [InlineData(0, 1, 4)]
    [InlineData(1, 0, 4)]
    [InlineData(1, 1, 3)]   // length != w*h*4
    [InlineData(2, 2, 4)]   // length != w*h*4
    public void SetImageRgba32_rejects_bad_dimensions_or_length(int width, int height, int length)
    {
        bool result = ClipboardInterop.DispatchSetImageRgba32(
            width, height, new byte[length],
            isWindows: true, (_, _, _) => throw new Xunit.Sdk.XunitException("windows backend must not be called for invalid input"));

        Assert.False(result);
    }

    [Fact]
    public void SetImageRgba32_returns_false_off_windows_without_calling_backend()
    {
        bool result = ClipboardInterop.DispatchSetImageRgba32(
            1, 1, new byte[4],
            isWindows: false, (_, _, _) => throw new Xunit.Sdk.XunitException("windows backend must not be called off Windows"));

        Assert.False(result);
    }

    [Fact]
    public void SetImageRgba32_invokes_windows_backend_for_valid_input()
    {
        bool called = false;
        bool result = ClipboardInterop.DispatchSetImageRgba32(
            1, 1, new byte[4],
            isWindows: true, (_, _, _) => { called = true; return true; });

        Assert.True(called);
        Assert.True(result);
    }

    // ---- BuildWindowsDib ----

    [Fact]
    public void BuildWindowsDib_writes_header_and_swaps_rgba_to_bgra()
    {
        // 1x1, RGBA = (R=10, G=20, B=30, A=40).
        byte[] dib = ClipboardInterop.BuildWindowsDib(1, 1, new byte[] { 10, 20, 30, 40 });

        Assert.Equal(40 + 4, dib.Length);
        Assert.Equal(40, ReadInt32(dib, 0));   // biSize
        Assert.Equal(1, ReadInt32(dib, 4));    // biWidth
        Assert.Equal(1, ReadInt32(dib, 8));    // biHeight
        Assert.Equal(1, ReadInt16(dib, 12));   // biPlanes
        Assert.Equal(32, ReadInt16(dib, 14));  // biBitCount
        Assert.Equal(0, ReadInt32(dib, 16));   // BI_RGB
        Assert.Equal(4, ReadInt32(dib, 20));   // biSizeImage

        // Pixel is BGRA.
        Assert.Equal(30, dib[40]); // B
        Assert.Equal(20, dib[41]); // G
        Assert.Equal(10, dib[42]); // R
        Assert.Equal(40, dib[43]); // A
    }

    [Fact]
    public void BuildWindowsDib_flips_rows_bottom_up()
    {
        // 1x2 (width 1, height 2). Top-down source: row0 then row1.
        // R channel marks the row so we can see the flip: row0 R=100, row1 R=200.
        byte[] rgba =
        {
            100, 0, 0, 255, // row 0 (top)
            200, 0, 0, 255, // row 1 (bottom)
        };

        byte[] dib = ClipboardInterop.BuildWindowsDib(1, 2, rgba);

        // CF_DIB is bottom-up: the first stored row is the source's bottom row (row1, R=200).
        Assert.Equal(200, dib[42]); // first stored pixel, R channel
        Assert.Equal(100, dib[46]); // second stored pixel, R channel
    }

    // ---- Public facade guards (short-circuit before any native call) ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clipboard_TrySetClipboardText_returns_false_for_null_or_empty(string? text)
    {
        Assert.False(Clipboard.TrySetClipboardText(text));
    }

    [Fact]
    public void Clipboard_TrySetClipboardImagePng_returns_false_for_null_or_empty()
    {
        Assert.False(Clipboard.TrySetClipboardImagePng(null!));
        Assert.False(Clipboard.TrySetClipboardImagePng(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(0, 1, 4)]
    [InlineData(1, 1, 3)]
    public void Clipboard_TrySetClipboardImageRgba32_returns_false_for_invalid_input(int width, int height, int length)
    {
        Assert.False(Clipboard.TrySetClipboardImageRgba32(width, height, new byte[length]));
    }

    [Fact]
    public void Clipboard_MobileBridgeTypeName_roundtrips()
    {
        string? original = Clipboard.MobileBridgeTypeName;
        try
        {
            Clipboard.MobileBridgeTypeName = "MyGame.Platform.MobileClipboardBridge";
            Assert.Equal("MyGame.Platform.MobileClipboardBridge", Clipboard.MobileBridgeTypeName);

            Clipboard.MobileBridgeTypeName = null;
            Assert.Null(Clipboard.MobileBridgeTypeName);
        }
        finally
        {
            Clipboard.MobileBridgeTypeName = original;
        }
    }

    private static int ReadInt32(byte[] b, int o)
        => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static int ReadInt16(byte[] b, int o)
        => b[o] | (b[o + 1] << 8);
}
