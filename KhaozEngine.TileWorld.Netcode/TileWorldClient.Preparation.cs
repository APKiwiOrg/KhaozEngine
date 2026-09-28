using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

public sealed partial class TileWorldClient
{
    PreparationClientRuntime? combatPreparation;
    int preparationGeneration;

    sealed class PreparationClientRuntime
    {
        internal readonly TileCombatPreparationAssembler Assembler = new();
        internal readonly TileCombatPreparationLedger Ledger = new();
        internal readonly TileCombatPresentationClock Clock = new();
        internal readonly List<TileCombatPreparation> States = new();
        internal readonly List<TileCombatTerminal> Terminals = new();
    }

    /// <summary>Fractional combat presentation time from the newest applied movement snapshot, capped at one tick
    /// beyond it. Returns -1 before a valid snapshot, after disconnect, or when preparation is disabled.
    /// This clock predicts no outcome and is independent of the remote movement interpolation timeline.</summary>
    public double CombatPresentationTick => combatPreparation?.Clock.Tick ?? -1d;

    void ObservePreparationSnapshot(long serverTick)
    {
        if (!config.CombatPreparationEnabled || !IsJoined || serverTick < 0) return;
        combatPreparation ??= new();
        combatPreparation.Clock.Observe(serverTick);
    }

    /// <summary>Raised after a complete preparation snapshot is applied, carrying its authoritative server tick.</summary>
    public event Action<long>? CombatPreparationsChanged;

    /// <summary>One authoritative prepared outcome, delivered once after its tick's full preparation snapshot.</summary>
    public event Action<PreparedCombatEvent>? PreparedCombatEvent;

    /// <summary>One cancelled preparation. Cancellation never implies a hit or damage.</summary>
    public event Action<CombatPreparationEnded>? CombatPreparationEnded;

    /// <summary>Malformed preparation chunks or sets refused without replacing the last published state.</summary>
    public long RejectedCombatPreparationFrameCount { get; private set; }

    /// <summary>The latest authoritative visible preparation for an attacker. False when disabled or absent.</summary>
    public bool TryGetCombatPreparation(long attackerNetId, out TileCombatPreparation preparation)
    {
        preparation = default;
        return combatPreparation is { } runtime && runtime.Ledger.TryGet(attackerNetId, out preparation);
    }

    void CheckPreparationInterruption(byte tag)
    {
        if (tag is TileProtocol.ServerFrameCombatPreparation or TileProtocol.ServerFrameCombatPreparationTerminal
            || combatPreparation is not { Assembler.HasPending: true } runtime) return;
        runtime.Assembler.Clear();
        RejectedCombatPreparationFrameCount++;
    }

    void OnPreparationFrame(byte[] data, bool terminal)
    {
        if (!config.CombatPreparationEnabled || !IsJoined) return;
        combatPreparation ??= new();
        var runtime = combatPreparation;
        TilePreparationDispatch? dispatch = null;
        bool valid;
        if (terminal)
        {
            valid = TileProtocol.TryDecodePreparationTerminalChunk(data, out var header, runtime.Terminals);
            if (valid)
            {
                valid = runtime.Assembler.TryAddTerminals(header, runtime.Terminals, out var frame);
                if (valid && frame is not null) dispatch = runtime.Ledger.ApplyTerminals(frame);
            }
        }
        else
        {
            valid = TileProtocol.TryDecodePreparationChunk(data, out var header, runtime.States);
            if (valid)
            {
                valid = runtime.Assembler.TryAddState(header, runtime.States, out var frame);
                if (valid && frame is not null) dispatch = runtime.Ledger.ApplyState(frame);
            }
        }
        if (!valid)
        {
            runtime.Assembler.Clear();
            RejectedCombatPreparationFrameCount++;
            return;
        }
        if (dispatch is not null) DispatchPreparations(dispatch);
    }

    void DispatchPreparations(TilePreparationDispatch dispatch)
    {
        // Owned arrays survive nested Poll calls. A disconnect/dispose during a callback fences the remaining
        // outer batch, so lifecycle cleanup cannot be followed by callbacks from the abandoned session.
        int generation = preparationGeneration;
        if (dispatch.AppliedStateTick is { } tick) CombatPreparationsChanged?.Invoke(tick);
        foreach (CombatPreparationEnded ended in dispatch.Ended)
        {
            if (generation != preparationGeneration) return;
            CombatPreparationEnded?.Invoke(ended);
        }
        foreach (PreparedCombatEvent result in dispatch.Results)
        {
            if (generation != preparationGeneration) return;
            PreparedCombatEvent?.Invoke(result);
        }
    }

    void ClearPreparations()
    {
        preparationGeneration++;
        combatPreparation?.Clock.Clear();
        combatPreparation?.Assembler.Clear();
        combatPreparation?.Ledger.Clear();
        combatPreparation?.States.Clear();
        combatPreparation?.Terminals.Clear();
    }
}
