using System;
using System.Globalization;

namespace KhaozEngine.BugReports.Contracts;

public static class BundleEntryNames
{
    public const string Manifest = "manifest.json";
    public const string ScreenshotPng = "screenshot.png";
    public const string ScreenshotJpeg = "screenshot.jpg";
    public const string ClipIndex = "clip/index.json";
    public const string ClipMp4 = "clip.mp4";
    public const int MaxClipFrames = 120;

    public static bool IsValidSectionKey(string key)
    {
        if (key is null || key.Length is < 1 or > 48 || !IsLetterOrDigit(key[0])) return false;
        foreach (char c in key)
            if (!IsLetterOrDigit(c) && c is not ('.' or '_' or '-')) return false;
        return true;
    }

    public static string Section(string key)
    {
        if (!IsValidSectionKey(key))
            throw new ArgumentException("A section key must match [a-z0-9][a-z0-9._-]{0,47}.", nameof(key));
        return "sections/" + key + ".json";
    }

    public static string ClipFrame(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MaxClipFrames);
        return "clip/" + index.ToString("D3", CultureInfo.InvariantCulture) + ".jpg";
    }

    public static bool IsAllowed(string name)
    {
        if (name is Manifest or ScreenshotPng or ScreenshotJpeg or ClipIndex or ClipMp4) return true;
        if (name is null) return false;
        if (name.StartsWith("sections/", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            return IsValidSectionKey(name[9..^5]);
        if (name.Length != 12 || !name.StartsWith("clip/", StringComparison.Ordinal)
            || !name.EndsWith(".jpg", StringComparison.Ordinal)) return false;
        if (!char.IsAsciiDigit(name[5]) || !char.IsAsciiDigit(name[6]) || !char.IsAsciiDigit(name[7])) return false;
        int index = (name[5] - '0') * 100 + (name[6] - '0') * 10 + name[7] - '0';
        return index < MaxClipFrames;
    }

    static bool IsLetterOrDigit(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
}
