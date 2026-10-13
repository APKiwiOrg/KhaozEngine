using System;
using System.Collections.Generic;

namespace KhaozEngine.Gui;

/// <summary>Immutable Pointer-space sheet policy. Snap duration is 0.18 seconds.</summary>
public sealed class BottomSheetOptions
{
    public float HandleHeight { get; init; } = 32;
    public float DragSlop { get; init; } = 8;
    public float TapMaxDuration { get; init; } = .3f;
    public float StationaryReleaseCutoff { get; init; } = .08f;
    public float WheelScrollSpeed { get; init; } = 40;
    /// <summary>Optional distinct pair of IDs, including dismissible IDs if desired. Copied on construction.
    /// Without a pair the handle toggles the lowest and highest non-dismissible detents.</summary>
    public IReadOnlyList<string>? HandleToggleDetentIds { get; init; }

    internal void Validate()
    {
        Nonnegative(HandleHeight, nameof(HandleHeight));
        Nonnegative(DragSlop, nameof(DragSlop));
        Nonnegative(TapMaxDuration, nameof(TapMaxDuration));
        Nonnegative(WheelScrollSpeed, nameof(WheelScrollSpeed));
        Nonnegative(StationaryReleaseCutoff, nameof(StationaryReleaseCutoff));
        if (StationaryReleaseCutoff == 0) throw new ArgumentOutOfRangeException(nameof(StationaryReleaseCutoff));
    }

    internal static void Nonnegative(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
}
