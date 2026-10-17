using System;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.Imaging;

namespace KhaozEngine.Tests.BugReports.Client;

/// <summary>A scripted capture: a six frame clip of real JPEGs and a 64 by 32 PNG screenshot unless told otherwise.</summary>
internal sealed class FakeCapture : IBugReportCapture
{
    internal const int ClipWidth = 256;
    internal const int ClipHeight = 144;
    internal const int ShotWidth = 64;
    internal const int ShotHeight = 32;

    public bool ClipAvailable { get; set; } = true;

    internal ReportClip Clip { get; set; } = DefaultClip();

    internal Func<Task<ReportImage>> Screenshot { get; set; } = () => Task.FromResult(DefaultScreenshot());

    internal int FreezeCount { get; private set; }

    internal int ThawCount { get; private set; }

    internal int ScreenshotCount { get; private set; }

    public ReportClip? FreezeClip()
    {
        FreezeCount++;
        return ClipAvailable ? Clip : null;
    }

    /// <summary>When set, <see cref="Thaw"/> counts the call and then throws it.</summary>
    internal Exception? ThawFailure { get; set; }

    public void Thaw()
    {
        ThawCount++;
        if (ThawFailure is { } failure) throw failure;
    }

    public Task<ReportImage> CaptureScreenshotAsync()
    {
        ScreenshotCount++;
        return Screenshot();
    }

    internal static ReportImage DefaultScreenshot() =>
        new("image/png", ShotWidth, ShotHeight, PngWriter.Encode(Solid(ShotWidth, ShotHeight, 200, 40, 10), ShotWidth, ShotHeight));

    /// <summary>A PNG whose header reads 64 by 32 and whose pixel data is cut off, so only a decoder notices.</summary>
    internal static ReportImage CorruptScreenshot()
    {
        byte[] png = DefaultScreenshot().Bytes.ToArray();
        return new ReportImage("image/png", ShotWidth, ShotHeight, png.AsMemory(0, 40));
    }

    internal static ReportClip DefaultClip(int frames = 6)
    {
        byte[] jpeg = JpegWriter.Encode(Solid(ClipWidth, ClipHeight, 90, 160, 30), ClipWidth, ClipHeight, 80);
        var list = new ReportClipFrame[frames];
        for (int i = 0; i < frames; i++)
            list[i] = new ReportClipFrame(TimeSpan.FromMilliseconds(83 * i), 16.5, ClipWidth, ClipHeight, jpeg);
        return new ReportClip(list);
    }

    static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = r;
            rgba[i + 1] = g;
            rgba[i + 2] = b;
            rgba[i + 3] = 255;
        }
        return rgba;
    }
}
