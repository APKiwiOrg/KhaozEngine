using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>One loaded frame: its parsed frame_NNN index and pixels.</summary>
public sealed record FrameEntry(int Index, Image<Rgba32> Image);

/// <summary>A character's single animation: per PixelLab direction name, the present frames (any order).</summary>
public sealed record CharacterAnimation(
    string CharacterName,
    string AnimName,
    IReadOnlyDictionary<string, IReadOnlyList<FrameEntry>> FramesByDir);

/// <summary>Assembly tuning. Defaults match the CLI defaults.</summary>
public sealed record AssemblyOptions(
    int BottomPad = 0,
    int AlphaThreshold = 0,
    bool Strict = false,
    float SuggestedFps = 10f);

/// <summary>Result of assembly: the composited sheet plus the values the consumer needs.</summary>
public sealed record AssemblyResult(
    Image<Rgba32> Sheet,
    int FrameCount,
    float SuggestedFps,
    IReadOnlyList<string> Warnings,
    int CellWidth,
    int CellHeight);
