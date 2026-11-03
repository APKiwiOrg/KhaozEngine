using System;
using System.Collections.Generic;

namespace PixelLabSheetAssembler;

/// <summary>Raised for any expected, user-facing assembly failure (bad input, missing dir, strict gap).</summary>
public sealed class AssemblyException : Exception
{
    public AssemblyException(string message) : base(message) { }

    /// <summary>Create a refusal with its stable <paramref name="reason"/> and a developer-facing
    /// <paramref name="message"/>.</summary>
    public AssemblyException(AssemblyRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal with its stable <paramref name="reason"/>, a developer-facing
    /// <paramref name="message"/> and the failure that caused it.</summary>
    public AssemblyException(AssemblyRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>Why the tool refused. Null only for an instance built through the message-only constructor by code
    /// outside the tool.</summary>
    public AssemblyRefusal? Reason { get; }

    /// <summary>The animation the refusal concerns, when it concerns one.</summary>
    public string? Animation { get; init; }

    /// <summary>The PixelLab direction name the refusal concerns, when it concerns one.</summary>
    public string? Direction { get; init; }

    /// <summary>The first missing frame index of a strict gap.</summary>
    public int? FrameIndex { get; init; }

    /// <summary>The animations the export does have, for <see cref="AssemblyRefusal.AnimationNotFound"/>.</summary>
    public IReadOnlyList<string>? AvailableAnimations { get; init; }

    /// <summary>The input or frame path the refusal concerns, when it concerns one.</summary>
    public string? Path { get; init; }
}
