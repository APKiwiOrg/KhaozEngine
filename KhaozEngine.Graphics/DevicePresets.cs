namespace KhaozEngine.Graphics;

/// <summary>
/// A named device size in portrait logical points. Call <see cref="Portrait"/> or
/// <see cref="Landscape"/> to get <see cref="DisplaySettings"/> with the matching orientations.
/// </summary>
public readonly record struct DevicePreset(string Name, int PortraitWidth, int PortraitHeight)
{
    /// <summary>Portrait settings at this preset's size.</summary>
    public DisplaySettings Portrait() => DisplaySettings.Portrait(PortraitWidth, PortraitHeight);

    /// <summary>Landscape settings: width/height swapped from the portrait size.</summary>
    public DisplaySettings Landscape() => DisplaySettings.Landscape(PortraitHeight, PortraitWidth);
}

/// <summary>
/// Common iOS device sizes in logical points (portrait). Convenience over raw width/height;
/// the plain <see cref="DisplaySettings.Landscape(int,int)"/> entry point is always available.
/// </summary>
public static class DevicePresets
{
    /// <summary>iPhone SE (2nd/3rd gen) — 375x667.</summary>
    public static readonly DevicePreset IPhoneSE = new("iPhone SE", 375, 667);

    /// <summary>iPhone 13 mini / 12 mini — 375x812.</summary>
    public static readonly DevicePreset IPhone13Mini = new("iPhone 13 mini", 375, 812);

    /// <summary>iPhone 15 / 14 / 13 — 390x844.</summary>
    public static readonly DevicePreset IPhone15 = new("iPhone 15", 390, 844);

    /// <summary>iPhone 15 Pro / 14 Pro — 393x852.</summary>
    public static readonly DevicePreset IPhone15Pro = new("iPhone 15 Pro", 393, 852);

    /// <summary>iPhone 15 Plus / 14 Plus / 13 Pro Max — 428x926.</summary>
    public static readonly DevicePreset IPhone15Plus = new("iPhone 15 Plus", 428, 926);

    /// <summary>iPhone 15 Pro Max / 14 Pro Max — 430x932 (landscape 932x430).</summary>
    public static readonly DevicePreset IPhone15ProMax = new("iPhone 15 Pro Max", 430, 932);

    /// <summary>iPad 10.2" — 810x1080.</summary>
    public static readonly DevicePreset IPad102 = new("iPad 10.2\"", 810, 1080);

    /// <summary>iPad Air / iPad Pro 11" — 834x1194.</summary>
    public static readonly DevicePreset IPadAir = new("iPad Air", 834, 1194);

    /// <summary>iPad Pro 12.9" — 1024x1366.</summary>
    public static readonly DevicePreset IPadPro129 = new("iPad Pro 12.9\"", 1024, 1366);
}
