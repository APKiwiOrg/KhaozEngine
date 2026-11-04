using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// Optional operator import capability. It carries a required host policy through the store's existing
/// seed pipeline to validation of the actual frozen publication. Trusted seed APIs remain separate.
/// </summary>
public interface IContentPolicyImportStore
{
    /// <summary>A host configured a policy but this store cannot bind it to an operator import.</summary>
    const string UnavailableReason = "policy-import-unavailable";

    /// <summary>Imports the complete bundle, including its text, under the required server-owned policy.</summary>
    Task<ContentPublishResult> ImportWithPolicyAsync(
        ContentBundle bundle,
        ContentPolicyImportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>An in-process operator seed request. Its host-owned policy is required and cannot be cleared.</summary>
public sealed class ContentPolicyImportRequest
{
    /// <summary>Captures the host's policy and authenticated publication metadata.</summary>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public ContentPolicyImportRequest(IContentPublishPolicy policy, string actor, string operatorId, string note)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(note);
        Policy = policy;
        Actor = actor;
        Operator = operatorId;
        Note = note;
    }

    /// <summary>The required server-owned policy, never populated from HTTP JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IContentPublishPolicy Policy { get; }

    /// <summary>What the engine authenticated.</summary>
    public string Actor { get; }

    /// <summary>The operator identity forwarded by the console.</summary>
    public string Operator { get; }

    /// <summary>The publication note.</summary>
    public string Note { get; }
}
