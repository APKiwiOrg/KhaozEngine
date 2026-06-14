using System;

namespace PixelLabSheetAssembler;

/// <summary>Raised for any expected, user-facing assembly failure (bad input, missing dir, strict gap).</summary>
public sealed class AssemblyException : Exception
{
    public AssemblyException(string message) : base(message) { }
}
