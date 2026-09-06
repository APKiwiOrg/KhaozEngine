using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Benchmarks.Journal;

public enum JournalWorkloadKind
{
    InventoryChange,
    BankTransfer,
    Trade,
    ProjectionRead,
    OperationReplay,
    Snapshot,
    Compaction,
}

public sealed record JournalWorkloadStep(
    int Index,
    JournalWorkloadKind Kind,
    int PrimaryPlayer,
    int SecondaryPlayer,
    Guid OperationId,
    string PayloadHex)
{
    public byte[] Payload => Convert.FromHexString(PayloadHex);
    public bool IsMutation => Kind is JournalWorkloadKind.InventoryChange
        or JournalWorkloadKind.BankTransfer
        or JournalWorkloadKind.Trade
        or JournalWorkloadKind.OperationReplay;
    public string Fingerprint => $"{Index}:{Kind}:{PrimaryPlayer}:{SecondaryPlayer}:{OperationId:D}:{PayloadHex}";
}

internal static class JournalWorkload
{
    internal static IReadOnlyList<JournalWorkloadStep> Generate(JournalBenchmarkConfig config)
        => Stream(config).ToArray();

    internal static IEnumerable<JournalWorkloadStep> Stream(JournalBenchmarkConfig config)
    {
        config.Validate();
        var generator = new Generator(unchecked((ulong)(uint)config.Seed) + 0x9E3779B97F4A7C15UL);
        Guid lastMutation = Guid.Empty;
        int lastPrimary = 0;
        int lastSecondary = 1;
        string lastPayload = "00";

        for (int index = 0; index < config.Operations; index++)
        {
            JournalWorkloadKind kind = (JournalWorkloadKind)(index % 7);
            int primary = generator.Next(config.Players);
            int secondary = generator.Next(config.Players - 1);
            if (secondary >= primary) secondary++;
            int payloadLength = 1 + generator.Next(config.PayloadBytes);
            byte[] payload = generator.Bytes(payloadLength);
            Guid operationId = CreateGuid(config.Seed, index, kind, primary, secondary, payload);

            if (kind == JournalWorkloadKind.OperationReplay)
            {
                operationId = lastMutation;
                primary = lastPrimary;
                secondary = lastSecondary;
                payload = Convert.FromHexString(lastPayload);
            }
            else if (kind is JournalWorkloadKind.Snapshot or JournalWorkloadKind.Compaction)
            {
                primary = lastPrimary;
                secondary = lastSecondary;
            }
            else if (kind is JournalWorkloadKind.InventoryChange or JournalWorkloadKind.BankTransfer or JournalWorkloadKind.Trade)
            {
                lastMutation = operationId;
                lastPrimary = primary;
                lastSecondary = secondary;
                lastPayload = Convert.ToHexString(payload);
            }

            yield return new JournalWorkloadStep(
                index,
                kind,
                primary,
                secondary,
                operationId,
                Convert.ToHexString(payload));
        }
    }

    private static Guid CreateGuid(int seed, int index, JournalWorkloadKind kind, int primary, int secondary, byte[] payload)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"{seed}:{index}:{(int)kind}:{primary}:{secondary}:");
        byte[] source = new byte[prefix.Length + payload.Length];
        Buffer.BlockCopy(prefix, 0, source, 0, prefix.Length);
        Buffer.BlockCopy(payload, 0, source, prefix.Length, payload.Length);
        byte[] hash = SHA256.HashData(source);
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class Generator
    {
        private ulong state;

        internal Generator(ulong seed) => state = seed == 0 ? 0xA0761D6478BD642FUL : seed;

        internal int Next(int exclusiveMaximum)
            => (int)(NextUInt64() % (uint)exclusiveMaximum);

        internal byte[] Bytes(int length)
        {
            var result = new byte[length];
            for (int i = 0; i < result.Length; i++) result[i] = (byte)NextUInt64();
            return result;
        }

        private ulong NextUInt64()
        {
            ulong value = state;
            value ^= value >> 12;
            value ^= value << 25;
            value ^= value >> 27;
            state = value;
            return value * 0x2545F4914F6CDD1DUL;
        }
    }
}
