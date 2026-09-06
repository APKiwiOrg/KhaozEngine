using System;

namespace KhaozEngine.WorldStore.Journal;

public sealed class JournalOperationIdentity
{
    private readonly byte[] normalizedIntent;

    public JournalOperationIdentity(Guid operationId, string authenticatedScope, string actionKind, byte[] normalizedIntent)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("Operation ID cannot be empty.", nameof(operationId));
        OperationId = operationId;
        AuthenticatedScope = JournalValidation.Identity(authenticatedScope, nameof(authenticatedScope), JournalLimits.EngineMaximumIdentityCharacters);
        ActionKind = JournalValidation.Identity(actionKind, nameof(actionKind), JournalLimits.EngineMaximumIdentityCharacters);
        this.normalizedIntent = JournalValidation.CopyBytes(normalizedIntent, JournalLimits.EngineMaximumNormalizedIntentBytes, nameof(normalizedIntent));
    }

    public Guid OperationId { get; }
    public string AuthenticatedScope { get; }
    public string ActionKind { get; }
    public ReadOnlyMemory<byte> NormalizedIntent => JournalValidation.CopyForRead(normalizedIntent);
    public int OwnedByteCount => normalizedIntent.Length;

    public void Validate(JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Maximum;
        JournalValidation.Maximum(AuthenticatedScope.Length, limits.IdentityCharacters, nameof(AuthenticatedScope));
        JournalValidation.Maximum(ActionKind.Length, limits.IdentityCharacters, nameof(ActionKind));
        JournalValidation.Maximum(normalizedIntent.Length, limits.NormalizedIntentBytes, nameof(NormalizedIntent));
    }
}
