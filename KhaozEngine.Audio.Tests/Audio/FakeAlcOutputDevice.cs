using System.Collections.Generic;
using KhaozEngine.Audio;
using Silk.NET.OpenAL;

namespace KhaozEngine.Tests;

/// <summary>
/// Scriptable <see cref="IAlcOutputDevice"/> for headless <see cref="AudioDeviceFollower"/> tests. The fields are the
/// state a real device would report, and the counters record what the follower asked for.
/// </summary>
internal sealed class FakeAlcOutputDevice : IAlcOutputDevice
{
    public bool CanReopen { get; set; } = true;
    public bool IsConnected { get; set; } = true;
    public string? CurrentDeviceName { get; set; } = "Speakers";

    /// <summary>What the next probe reports as the system default.</summary>
    public string? DefaultDeviceName { get; set; } = "Speakers";

    /// <summary>Outcomes for the next reopens, consumed in order. Once empty, every reopen succeeds.</summary>
    public Queue<ContextError> ReopenFailures { get; } = new();

    public int ProbeCount { get; private set; }
    public int ReopenCalls { get; private set; }

    public string? ProbeDefaultDeviceName()
    {
        ProbeCount++;
        return DefaultDeviceName;
    }

    public bool TryReopen(out ContextError error)
    {
        ReopenCalls++;
        if (ReopenFailures.TryDequeue(out error)) return false;
        error = ContextError.NoError;
        // A real reopen lands on the default output and comes back connected.
        CurrentDeviceName = DefaultDeviceName;
        IsConnected = true;
        return true;
    }
}
