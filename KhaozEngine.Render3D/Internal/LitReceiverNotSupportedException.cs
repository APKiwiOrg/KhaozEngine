using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of LitReceiver input or state.</summary>
public sealed class LitReceiverNotSupportedException : NotSupportedException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public LitReceiverNotSupportedException(LitReceiverRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public LitReceiverNotSupportedException(LitReceiverRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or state was refused.</summary>
    public LitReceiverRefusal Reason { get; }

    /// <summary>The fragment storage-buffer count required by lit receivers.</summary>
    public int RequiredCount { get; init; }

    /// <summary>The fragment storage-buffer count the device reported.</summary>
    public int AvailableCount { get; init; }

    /// <summary>The native capability name for the refused device limit.</summary>
    public string CapabilityName { get; init; } = string.Empty;
}
