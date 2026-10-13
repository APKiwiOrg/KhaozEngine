using System;

namespace KhaozEngine.Gui;

/// <summary>A stable sheet identity and a caller-owned height policy in Pointer units.</summary>
public sealed class BottomSheetDetent
{
    readonly Func<float, float, float> _heightResolver;
    public string Id { get; }
    public bool Dismissible { get; }

    /// <summary>The resolver receives safe available height and desired body plus handle height.</summary>
    public BottomSheetDetent(string id, Func<float, float, float> heightResolver, bool dismissible = false)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A detent ID is required.", nameof(id));
        ArgumentNullException.ThrowIfNull(heightResolver);
        Id = id;
        _heightResolver = heightResolver;
        Dismissible = dismissible;
    }

    public float ResolveHeight(float availableHeight, float contentHeight) => _heightResolver(availableHeight, contentHeight);

    public static BottomSheetDetent Fixed(string id, float height, bool dismissible = false)
    {
        BottomSheetOptions.Nonnegative(height, nameof(height));
        return new(id, (_, _) => height, dismissible);
    }

    public static BottomSheetDetent Fraction(string id, float fraction, bool dismissible = false)
    {
        BottomSheetOptions.Nonnegative(fraction, nameof(fraction));
        return new(id, (available, _) => available * fraction, dismissible);
    }

    /// <summary>Fits body plus handle, capped at the supplied fraction of safe available height.</summary>
    public static BottomSheetDetent ContentCapped(string id, float maxFraction, bool dismissible = false)
    {
        BottomSheetOptions.Nonnegative(maxFraction, nameof(maxFraction));
        return new(id, (available, content) => MathF.Min(content, available * maxFraction), dismissible);
    }
}
