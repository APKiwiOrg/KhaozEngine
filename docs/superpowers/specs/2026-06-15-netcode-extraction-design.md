# Netcode extraction into KhaozEngine

Date: 2026-06-15
Branch: `worktree-feature+netcode`
Status: design, awaiting approval

## Goal

Extract SpaceGame's reusable, client-side / transport-level netcode patterns into KhaozEngine,
generalized off SpaceGame's wire format so other games can reuse them. Three source patterns:

- `TickCommandCodec` -> the 8-bit quantization scheme for continuous input axes.
- `MultiplayerPredictionModels` (`ClientPredictionState` + `RemotePlayerCommandQueue`) -> client-side
  prediction/reconciliation and the symmetric host-side per-slot command queue.
- `EntityUpdateChannelSplitter` -> splitting entity updates across reliable/unreliable LiteNetLib
  channels to avoid head-of-line blocking.

The packed field layout (which axes/actions/entities) stays game-side; the engine exposes the generic
machinery (primitives, generic buffer/replay, interface + driver).

## Determinism (the flagged caveat — CONFIRMED hash-gated)

The dequantization **does** feed the host-authoritative deterministic sim:

- `SpaceGame.Core/Game/Authority/RunWorldAuthority.cs:131` calls `TickCommandCodec.ToGameInput`
  (which dequantizes) to produce the tick input fed to the sim.
- `SpaceGame.Core/Systems/PilotSystem.cs` (header: "No RNG; movement is deterministic
  (hash-identical)") calls `TickCommandCodec.DequantizeAxis` on the command before integrating.

Therefore `Quantize`/`Dequantize` are **hash-gated against SpaceGame sim hash
`17709480852979803671`**. Consequences:

- The engine `UnitAxisQuantizer` MUST round bit-identically to SpaceGame's current code:
  - `Quantize`: `(sbyte)MathF.Round(MathHelper.Clamp(v, -1f, 1f) * 127f, MidpointRounding.AwayFromZero)`
  - `Dequantize`: `v / 127f`
- This task ships the engine package ONLY. No SpaceGame change here, so **no determinism risk in this
  task**. The risk lands at *adoption*: SpaceGame must swap its private `QuantizeAxis`/`DequantizeAxis`
  to call the engine methods and re-run its determinism suite (hash must stay
  `17709480852979803671`) before merging the adoption.
- Engine tests pin the exact rounding values so the contract cannot silently drift.

`ClientPrediction` and `RemoteCommandQueue` are determinism-neutral: prediction is client-side only,
and the queue orders/dedups by sequence number without ever altering command values.

## Packages

Two new packages (decision: keep the pure machinery free of a transport dependency).

### `KhaozEngine.Netcode` (pure)

Refs `MonoGame.Framework.DesktopGL 3.8.*` only (for `Vector2` / `MathHelper`). `net10.0`, nullable on,
`InternalsVisibleTo KhaozEngine.Tests`, ships `README.md`.

1. **`UnitAxisQuantizer`** (static class)
   - `static sbyte Quantize(float value)` — clamp to `[-1,1]`, `*127`, round away-from-zero, cast `sbyte`.
   - `static float Dequantize(sbyte value)` — `value / 127f`.
   - Byte-identical to SpaceGame `TickCommandCodec`. The game keeps its own command record + field
     mapping (move/aim/actions); it just calls these two primitives per axis.

2. **Client prediction** — generalizes `ClientPredictionState`.
   ```csharp
   public interface IPredictedState<TSelf>
   {
       Vector2 Position { get; }
       TSelf WithPosition(Vector2 position);
   }

   public interface ITickSimulator<TState, TCommand>
   {
       TState Step(in TState state, in TCommand command, float dt);
   }

   public readonly record struct PredictionSettings(
       float TickSeconds,
       int MaxPendingCommands,
       float HardSnapDistance,
       float CorrectionRate,
       float CorrectionDeadZone)
   {
       // SpaceGame's defaults (config-tunable there; explicit struct here).
       public static PredictionSettings Default => new(
           TickSeconds: 1f / 60f,
           MaxPendingCommands: 256,
           HardSnapDistance: 100f,
           CorrectionRate: 8f,
           CorrectionDeadZone: 1.5f);
   }

   public readonly record struct ReconciliationResult(
       int AuthoritativeTick,
       float PositionError,
       bool HardSnapApplied);

   public sealed class ClientPrediction<TState, TCommand>
       where TState : IPredictedState<TState>
   {
       public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null);
       public void Reset(in TState initialState);
       public int Predict(in TCommand command);                                   // returns assigned seq
       public ReconciliationResult Reconcile(int authoritativeTick, in TState authoritativeBasis, int lastAcknowledgedSeq);
       public void AdvancePresentation(float elapsedSeconds);
       public TState PredictedState { get; }
       public TState RenderedState { get; }                                       // PredictedState with render offset
   }
   ```
   Engine owns: seq-keyed pending-command buffer (`SortedList<int,TCommand>`), `MaxPendingCommands`
   oldest-drop, ack-based pending pruning, rebase to `authoritativeBasis` + replay of unacked commands
   through `simulator.Step(..., TickSeconds)`, decaying render-offset smoothing with hard-snap and
   dead-zone exactly as `ClientPredictionState`. The game supplies physics (`ITickSimulator`, including
   its own dequantize + bounds clamp) and state shape (`IPredictedState`).

   Reconcile semantics preserved from source: capture `previousRendered = PredictedState.Position +
   offset` before rebase; after replay, `error = previousRendered - newPredicted.Position`; hard-snap
   when `|error| >= HardSnapDistance` (offset zeroed), ignore when `|error| <= CorrectionDeadZone`,
   else offset = error and decays in `AdvancePresentation`.

3. **`RemoteCommandQueue<TCommand>`** — generalizes `RemotePlayerCommandQueue` (host-side, same source
   file, determinism-neutral).
   ```csharp
   public sealed class RemoteCommandQueue<TCommand>
   {
       public RemoteCommandQueue(TCommand neutralCommand);   // returned when a slot queue is empty
       public void Reset();
       public void Store(int slot, int seq, in TCommand command);   // ignores seq < 0 and duplicate (slot,seq)
       public TCommand Dequeue(int slot, out int lastAcknowledgedSeq);
       public int GetLastAcknowledgedSeq(int slot);
   }
   ```
   Decoupled from `PlayerCommandDto`: takes `(slot, seq, command)` instead of a DTO. The game supplies
   its neutral/no-op command for empty slots.

### `KhaozEngine.Netcode.LiteNetLib`

Refs `LiteNetLib 2.1.2`. Independent of `KhaozEngine.Netcode` (no cross-ref needed). `net10.0`,
nullable on, `InternalsVisibleTo KhaozEngine.Tests`, ships `README.md`.

```csharp
public enum NetChannelReliability { UnreliableSequenced, ReliableOrdered }

public interface IChannelSplittable<TSelf>
{
    bool HasUnreliableContent { get; }
    bool HasReliableContent { get; }
    TSelf ExtractUnreliable();
    TSelf ExtractReliable();
}

public static class ChannelSplitter
{
    public static DeliveryMethod ToDeliveryMethod(NetChannelReliability reliability);
        // UnreliableSequenced -> DeliveryMethod.Sequenced; ReliableOrdered -> DeliveryMethod.ReliableOrdered

    public static void Send<T>(T batch, Action<T, DeliveryMethod> send) where T : IChannelSplittable<T>;
        // if batch.HasUnreliableContent: send(batch.ExtractUnreliable(), Sequenced)
        // if batch.HasReliableContent:   send(batch.ExtractReliable(),   ReliableOrdered)
}
```
The game implements `IChannelSplittable<EntityUpdateBatchDto>` (its `HasPositionContent` ->
`HasUnreliableContent`, `ExtractPositions` -> `ExtractUnreliable`, etc.). The engine drives "split, send
each non-empty part on its channel" — the head-of-line-blocking fix, transport-mapped.

## Tests (`KhaozEngine.Tests`, headless, no GraphicsDevice; references both new packages)

- **`UnitAxisQuantizer`**: pinned exact values — `Quantize(0)=0`, `Quantize(1)=127`, `Quantize(-1)=-127`,
  `Quantize(0.5)=64` (away-from-zero on `63.5`), `Quantize(-0.5)=-64`, clamp `Quantize(2f)=127`,
  `Quantize(-2f)=-127`; `Dequantize(127)=1`, `Dequantize(0)=0`; round-trip error <= `1/127`. These pin
  the determinism contract.
- **`ClientPrediction`**: deterministic fake simulator (e.g. `Step` integrates a fixed delta from the
  command). Cases: `Predict` returns increasing seq and advances `PredictedState`; `Reconcile` with a
  matching basis + ack yields ~zero error, zero offset, `PredictedState == RenderedState`; a
  misprediction sets a non-zero offset that `AdvancePresentation` decays to zero; error >=
  `HardSnapDistance` zeroes the offset immediately (`HardSnapApplied == true`); error <=
  `CorrectionDeadZone` is ignored; acked commands are pruned and not replayed; `MaxPendingCommands`
  bound drops oldest.
- **`RemoteCommandQueue`**: stores + dequeues in seq order; duplicate `(slot,seq)` ignored; `seq < 0`
  ignored; empty slot returns the neutral command; `lastAcknowledgedSeq` tracks highest dequeued and is
  reported on the empty-queue path too; slots are isolated; `Reset` clears.
- **`ChannelSplitter`**: a fake `IChannelSplittable` records which parts were sent; `Send` invokes the
  callback once per non-empty part with the correct `DeliveryMethod`, skips empty parts, sends nothing
  when both empty; `ToDeliveryMethod` mapping is asserted.

## Wiring

- Add `KhaozEngine.Netcode/KhaozEngine.Netcode.csproj` and
  `KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj` to `KhaozEngine.slnx`.
- Add both as `ProjectReference` in `KhaozEngine.Tests.csproj`.
- Each package ships a `README.md` (packed) per repo convention.

## Release ritual (per CLAUDE.md)

- **Version**: provisional `4.5.0` (additive -> minor). See "Version coordination" — the actual number
  may need to move to `4.6.0`/`4.7.0` at merge depending on the other two in-flight release branches.
- Bump `<Version>` in `Directory.Build.props`.
- `CHANGELOG.md`: newest-first entry for the new packages.
- Update the three guard declarations: `docs/CONSUMERS.md` "Engine current version", `docs/ROADMAP.md`
  "Current released version", README `<PackageReference>` example. Run `scripts/check-doc-versions.sh`.
- `docs/CONSUMERS.md`: add `Netcode` + `Netcode.LiteNetLib` columns to the version + adoption matrices
  (all consumers `-`; none adopt yet).
- `dotnet test` green, then `dotnet pack -c Release -o ./local-feed`.
- Commit per item; single version bump at the end; `git tag v<ver>`; push `main` + tag.

## Version coordination (IMPORTANT — concurrent release collision)

At design time, `v4.4.0` is already tagged on `main` for `KhaozEngine.Platform`. Two other worktrees
are in flight and each ALSO bumped to `4.4.0` (now stale):

- `worktree-feature+collision-pooling` — Collision + Pooling packages.
- `worktree-feature+updates-package` — Updates package.

Whichever of the three remaining branches merges first takes `4.5.0`; the others must rebase onto
`main` and bump again (`4.6.0`, `4.7.0`). This spec uses `4.5.0` provisionally; reconcile the real
number against `main` immediately before the final bump/tag.

## Out of scope

- No SpaceGame changes (adoption is a separate, hash-gated consumer task).
- No N-bit / arbitrary-range quantizer generalization (YAGNI; ship the proven signed-byte unit-axis
  scheme that the determinism contract is pinned to).
- No transport abstraction beyond LiteNetLib (only mapping target today).
