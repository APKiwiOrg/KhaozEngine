using System;
using System.Globalization;
using KhaozEngine.TileWorld;

namespace KhaozEngine.TileEdit.Tools;

/// <summary>The argument checks the verb layer owns, shared by every tool class that takes one of them. These
/// live here rather than on a tool class because rotation is taken by six verbs across three files, and a check
/// copied three times is a check that will only be fixed in two of them.
///
/// <para>Each one exists because the LAYER BELOW is deliberately permissive. The command layer masks a rotation
/// with <c>and 3</c>, so a client sending 7 would get a silently different building than the one it asked for.
/// A material id is a <c>ushort</c> in the document but an unbounded JSON number on the wire, so 70000 would
/// wrap into a plausible-looking id. And <c>Enum.TryParse</c> accepts a bare number, so "1" would land as an
/// overlay shape a client never named. All three are cheap to check once, at the edge, where the message can
/// still name the argument the client actually sent.</para></summary>
internal static class ToolArgs
{
    /// <summary>The overlay shape names the <c>shape</c> argument accepts, case-insensitively.</summary>
    public const string ShapeNames = "Full, DiagonalHalf, CornerQuarter, CornerThreeQuarter";

    /// <summary>The tile setting flag names the <c>settings</c> argument accepts, comma separated.</summary>
    public const string SettingNames = "None, Blocked, Indoors, Bridge, NoDraw";

    /// <summary>A quarter-turn rotation, checked against the 0..3 range every verb description promises.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rotation is outside 0..3.</exception>
    public static int Rotation(int rotation)
    {
        // The command layer masks with "and 3", so an unchecked 7 would quietly become 3 and place the building
        // facing south when the client asked for something it thought was different.
        if ((uint)rotation > 3u)
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation,
                "rotation must be 0..3, 0 west, 1 north, 2 east, 3 south.");
        return rotation;
    }

    /// <summary>An optional quarter-turn rotation, with null meaning leave it alone.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The rotation is outside 0..3.</exception>
    public static int? Rotation(int? rotation) => rotation is { } value ? Rotation(value) : null;

    /// <summary>A catalog material id, which crosses the wire as an unbounded JSON number and is a
    /// <c>ushort</c> in the document. <paramref name="parameterName"/> is the WIRE name (underlay or overlay),
    /// so the error names something the client actually sent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The id is outside 0..65535.</exception>
    public static ushort? Material(int? id, string parameterName)
    {
        if (id is not { } value) return null;
        if ((uint)value > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, value,
                string.Create(CultureInfo.InvariantCulture,
                    $"a material id must be 0..{ushort.MaxValue}."));
        return (ushort)value;
    }

    /// <summary>One overlay shape by name, case-insensitively, with null or empty meaning leave it alone.</summary>
    /// <exception cref="ArgumentException">The value is not one of <see cref="ShapeNames"/>.</exception>
    public static TileOverlayShape? Shape(string? shape)
    {
        if (shape is null) return null;
        string name = shape.Trim();
        if (name.Length == 0) return null;
        RefuseANumber(name, shape, ShapeNames, "an overlay shape", nameof(shape));
        if (!Enum.TryParse(name, ignoreCase: true, out TileOverlayShape parsed) || !Enum.IsDefined(parsed))
            throw new ArgumentException($"'{shape}' is not an overlay shape. The shapes are {ShapeNames}.",
                nameof(shape));
        return parsed;
    }

    /// <summary>A comma list of tile setting flag names OR-ed together, with null meaning leave them alone and
    /// an empty string or "none" meaning clear every flag.</summary>
    /// <exception cref="ArgumentException">A name is not one of <see cref="SettingNames"/>.</exception>
    public static TileSettings? Settings(string? settings)
    {
        if (settings is null) return null;
        string list = settings.Trim();
        if (list.Length == 0) return TileSettings.None;
        TileSettings flags = TileSettings.None;
        // One name at a time rather than handing the whole string to Enum.TryParse, so a typo names ITSELF in
        // the error instead of the whole list failing anonymously.
        foreach (string part in list.Split(','))
        {
            string name = part.Trim();
            if (name.Length == 0) continue;
            RefuseANumber(name, name, SettingNames, "a tile setting", nameof(settings));
            if (!Enum.TryParse(name, ignoreCase: true, out TileSettings parsed) || !Enum.IsDefined(parsed))
                throw new ArgumentException($"'{name}' is not a tile setting. The settings are {SettingNames}.",
                    nameof(settings));
            flags |= parsed;
        }
        return flags;
    }

    // Enum.TryParse accepts a bare number and hands back that number as the enum value, defined or not, so
    // "1" would land as DiagonalHalf and "9" as a shape the renderer has no case for. Both are wrong for the
    // same reason: the client never named a shape, and the wire contract here is names. Refused before the
    // parse so the message can say what the legal names are rather than what the number was not.
    static void RefuseANumber(string name, string original, string legal, string what, string parameterName)
    {
        if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException(
                $"'{original}' is a number, and {what} is given by NAME here. The names are {legal}.",
                parameterName);
    }
}
