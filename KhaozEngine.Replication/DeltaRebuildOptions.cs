using System;

namespace KhaozEngine.Replication;

/// <summary>
/// The hard resource limits of one format 2 stream, shared by the server writer slot and the client receiver. Every
/// limit is enforced before a projection is retained or a packet is sent. The defaults are the approved review
/// defaults, not measured capacity. Call <see cref="Validate"/> before use. Format 2 components construct with
/// validated options.
/// </summary>
public sealed class DeltaRebuildOptions
{
    /// <summary>Retained projections per viewer, pins and the unsent or repair candidate included. At least 4, so
    /// three pins and one candidate always fit by count.</summary>
    public int MaxRetainedProjections { get; init; } = 32;

    /// <summary>Retained backing bytes per viewer: the full <c>Length</c> of every distinct backing array reachable
    /// from any retained projection, counted once by reference identity. Must hold three complete keyframes, at least
    /// <c>3 * MaxKeyframeBytes</c>, so an acknowledged baseline, a pending keyframe and a new candidate always fit
    /// together and byte pressure can never force a repair loop. Client presentation buffers hold independent copies
    /// and are outside this budget.</summary>
    public int MaxRetainedPayloadBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Complete keyframe object bytes, <see cref="EnvelopeBytes"/> included.</summary>
    public int MaxKeyframeBytes { get; init; } = 64 * 1024;

    /// <summary>Entities in one projection.</summary>
    public int MaxEntities { get; init; } = 1024;

    /// <summary>Component frames in one projection, zero-byte tags and opaque extensions included. Bounds metadata
    /// independently of payload bytes.</summary>
    public int MaxComponents { get; init; } = 16384;

    /// <summary>An uninterpreted byte charge added to every complete keyframe object. Zero for standalone users. A
    /// transport layer sets it to its own envelope size, so the complete object budget holds without this assembly
    /// knowing a transport or envelope format. Must be nonnegative and below <see cref="MaxKeyframeBytes"/>.</summary>
    public int EnvelopeBytes { get; init; }

    /// <summary>Committed new sends without a newer acknowledgement before a writer asks for keyframe repair. At
    /// least 1. The effective window is <c>min(NoAckSendWindow, MaxRetainedProjections - 1)</c>.</summary>
    public int NoAckSendWindow { get; init; } = 31;

    /// <summary>The no-ack window the retained count can honor: <c>min(NoAckSendWindow, MaxRetainedProjections - 1)</c>.</summary>
    internal int EffectiveNoAckSendWindow => Math.Min(NoAckSendWindow, MaxRetainedProjections - 1);

    /// <summary>
    /// Throws for the first invalid property in declaration order: a retained count below 4, a nonpositive byte or
    /// count limit, a retained byte budget below three keyframes, a negative envelope charge or one at or above the
    /// keyframe cap, or a no-ack window below 1. The three keyframe rule is checked once both byte limits are known to
    /// be positive and names <see cref="MaxRetainedPayloadBytes"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A limit is invalid. <c>ParamName</c> names the property.</exception>
    public void Validate()
    {
        if (MaxRetainedProjections < 4)
            throw Invalid(nameof(MaxRetainedProjections), MaxRetainedProjections, "must be at least 4");
        if (MaxRetainedPayloadBytes <= 0)
            throw Invalid(nameof(MaxRetainedPayloadBytes), MaxRetainedPayloadBytes, "must be positive");
        if (MaxKeyframeBytes <= 0)
            throw Invalid(nameof(MaxKeyframeBytes), MaxKeyframeBytes, "must be positive");
        if (MaxRetainedPayloadBytes < 3L * MaxKeyframeBytes)
            throw Invalid(nameof(MaxRetainedPayloadBytes), MaxRetainedPayloadBytes,
                "must be at least three times MaxKeyframeBytes");
        if (MaxEntities <= 0)
            throw Invalid(nameof(MaxEntities), MaxEntities, "must be positive");
        if (MaxComponents <= 0)
            throw Invalid(nameof(MaxComponents), MaxComponents, "must be positive");
        if (EnvelopeBytes < 0 || EnvelopeBytes >= MaxKeyframeBytes)
            throw Invalid(nameof(EnvelopeBytes), EnvelopeBytes, "must be nonnegative and below MaxKeyframeBytes");
        if (NoAckSendWindow < 1)
            throw Invalid(nameof(NoAckSendWindow), NoAckSendWindow, "must be at least 1");
    }

    /// <summary>Returns a validated copy with only <see cref="EnvelopeBytes"/> replaced.</summary>
    /// <param name="envelopeBytes">The new envelope charge.</param>
    /// <returns>A new options instance. This instance is unchanged.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The copy is invalid. <c>ParamName</c> names the property.</exception>
    public DeltaRebuildOptions WithEnvelopeBytes(int envelopeBytes)
    {
        var copy = new DeltaRebuildOptions
        {
            MaxRetainedProjections = MaxRetainedProjections,
            MaxRetainedPayloadBytes = MaxRetainedPayloadBytes,
            MaxKeyframeBytes = MaxKeyframeBytes,
            MaxEntities = MaxEntities,
            MaxComponents = MaxComponents,
            EnvelopeBytes = envelopeBytes,
            NoAckSendWindow = NoAckSendWindow,
        };
        copy.Validate();
        return copy;
    }

    private static ArgumentOutOfRangeException Invalid(string property, int value, string rule) =>
        new(property, value, $"DeltaRebuildOptions.{property} {rule}.");
}
