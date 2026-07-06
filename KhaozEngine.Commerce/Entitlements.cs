using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Commerce;

/// <summary>A purchase proven by an external source, already resolved to an account.</summary>
public readonly record struct VerifiedEntitlement(AccountId Account, string ProductId, string SourceTxnId, int Quantity);

/// <summary>An opaque external proof (webhook body, store receipt). <c>Kind</c> selects the validator.</summary>
public readonly record struct EntitlementProof(string Kind, byte[] Payload);

/// <summary>Turns an untrusted external proof into a verified entitlement, or null if invalid.</summary>
public interface IEntitlementValidator
{
    Task<VerifiedEntitlement?> ValidateAsync(EntitlementProof proof, CancellationToken ct = default);
}
