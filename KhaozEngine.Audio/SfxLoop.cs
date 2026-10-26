using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Audio;

/// <summary>A request for a protected, explicitly stopped sound effect.</summary>
public readonly record struct SfxLoopRequest(string Key, string? Bus = null,
    float Volume = 1f, float Pitch = 1f, Vector3? Position = null);

/// <summary>The outcome of a protected loop admission. The default value denotes failure.</summary>
public enum SfxLoopStartStatus { BackendFailure = 0, Started, Capacity, NotLoaded, Unsupported, Disposed }

/// <summary>A backend slot identity. Generation zero is never admitted.</summary>
public readonly record struct SfxLoopBackendHandle(int Slot, ulong Generation);

/// <summary>Resolved playback parameters passed to a loop-capable backend.</summary>
public readonly record struct SfxLoopPlayback(int Buffer, float Gain, float Pitch,
    bool Positional, Vector3 Position, SfxAttenuation Attenuation);

/// <summary>A backend admission result. Failures carry an empty handle.</summary>
public readonly record struct SfxLoopBackendStartResult(SfxLoopStartStatus Status, SfxLoopBackendHandle Handle);

/// <summary>An opaque loop identity scoped to its admitting mixer.</summary>
public readonly struct SfxLoopHandle : IEquatable<SfxLoopHandle>
{
    internal SfxLoopHandle(object owner, SfxLoopBackendHandle backendHandle)
    {
        Owner = owner;
        BackendHandle = backendHandle;
    }

    internal object? Owner { get; }
    internal SfxLoopBackendHandle BackendHandle { get; }

    public bool Equals(SfxLoopHandle other) =>
        ReferenceEquals(Owner, other.Owner) && BackendHandle == other.BackendHandle;

    public override bool Equals(object? obj) => obj is SfxLoopHandle other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(Owner is null ? 0 : RuntimeHelpers.GetHashCode(Owner), BackendHandle);

    public static bool operator ==(SfxLoopHandle left, SfxLoopHandle right) => left.Equals(right);
    public static bool operator !=(SfxLoopHandle left, SfxLoopHandle right) => !left.Equals(right);
}

/// <summary>A mixer admission result. Its default value is failure with an empty identity.</summary>
public readonly struct SfxLoopStartResult
{
    internal SfxLoopStartResult(SfxLoopStartStatus status, SfxLoopHandle handle)
    {
        Status = status;
        Handle = handle;
    }

    public SfxLoopStartStatus Status { get; }
    public SfxLoopHandle Handle { get; }
}
