namespace PixelLabSheetAssembler;

/// <summary>Stable reasons the tool refuses an export or an animation.</summary>
public enum AssemblyRefusal
{
    /// <summary>The input is neither an existing directory nor an existing <c>.zip</c> file.
    /// <see cref="AssemblyException.Path"/> names the input.</summary>
    InputNotFound,
    /// <summary>No <c>metadata.json</c> exists anywhere under the input. <see cref="AssemblyException.Path"/>
    /// names the input.</summary>
    MetadataNotFound,
    /// <summary>The export has no animation of the requested name. <see cref="AssemblyException.Animation"/>
    /// names the request and <see cref="AssemblyException.AvailableAnimations"/> lists what the export has.</summary>
    AnimationNotFound,
    /// <summary>A listed frame file name does not end in its frame index. <see cref="AssemblyException.Path"/>
    /// names the frame as metadata lists it.</summary>
    FrameIndexUnparsable,
    /// <summary>A direction has no present frame for the animation. <see cref="AssemblyException.Direction"/>
    /// and <see cref="AssemblyException.Animation"/> name it.</summary>
    DirectionEmpty,
    /// <summary>Strict mode found a gap. <see cref="AssemblyException.FrameIndex"/> is the first missing index
    /// of <see cref="AssemblyException.Direction"/>.</summary>
    FrameMissing,
    /// <summary>The animation lacks one of the eight directions or has no frame in it.
    /// <see cref="AssemblyException.Direction"/> names the first such direction.</summary>
    DirectionMissing,
    /// <summary>Every direction is present but no frame has a nonnegative index, so the sheet has no column.</summary>
    NoFrames
}
