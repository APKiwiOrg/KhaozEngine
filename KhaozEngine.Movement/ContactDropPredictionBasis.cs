using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One preview attempt's local and semantic query basis. Never retain it across a changed body or
/// environment, and never use a new basis for each slice of the same preview.</summary>
internal sealed class ContactDropPredictionBasis
{
    MovementQueryLeaseId? _id;
    MovementQueryIdentity? _identity;

    internal void Authenticate(MovementQueryLease queries)
    {
        queries.AssertCurrent();
        if (_id is { } id && id != queries.Id || _identity is { } identity && identity != queries.Identity)
            throw new InvalidOperationException("The Contact Drop prediction basis changed between slices.");
        _id ??= queries.Id;
        _identity ??= queries.Identity;
    }
}
