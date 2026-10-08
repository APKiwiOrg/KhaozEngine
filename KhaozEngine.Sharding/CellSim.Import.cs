using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace KhaozEngine.Sharding;

public sealed partial class CellSim
{
    /// <summary>Optional destination admission for restored and migrated entities. Null keeps the
    /// legacy contract. Configure before exposing the cell to persistence or handoff.</summary>
    public ICellImportAdmission? ImportAdmission { get; set; }

    internal void FinishAdoption(IReadOnlyList<long> netIds)
    {
        foreach (long id in netIds) DespawnGhost(id);
    }

    internal ImportPreparation PrepareImport(byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!SnapshotStaging.TryDecode(registry, snapshot, out SnapshotStaging? staged, out string? error))
            return new(this, error ?? "cell snapshot failed to decode");
        return PrepareImport(staged!);
    }

    internal ImportPreparation PrepareImport(SnapshotStaging staged)
    {
        if (!ReferenceEquals(staged.Registry, registry)) throw new ArgumentException("Import staging uses a different registry.", nameof(staged));
        var existing = LiveOwnedNetIds();
        var selected = new Dictionary<long, Entity>();
        foreach (var pair in staged.Entities)
        {
            if (existing.Contains(pair.Key)) continue;
            FrameAdapter?.ToFrame(staged.World, pair.Value, Frame);
            selected.Add(pair.Key, pair.Value);
        }
        CellAdmissionRead? read = selected.Count > 0 && ImportAdmission is { } policy
            ? policy.Acquire(staged.World, selected) ?? new(CellAdmissionOutcome.Refused, detail: "The import policy returned no verdict.")
            : null;
        return new(this, staged, selected, read);
    }

    internal sealed class ImportPreparation : IDisposable
    {
        readonly CellSim cell;
        readonly SnapshotStaging? staged;
        readonly Dictionary<long, Entity>? selected;
        readonly CellAdmissionRead? read;
        readonly string? decodeError;
        bool published;
        bool disposed;
        public IReadOnlyList<long> AllNetIds { get; } = Array.Empty<long>();
        public ImportPreparation(CellSim cell, string error) { this.cell = cell; decodeError = error; }
        public ImportPreparation(CellSim cell, SnapshotStaging staged, Dictionary<long, Entity> selected, CellAdmissionRead? read)
        {
            this.cell = cell;
            this.staged = staged;
            this.selected = selected;
            this.read = read;
            AllNetIds = new List<long>(staged.Entities.Keys);
        }
        internal CellRestoreResult CheckAdmission()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            read?.AssertUsable();
            if (decodeError is not null) return CellRestoreResult.Failed(decodeError);
            if (read is { Outcome: not CellAdmissionOutcome.Accepted })
                return CellRestoreResult.AwaitingAdmission(read.Outcome, read.Detail);
            return new(true, Array.Empty<long>(), 0, null);
        }
        public CellRestoreResult Publish(IReadOnlyDictionary<long, TransientScope>? marks = null)
        {
            if (published) throw new InvalidOperationException("A cell import can only publish once.");
            CellRestoreResult admission = CheckAdmission();
            if (!admission.Ok) return admission;
            var copies = new Dictionary<long, Entity>();
            try
            {
                foreach (long id in selected!.Keys) copies.Add(id, staged!.CopyTo(id, cell.World));
            }
            catch
            {
                foreach (Entity entity in copies.Values) cell.World.Despawn(entity);
                throw;
            }
            int retained = 0;
            foreach (RetainedComponent component in staged!.Retained)
            {
                if (!selected.ContainsKey(component.NetId)) continue;
                if (!cell.retainedUnknown.TryGetValue(component.NetId, out List<RetainedComponent>? frames))
                    cell.retainedUnknown[component.NetId] = frames = [];
                frames.Add(component);
                retained++;
            }
            foreach (var pair in copies)
            {
                if (marks is not null && marks.TryGetValue(pair.Key, out TransientScope scope))
                    cell.World.Set(pair.Value, new Transient { Scope = scope });
                cell.RegisterOwned(pair.Key, pair.Value);
            }
            published = true;
            return new(true, new List<long>(copies.Keys), retained, null, AllNetIds.Count - copies.Count);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            read?.Dispose();
        }
    }
}
