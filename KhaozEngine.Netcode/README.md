# KhaozEngine.Netcode

Game-agnostic, transport-free netcode primitives for MonoGame games.

## UnitAxisQuantizer

8-bit quantization of a unit-range axis (`[-1,1]`) to a signed byte and back, rounding away-from-zero.

```csharp
sbyte qx = UnitAxisQuantizer.Quantize(moveDir.X);   // -127..127
float x  = UnitAxisQuantizer.Dequantize(qx);        // ~moveDir.X, within 1/127
```

The game keeps its own command record and decides which fields to pack; this just does the per-axis math.

> Determinism: if you dequantize commands before they enter a host-authoritative deterministic sim, this
> rounding is part of your sim hash. The scheme is fixed (round away-from-zero, `*127`) for that reason.

## ClientPrediction&lt;TState, TCommand&gt;

Client-side prediction with authoritative reconciliation. You supply the state shape and the per-tick
physics; the engine owns the pending-command buffer, ack-prune, rebase + replay, and the decaying
render-offset smoothing (hard-snap + dead-zone).

```csharp
readonly record struct ShipState(Vector2 Position, Vector2 Velocity) : IPredictedState<ShipState>
{
    public ShipState WithPosition(Vector2 p) => this with { Position = p };
}

sealed class ShipSim : ITickSimulator<ShipState, MyCommand>
{
    public ShipState Step(in ShipState s, in MyCommand c, float dt) => /* integrate */;
}

var prediction = new ClientPrediction<ShipState, MyCommand>(new ShipSim());      // PredictionSettings.Default
prediction.Reset(initialState);

int seq = prediction.Predict(command);                                          // local tick, send seq with command
var result = prediction.Reconcile(tick, authoritativeBasis, lastAckedSeq);      // on snapshot
prediction.AdvancePresentation(elapsedSeconds);                                 // per render frame
Draw(prediction.RenderedState);
```

Tune via `PredictionSettings` (tick rate, buffer cap, hard-snap distance, correction rate, dead-zone).

## RemoteCommandQueue&lt;TCommand&gt;

Host-side per-slot, seq-ordered command queue. Dedups retransmits and negative seqs, returns a neutral
command for an empty slot, and tracks the last acknowledged seq per slot to stamp on snapshots.

```csharp
var queue = new RemoteCommandQueue<MyCommand>(neutralCommand: MyCommand.Idle);
queue.Store(slot, seq, command);                       // on receive
var cmd = queue.Dequeue(slot, out int lastAckedSeq);   // once per sim tick
```
