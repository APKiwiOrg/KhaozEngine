using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Readiness bookkeeping for a caller-proved new, empty source at generation zero.
/// The caller holds its existing physics gate and supplies the actual source generation.
/// This is neither a lease nor proof of registration, topology or geometry.</summary>
internal sealed class CapsuleSweepEvidence
{
    readonly object source;
    long generation;
    long issue;
    bool valid = true;
    Mutation? pending;

    internal CapsuleSweepEvidence(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
    }

    internal bool IsCurrent(object source, long generation) =>
        ReferenceEquals(this.source, source) && valid && pending is null && this.generation == generation;

    internal Mutation BeginMutation(long advancedGeneration)
    {
        bool canCarry = advancedGeneration > 0 && IsCurrent(source, advancedGeneration - 1);
        // Clear before creating a token too. An abandoned or failed begin cannot leave an older
        // pending completion able to publish evidence later.
        valid = false;
        pending = null;
        long issued = checked(++issue);
        var token = new Mutation(this, advancedGeneration, canCarry, issued);
        pending = token;
        return token;
    }

    internal bool CompleteMutation(Mutation? token, long currentGeneration, bool recordsComplete)
    {
        bool accepted = token is Mutation supplied && pending is Mutation expected &&
            ReferenceEquals(supplied.Owner, this) && ReferenceEquals(expected.Owner, this) &&
            supplied.Issue == expected.Issue && supplied.Generation == expected.Generation &&
            supplied.CanCarry == expected.CanCarry && expected.CanCarry &&
            expected.Generation == currentGeneration && recordsComplete;
        pending = null;
        valid = false;
        if (accepted)
        {
            generation = currentGeneration;
            valid = true;
        }
        return accepted;
    }

    // Immutable issued identity is carried by value so physical mutation hooks remain allocation-free.
    // The local issue number is never serialized, reset or compared between evidence owners.
    internal readonly struct Mutation(CapsuleSweepEvidence owner, long generation, bool canCarry, long issue)
    {
        internal CapsuleSweepEvidence? Owner { get; } = owner;
        internal long Issue { get; } = issue;
        internal long Generation { get; } = generation;
        internal bool CanCarry { get; } = canCarry;
    }
}
