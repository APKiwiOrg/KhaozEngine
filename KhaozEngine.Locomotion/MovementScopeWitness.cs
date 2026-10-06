using System;
using System.Collections.Generic;
using System.Text;

namespace KhaozEngine.Locomotion;

/// <summary>Finite producer-certified scope evidence. Backing IDs are not portable compatibility keys.</summary>
public sealed class MovementScopeWitness
{
    public const int MaxResourceCount = 256;
    public const int MaxResourceIdUtf8Bytes = 1024;
    public const int MaxAggregateResourceIdUtf8Bytes = 65536;
    /// <summary>Producer query-policy ceiling over one combined prepare/pin attempt, not a world-size limit.</summary>
    public const int MaxDependencyVisits = 4096;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public MovementQueryScope Scope { get; }
    public MovementQueryIdentity Identity { get; }
    public IReadOnlyList<string> ResourceIds { get; }
    public bool CoversKnownEmptyRegions { get; }

    MovementScopeWitness(in MovementQueryScope scope, in MovementQueryIdentity identity, string[] resources)
    {
        Scope = scope;
        Identity = identity;
        ResourceIds = Array.AsReadOnly(resources);
        CoversKnownEmptyRegions = true;
    }

    /// <summary>Validates bounds before copying/sorting. Any refusal returns a null witness, never a prefix.</summary>
    public static MovementAvailability TryCreate(in MovementQueryScope scope, in MovementQueryIdentity identity,
        IReadOnlyList<string> resourceIds, bool coversKnownEmptyRegions, out MovementScopeWitness? witness)
    {
        witness = null;
        if (!scope.IsValid || !identity.IsValid || scope.Identity != identity || resourceIds is null)
            return MovementAvailability.Invalid;
        if (!coversKnownEmptyRegions) return MovementAvailability.Unresolved;
        int count = resourceIds.Count;
        if (count > MaxResourceCount) return MovementAvailability.CapacityExceeded;
        if (count <= 0) return MovementAvailability.Invalid;

        MovementAvailability validation = ValidateResources(resourceIds, count, null);
        if (validation != MovementAvailability.Known) return validation;
        // The input need not remain immutable after this call. Revalidate the actual copied values
        // as well, so a mutable collection cannot evade the limits between the two passes.
        var copy = new string[count];
        validation = ValidateResources(resourceIds, count, copy);
        if (validation != MovementAvailability.Known) return validation;
        Array.Sort(copy, StringComparer.Ordinal);
        for (int i = 1; i < copy.Length; i++)
            if (string.Equals(copy[i - 1], copy[i], StringComparison.Ordinal)) return MovementAvailability.Invalid;
        witness = new MovementScopeWitness(scope, identity, copy);
        return MovementAvailability.Known;
    }

    static MovementAvailability ValidateResources(IReadOnlyList<string> resources, int count, string[]? copy)
    {
        if (resources.Count != count) return MovementAvailability.Invalid;
        int total = 0;
        for (int i = 0; i < count; i++)
        {
            string resource = resources[i];
            if (resource is null) return MovementAvailability.Invalid;
            if (resource.Length > MaxResourceIdUtf8Bytes) return MovementAvailability.CapacityExceeded;
            if (!MovementEnvironmentValidation.Name(resource)) return MovementAvailability.Invalid;
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(resource); }
            catch (EncoderFallbackException) { return MovementAvailability.Invalid; }
            if (bytes > MaxResourceIdUtf8Bytes) return MovementAvailability.CapacityExceeded;
            total = checked(total + bytes);
            if (total > MaxAggregateResourceIdUtf8Bytes) return MovementAvailability.CapacityExceeded;
            if (copy is not null) copy[i] = resource;
        }
        return resources.Count == count ? MovementAvailability.Known : MovementAvailability.Invalid;
    }
}
