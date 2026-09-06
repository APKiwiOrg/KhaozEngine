using System;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalEvent
{
    private readonly byte[] payload;
    private readonly byte[] payloadChecksum;

    public JournalEvent(string eventType, int eventSchemaVersion, byte[] payload)
    {
        EventType = JournalValidation.Identity(eventType, nameof(eventType), JournalLimits.EngineMaximumIdentityCharacters);
        JournalValidation.Positive(eventSchemaVersion, nameof(eventSchemaVersion));
        EventSchemaVersion = eventSchemaVersion;
        this.payload = JournalValidation.CopyBytes(payload, JournalLimits.EngineMaximumEventPayloadBytes, nameof(payload));
        payloadChecksum = JournalValidation.Hash(this.payload);
    }

    public string EventType { get; }
    public int EventSchemaVersion { get; }
    public ReadOnlyMemory<byte> Payload => JournalValidation.CopyForRead(payload);
    public ReadOnlyMemory<byte> PayloadChecksum => JournalValidation.CopyForRead(payloadChecksum);
    public int OwnedByteCount => payload.Length;

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(EventType.Length, limits.IdentityCharacters, nameof(EventType));
        JournalValidation.Maximum(payload.Length, limits.EventPayloadBytes, nameof(Payload));
    }
}
