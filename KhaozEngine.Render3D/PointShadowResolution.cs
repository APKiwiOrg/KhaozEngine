using System;

namespace KhaozEngine.Render3D;

/// <summary>
/// What the point-light shadow atlas is ACTUALLY doing right now: whether one is allocated at all, the layout it
/// was allocated at, and whether the live layout is the one that was asked for. Read it from
/// <see cref="Scene3D.ResolvedPointShadows"/>.
/// <para>
/// The <see cref="ShadowResolution"/> precedent, one feature over: a request that the device refused must be
/// VISIBLE rather than silent, because the alternative is a settings screen reporting a quality level the frame
/// is not rendering at. <see cref="Degraded"/> with a <see cref="Reason"/> is that report, and the three live
/// values beside it are what is on the card rather than what was wanted.
/// </para>
/// <para>
/// Immutable value. A <c>default</c> instance reads as "no atlas, nothing refused", which is what a scene that
/// has never had a point-shadow request carries.
/// </para>
/// </summary>
public readonly struct PointShadowResolution : IEquatable<PointShadowResolution>
{
    readonly string? _reason;

    internal PointShadowResolution(bool enabled, int faceResolution, int maxShadowedLights, bool degraded,
        string? reason)
        : this(enabled, faceResolution, maxShadowedLights, degraded, reason,
            faceResolution > 0 && maxShadowedLights > 0
                ? 6L * faceResolution * maxShadowedLights * faceResolution * 9L : 0L,
            0, 0L)
    {
    }

    internal PointShadowResolution(bool enabled, int faceResolution, int maxShadowedLights, bool degraded,
        string? reason, long baseAtlasBytes, int transientAtlasRows, long transientAtlasBytes)
    {
        Enabled = enabled;
        FaceResolution = faceResolution;
        MaxShadowedLights = maxShadowedLights;
        Degraded = degraded;
        _reason = reason;
        BaseAtlasBytes = baseAtlasBytes;
        TransientAtlasRows = transientAtlasRows;
        TransientAtlasBytes = transientAtlasBytes;
    }

    /// <summary>Whether an atlas is live, so a light that asks can actually be given a row. False when the
    /// settings turned point shadows off, when nothing has asked for one yet, and on the first frame that asks:
    /// the allocation happens at the FRAME BOUNDARY after that frame rather than inside it.</summary>
    public bool Enabled { get; }

    /// <summary>Pixels per axis of one cube face in the LIVE atlas, or 0 when there is none.</summary>
    public int FaceResolution { get; }

    /// <summary>Rows in the LIVE atlas, which is how many lights can carry a map at once, or 0 when there is
    /// none.</summary>
    public int MaxShadowedLights { get; }

    /// <summary>True when the last requested layout could not be brought up and the values above are therefore
    /// something other than what was asked for. Stays true until a different layout is requested.</summary>
    public bool Degraded { get; }

    /// <summary>Why the request was refused, naming the layout that was asked for and what failed (empty when
    /// nothing was refused). A diagnostics and log string, not player-facing.</summary>
    public string Reason => _reason ?? "";

    /// <summary>Bytes held by the live base point-shadow atlas.</summary>
    public long BaseAtlasBytes { get; }

    /// <summary>Rows in the live transient point-shadow atlas, or zero when none is allocated.</summary>
    public int TransientAtlasRows { get; }

    /// <summary>Bytes held by the live transient point-shadow atlas.</summary>
    public long TransientAtlasBytes { get; }

    /// <summary>Total live point-shadow atlas storage in bytes.</summary>
    public long TotalAtlasBytes => BaseAtlasBytes + TransientAtlasBytes;

    public bool Equals(PointShadowResolution other) =>
        Enabled == other.Enabled && FaceResolution == other.FaceResolution
        && MaxShadowedLights == other.MaxShadowedLights && Degraded == other.Degraded
        && BaseAtlasBytes == other.BaseAtlasBytes && TransientAtlasRows == other.TransientAtlasRows
        && TransientAtlasBytes == other.TransientAtlasBytes
        && string.Equals(Reason, other.Reason, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PointShadowResolution other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(FaceResolution);
        hash.Add(MaxShadowedLights);
        hash.Add(Degraded);
        hash.Add(Reason, StringComparer.Ordinal);
        hash.Add(BaseAtlasBytes);
        hash.Add(TransientAtlasRows);
        hash.Add(TransientAtlasBytes);
        return hash.ToHashCode();
    }

    public static bool operator ==(PointShadowResolution a, PointShadowResolution b) => a.Equals(b);

    public static bool operator !=(PointShadowResolution a, PointShadowResolution b) => !a.Equals(b);

    public override string ToString()
    {
        string live = Enabled ? $"{FaceResolution} by {MaxShadowedLights}" : "off";
        return Degraded ? $"{live} (degraded: {Reason})" : live;
    }
}
