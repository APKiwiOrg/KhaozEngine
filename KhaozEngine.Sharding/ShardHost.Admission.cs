using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KhaozEngine.Sharding;

public sealed partial class ShardHost
{
    readonly record struct MigrationKey(CellCoord Source, CellCoord Target, long NetId);
    readonly Dictionary<MigrationKey, CellMessage> deferredMigrations = new();

    /// <summary>Received migrations still waiting for destination environment admission. Their
    /// sources remain frozen and their cells cannot be evicted.</summary>
    public int PendingMigrationAdmissions => deferredMigrations.Count;

    void ProcessMigrationAdmissions(CellSim cell)
    {
        foreach (CellMessage message in link.Drain(cell.Coord, CellMessageKind.Migrate))
        {
            // ProcessHandoffs sends one owned entity per message. Keying by that stable identity
            // deduplicates at-least-once delivery even while its destination is unavailable.
            if (message.Payload.Length < 12 || BinaryPrimitives.ReadInt32LittleEndian(message.Payload) != 1)
                throw new InvalidDataException("A migration must contain exactly one entity.");
            long id = BinaryPrimitives.ReadInt64LittleEndian(message.Payload.AsSpan(4));
            var key = new MigrationKey(message.Source, message.Target, id);
            if (deferredMigrations.TryGetValue(key, out CellMessage previous))
            {
                if (!previous.Payload.AsSpan().SequenceEqual(message.Payload))
                    throw new InvalidDataException("A pending migration changed its frozen snapshot.");
                continue;
            }
            deferredMigrations.Add(key, message);
        }
        foreach (var pair in deferredMigrations.Where(pair => pair.Key.Target == cell.Coord).ToArray())
        {
            CellMessage message = pair.Value;
            using CellSim.ImportPreparation prepared = cell.PrepareImport(message.Payload);
            CellRestoreResult result = prepared.Publish(transientCrossings);
            if (result.NeedsAdmission) continue;
            if (!result.Ok) throw new InvalidDataException(result.Error ?? "The migration snapshot could not be decoded.");
            cell.FinishAdoption(prepared.AllNetIds);
            foreach (long id in prepared.AllNetIds)
            {
                transientCrossings.Remove(id);
                link.Send(new CellMessage(cell.Coord, message.Source, CellMessageKind.MigrateAck, BitConverter.GetBytes(id)));
            }
            deferredMigrations.Remove(pair.Key);
        }
    }
}
