using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>One bounded chunk set at a time. Completed arrays never alias the private assembly lists.</summary>
internal sealed class TileCombatPreparationAssembler
{
    readonly List<TileCombatPreparation> states = new();
    readonly List<TileCombatTerminal> terminals = new();
    readonly TileCombatTerminalOrder terminalOrder = new(TileProtocol.MaxPreparationRecords);
    byte tag;
    long tick;
    ushort chunks;
    int nextChunk;

    public bool TryAddState(in TilePreparationChunkHeader header, IReadOnlyList<TileCombatPreparation> records,
        out TilePreparationStateFrame? complete)
    {
        complete = null;
        if (records is null || !BeginChunk(header, records.Count, terminal: false)) return Reject();
        Reserve(states, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            TileCombatPreparation record = records[i];
            if (!TileProtocol.ValidPreparationRecord(record, header.ServerTick)
                || (states.Count > 0 && record.AttackerNetId <= states[^1].AttackerNetId)) return Reject();
            states.Add(record);
        }
        nextChunk++;
        if (nextChunk == chunks)
        {
            complete = new TilePreparationStateFrame(tick, states.ToArray());
            Clear();
        }
        return true;
    }

    public bool TryAddTerminals(in TilePreparationChunkHeader header, IReadOnlyList<TileCombatTerminal> records,
        out TilePreparationTerminalFrame? complete)
    {
        complete = null;
        if (records is null || !BeginChunk(header, records.Count, terminal: true)) return Reject();
        Reserve(terminals, records.Count);
        for (int i = 0; i < records.Count; i++)
        {
            TileCombatTerminal record = records[i];
            if (!TileProtocol.ValidPreparationTerminal(record, header.ServerTick)
                || !terminalOrder.TryAdd(record)) return Reject();
            terminals.Add(record);
        }
        nextChunk++;
        if (nextChunk == chunks)
        {
            complete = new TilePreparationTerminalFrame(tick, terminals.ToArray());
            Clear();
        }
        return true;
    }

    bool BeginChunk(in TilePreparationChunkHeader header, int count, bool terminal)
    {
        if (!TileProtocol.ValidPreparationHeader(header, count, terminal)) return false;
        byte requestedTag = terminal ? TileProtocol.ServerFrameCombatPreparationTerminal : TileProtocol.ServerFrameCombatPreparation;
        if (tag == 0)
        {
            if (header.ChunkIndex != 0) return false;
            tag = requestedTag;
            tick = header.ServerTick;
            chunks = header.ChunkCount;
        }
        if (tag != requestedTag || tick != header.ServerTick || chunks != header.ChunkCount
            || nextChunk != header.ChunkIndex) return false;
        int held = terminal ? terminals.Count : states.Count;
        return held + count <= chunks * TileProtocol.PreparationRecordsPerChunk;
    }

    static void Reserve<T>(List<T> list, int count)
    {
        int needed = list.Count + count;
        if (list.Capacity < needed)
            list.Capacity = Math.Min(TileProtocol.MaxPreparationRecords, Math.Max(needed, Math.Max(4, list.Capacity * 2)));
    }

    bool Reject()
    {
        Clear();
        return false;
    }

    public void Clear()
    {
        states.Clear();
        terminals.Clear();
        tag = 0;
        tick = 0;
        chunks = 0;
        nextChunk = 0;
        terminalOrder.Clear();
    }
}
