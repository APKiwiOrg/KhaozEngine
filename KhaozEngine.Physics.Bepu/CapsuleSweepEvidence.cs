using System;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Readiness bookkeeping for a caller-proved new, empty source at generation zero.
/// The caller holds its existing physics gate and supplies the actual source generation.
/// This is neither a lease nor proof of registration, topology or geometry.</summary>
internal sealed class CapsuleSweepEvidence
{
    readonly object source;
    long generation;
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
        var token = new Mutation(this, advancedGeneration, canCarry);
        pending = token;
        return token;
    }

    internal bool CompleteMutation(Mutation? token, long currentGeneration, bool recordsComplete)
    {
        bool accepted = token is not null && ReferenceEquals(token.Owner, this) &&
            ReferenceEquals(token, pending) && token.CanCarry && token.Generation == currentGeneration && recordsComplete;
        pending = null;
        valid = false;
        if (accepted)
        {
            generation = currentGeneration;
            valid = true;
        }
        return accepted;
    }

    internal sealed class Mutation(CapsuleSweepEvidence owner, long generation, bool canCarry)
    {
        internal CapsuleSweepEvidence Owner { get; } = owner;
        internal long Generation { get; } = generation;
        internal bool CanCarry { get; } = canCarry;
    }
}
