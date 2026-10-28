using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

public sealed partial class OrdinaryMovementRegistration
{
    /// <summary>Applies a canonical batch between movement reads. Sets block and support under ordinary static rules.
    /// Duplicate owners and baked or analytic aliases are invalid. Removes precede adds in ordinal owner order.
    /// Empty batches do nothing. A successful nonempty batch advances Generation once, preserving GeometryGeneration,
    /// Extent, ExcludedStatics and authored identity. Geometry and provenance publish inside one source writer.
    /// Stale registrations refuse before editing. A failure after a write leaves the installation Unresolved until
    /// rebuilt. No rollback, occupant carrying or network scheduling is implied.</summary>
    /// <exception cref="ArgumentException">A request, owner, remove target or fixed-extent containment is invalid.</exception>
    /// <exception cref="NotSupportedException">The source lacks certified reads, static mutation or shape support.</exception>
    /// <exception cref="InvalidOperationException">The registration is stale, a read is active or a write cannot be authenticated.</exception>
    public void ApplyRuntimeStatics(ReadOnlySpan<RuntimeStaticChange> changes)
    {
        if (changes.IsEmpty) return;
        RuntimeStaticChange[] batch = changes.ToArray();
        foreach (RuntimeStaticChange change in batch)
            MovementEnvironmentValidation.Require(change.Kind is RuntimeStaticChangeKind.Set or RuntimeStaticChangeKind.Remove &&
                change.Owner.IsValid && change.Owner.WorldId == WorldId, nameof(changes));
        Array.Sort(batch, (a, b) => StringComparer.Ordinal.Compare(a.Owner.LocalId, b.Owner.LocalId));
        for (int i = 1; i < batch.Length; i++)
            MovementEnvironmentValidation.Require(batch[i - 1].Owner != batch[i].Owner, nameof(changes));
        if (SourceWorld is not IPhysicsQueryLeaseSource || SourceWorld is not IPhysicsStaticMutationSource source)
            throw new NotSupportedException("Runtime statics require certified reads and an exclusive static writer.");
        // Never wait for this writer under the environment cache lock. Readers acquire that lock after physics.
        using IPhysicsStaticMutationLease writer = source.AcquireStaticMutationLease();
        writer.AssertCurrent();
        OrdinaryRuntimeStaticState old = Volatile.Read(ref _runtime);
        if (!ReferenceEquals(writer.SourceWorld, SourceWorld) || writer.Origin != SourceWorld.Origin || old.Poisoned ||
            writer.StaticGeometryGeneration != old.ExpectedStaticRevision)
            throw new InvalidOperationException("The runtime static installation is stale.");
        Vector3 origin = writer.Origin;
        long revision = checked(old.Revision + 1);
        var next = new Dictionary<MovementSupportKey, Entry>(checked(old.Owners.Count + batch.Length));
        var liveHandles = new HashSet<StaticHandle>(checked(old.Owners.Count + batch.Length));
        foreach (var pair in old.Owners) { next.Add(pair.Key, pair.Value); liveHandles.Add(pair.Value.Handle); }
        var poses = new Pose[batch.Length];
        var bounds = new (Vector3 Minimum, Vector3 Maximum)[batch.Length];
        int writes = 0;
        for (int i = 0; i < batch.Length; i++)
        {
            RuntimeStaticChange change = batch[i];
            MovementEnvironmentValidation.Require(!_authoredOwners.Contains(change.Owner) && change.Owner.LocalId != "analytic-ground",
                nameof(changes));
            bool exists = old.Owners.ContainsKey(change.Owner);
            MovementEnvironmentValidation.Require(change.Kind != RuntimeStaticChangeKind.Remove || exists, nameof(changes));
            if (exists) writes = checked(writes + 1);
            if (change.Kind != RuntimeStaticChangeKind.Set) continue;
            MovementEnvironmentValidation.Require(change.Shape is not null, nameof(changes));
            poses[i] = new(change.AbsolutePose.Position - origin, change.AbsolutePose.Orientation);
            var represented = RuntimeStaticBounds.Capture(change.Shape!, poses[i], origin);
            var absolute = RuntimeStaticBounds.Capture(change.Shape!, change.AbsolutePose, Vector3.Zero);
            bounds[i] = (Vector3.Min(represented.Minimum, absolute.Minimum), Vector3.Max(represented.Maximum, absolute.Maximum));
            var (min, max) = bounds[i];
            MovementEnvironmentValidation.Require(min.X >= Extent.Minimum.X && min.Y >= Extent.Minimum.Y && min.Z >= Extent.Minimum.Z &&
                max.X <= Extent.Maximum.X && max.Y <= Extent.Maximum.Y && max.Z <= Extent.Maximum.Z, nameof(changes));
            writes = checked(writes + 1);
        }
        _ = checked(old.ExpectedStaticRevision + writes);
        long accepted = old.ExpectedStaticRevision;
        var poisoned = new OrdinaryRuntimeStaticState(old);
        try
        {
            foreach (RuntimeStaticChange change in batch)
                if (old.Owners.TryGetValue(change.Owner, out Entry entry))
                {
                    SourceWorld.RemoveStatic(entry.Handle);
                    CheckWrite(writer, origin, ref accepted);
                    next.Remove(change.Owner);
                    liveHandles.Remove(entry.Handle);
                }
            for (int i = 0; i < batch.Length; i++)
            {
                RuntimeStaticChange change = batch[i];
                if (change.Kind != RuntimeStaticChangeKind.Set) continue;
                StaticHandle handle = SourceWorld.AddStatic(change.Shape!, poses[i], change.Material);
                CheckWrite(writer, origin, ref accepted);
                if (handle.Value < 0 || _entries.ContainsKey(handle) || _exclusions.Contains(handle) || !liveHandles.Add(handle))
                    throw new InvalidOperationException("A source static write returned an invalid or aliased handle.");
                next.Add(change.Owner, new(handle, 0, int.MaxValue, change.Owner, bounds[i].Minimum, bounds[i].Maximum));
            }
            var publication = new OrdinaryRuntimeStaticState(accepted, revision, next);
            writer.AssertCurrent();
            if (writer.Origin != origin || writer.StaticGeometryGeneration != accepted)
                throw new InvalidOperationException("The static writer changed before publication.");
            Volatile.Write(ref _runtime, publication);
        }
        catch
        {
            bool unchanged = false;
            try
            {
                writer.AssertCurrent();
                unchanged = accepted == old.ExpectedStaticRevision && writer.Origin == origin &&
                    writer.StaticGeometryGeneration == old.ExpectedStaticRevision;
            }
            finally
            {
                if (!unchanged) Volatile.Write(ref _runtime, poisoned);
            }
            throw;
        }
    }

    static void CheckWrite(IPhysicsStaticMutationLease writer, Vector3 origin, ref long accepted)
    {
        writer.AssertCurrent();
        if (writer.Origin != origin || writer.StaticGeometryGeneration != checked(accepted + 1))
            throw new InvalidOperationException("A source static write did not advance exactly once.");
        accepted++;
    }
}
