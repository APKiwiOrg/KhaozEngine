# Netcode Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship two new additive KhaozEngine packages (`KhaozEngine.Netcode` for quantization + client prediction/reconciliation + host command queue, and `KhaozEngine.Netcode.LiteNetLib` for the reliable/unreliable channel-split kernel), generalized from SpaceGame's netcode.

**Architecture:** Pure machinery (no transport dep) lives in `KhaozEngine.Netcode`, refs MonoGame only for `Vector2`/`MathHelper`. The LiteNetLib channel mapping lives in a separate `KhaozEngine.Netcode.LiteNetLib` package. The game keeps its own command/state/batch types and field layout; the engine exposes primitives, generics, and an interface + driver.

**Tech Stack:** net10.0, C# (nullable on, ImplicitUsings off), MonoGame.Framework.DesktopGL 3.8.*, LiteNetLib 2.1.2, xUnit.

**Determinism gate:** `UnitAxisQuantizer` must round bit-identically to SpaceGame's `TickCommandCodec`: its dequantize feeds the host-authoritative sim (hash `17709480852979803671`). Tests pin the exact values. No SpaceGame change in this plan; the hash risk lands at adoption (separate task).

**Version:** `4.7.0`. `4.4.0` (Platform), `4.5.0` (Collision+Pooling), and `4.6.0` (Updates) are all taken on `main` as of the rebase. Re-confirm `4.7.0` is still free in Task 8 Step 1 before bumping.

---

### Task 1: `KhaozEngine.Netcode` project + `UnitAxisQuantizer`

**Files:**
- Create: `KhaozEngine.Netcode/KhaozEngine.Netcode.csproj`
- Create: `KhaozEngine.Netcode/UnitAxisQuantizer.cs`
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Test: `KhaozEngine.Tests/Netcode/UnitAxisQuantizerTests.cs`

- [ ] **Step 1: Create the package project**

`KhaozEngine.Netcode/KhaozEngine.Netcode.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Netcode</PackageId>
    <Description>Game-agnostic, transport-free netcode primitives for MonoGame. UnitAxisQuantizer (signed-byte 8-bit quantization of a unit-range [-1,1] axis, the wire codec scheme; deterministic, round-trips within 1/127). ClientPrediction&lt;TState,TCommand&gt; (client-side prediction + authoritative reconciliation: seq-keyed pending-command buffer, ack-prune, rebase + replay of unacked commands, and decaying render-offset error smoothing with hard-snap and dead-zone) over IPredictedState/ITickSimulator with a PredictionSettings struct. RemoteCommandQueue&lt;TCommand&gt; (host-side per-slot seq-ordered command queue with duplicate/negative-seq rejection and last-acknowledged-seq tracking). The game keeps its own command/state types and packed field layout.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MonoGame.Framework.DesktopGL" Version="3.8.*" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

Also create a placeholder `KhaozEngine.Netcode/README.md` (full content in Task 6) so the project loads:
```markdown
# KhaozEngine.Netcode

Transport-free netcode primitives. See repo CHANGELOG.
```

- [ ] **Step 2: Add the project to the solution and test project**

In `KhaozEngine.slnx`, add after the Localization line:
```xml
  <Project Path="KhaozEngine.Netcode/KhaozEngine.Netcode.csproj" />
```

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add after the Localization `ProjectReference`:
```xml
    <ProjectReference Include="../KhaozEngine.Netcode/KhaozEngine.Netcode.csproj" />
```

- [ ] **Step 3: Write the failing test**

`KhaozEngine.Tests/Netcode/UnitAxisQuantizerTests.cs`:
```csharp
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class UnitAxisQuantizerTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 127)]
    [InlineData(-1f, -127)]
    [InlineData(0.5f, 64)]    // 0.5 * 127 = 63.5, rounded away-from-zero
    [InlineData(-0.5f, -64)]
    [InlineData(2f, 127)]     // clamped
    [InlineData(-2f, -127)]   // clamped
    public void Quantize_MatchesPinnedValues(float value, int expected)
    {
        Assert.Equal((sbyte)expected, UnitAxisQuantizer.Quantize(value));
    }

    [Theory]
    [InlineData((sbyte)0, 0f)]
    [InlineData((sbyte)127, 1f)]
    [InlineData((sbyte)-127, -1f)]
    public void Dequantize_MatchesPinnedValues(sbyte value, float expected)
    {
        Assert.Equal(expected, UnitAxisQuantizer.Dequantize(value), 5);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(-0.8f)]
    [InlineData(0.999f)]
    public void RoundTrip_WithinOneStep(float value)
    {
        float restored = UnitAxisQuantizer.Dequantize(UnitAxisQuantizer.Quantize(value));
        Assert.True(System.MathF.Abs(restored - value) <= 1f / 127f);
    }
}
```

- [ ] **Step 4: Run the test, verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter UnitAxisQuantizerTests`
Expected: build error: `UnitAxisQuantizer` does not exist.

- [ ] **Step 5: Implement `UnitAxisQuantizer`**

`KhaozEngine.Netcode/UnitAxisQuantizer.cs`:
```csharp
using System;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Netcode;

/// <summary>
/// 8-bit quantization of a unit-range axis (move/aim component) to a signed byte and back.
/// The wire codec scheme: quantization rounds away-from-zero so it is symmetric about zero.
/// Determinism note: SpaceGame dequantizes commands before they enter the host-authoritative sim,
/// so this rounding is hash-gated; it must not change.
/// </summary>
public static class UnitAxisQuantizer
{
    /// <summary>Clamp <paramref name="value"/> to [-1,1] and quantize to [-127,127].</summary>
    public static sbyte Quantize(float value)
        => (sbyte)MathF.Round(MathHelper.Clamp(value, -1f, 1f) * 127f, MidpointRounding.AwayFromZero);

    /// <summary>Dequantize a signed byte back to [-1,1].</summary>
    public static float Dequantize(sbyte value) => value / 127f;
}
```

- [ ] **Step 6: Run the test, verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter UnitAxisQuantizerTests`
Expected: PASS (all theories green).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Netcode KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj
git commit -m "feat(netcode): KhaozEngine.Netcode package + UnitAxisQuantizer"
```

---

### Task 2: Client prediction (`IPredictedState`, `ITickSimulator`, `PredictionSettings`, `ReconciliationResult`, `ClientPrediction`)

**Files:**
- Create: `KhaozEngine.Netcode/IPredictedState.cs`
- Create: `KhaozEngine.Netcode/ITickSimulator.cs`
- Create: `KhaozEngine.Netcode/PredictionSettings.cs`
- Create: `KhaozEngine.Netcode/ClientPrediction.cs`
- Test: `KhaozEngine.Tests/Netcode/ClientPredictionTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Netcode/ClientPredictionTests.cs`:
```csharp
using KhaozEngine.Netcode;
using Microsoft.Xna.Framework;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ClientPredictionTests
{
    // Command = a velocity (units/sec). Deterministic: position += command * dt.
    private readonly record struct FakeState(Vector2 Position) : IPredictedState<FakeState>
    {
        public FakeState WithPosition(Vector2 position) => this with { Position = position };
    }

    private sealed class MoveSimulator : ITickSimulator<FakeState, Vector2>
    {
        public FakeState Step(in FakeState state, in Vector2 command, float dt)
            => state.WithPosition(state.Position + command * dt);
    }

    private static ClientPrediction<FakeState, Vector2> NewPrediction(PredictionSettings? settings = null)
    {
        var p = new ClientPrediction<FakeState, Vector2>(new MoveSimulator(), settings);
        p.Reset(new FakeState(Vector2.Zero));
        return p;
    }

    [Fact]
    public void Predict_AssignsIncreasingSeq_AndAdvancesState()
    {
        var p = NewPrediction();
        int s0 = p.Predict(new Vector2(60f, 0f)); // 60 * (1/60) = 1 unit
        int s1 = p.Predict(new Vector2(60f, 0f));
        Assert.Equal(0, s0);
        Assert.Equal(1, s1);
        Assert.Equal(2f, p.PredictedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_MatchingBasis_ZeroErrorNoOffset()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f));
        p.Predict(new Vector2(60f, 0f)); // predicted X = 2
        var r = p.Reconcile(authoritativeTick: 1, new FakeState(new Vector2(2f, 0f)), lastAcknowledgedSeq: 1);
        Assert.Equal(0f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_Misprediction_SetsOffset_ThatDecaysToZero()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // seq 0, predicted X = 10
        // basis shifted +5, seq 0 still unacked -> replay puts predicted at 15, rendered stays at 10
        var r = p.Reconcile(1, new FakeState(new Vector2(5f, 0f)), lastAcknowledgedSeq: -1);
        Assert.Equal(5f, r.PositionError, 3);
        Assert.False(r.HardSnapApplied);
        Assert.Equal(15f, p.PredictedState.Position.X, 3);
        Assert.Equal(10f, p.RenderedState.Position.X, 3);
        for (int i = 0; i < 300; i++) p.AdvancePresentation(1f / 60f);
        Assert.Equal(p.PredictedState.Position.X, p.RenderedState.Position.X, 3);
    }

    [Fact]
    public void Reconcile_LargeError_HardSnaps_NoOffset()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(600f, 0f)); // predicted X = 10
        var r = p.Reconcile(1, new FakeState(new Vector2(200f, 0f)), lastAcknowledgedSeq: -1);
        Assert.True(r.HardSnapApplied);
        Assert.Equal(210f, p.PredictedState.Position.X, 3);
        Assert.Equal(210f, p.RenderedState.Position.X, 3); // snapped, no smoothing
    }

    [Fact]
    public void Reconcile_AcknowledgedCommands_ArePruned()
    {
        var p = NewPrediction();
        p.Predict(new Vector2(60f, 0f)); // 0
        p.Predict(new Vector2(60f, 0f)); // 1
        p.Predict(new Vector2(60f, 0f)); // 2
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: 2); // all acked, none replayed
        Assert.Equal(0f, p.PredictedState.Position.X, 3);
        Assert.Equal(3, p.Predict(new Vector2(60f, 0f))); // next seq continues
    }

    [Fact]
    public void MaxPendingCommands_DropsOldest()
    {
        var p = NewPrediction(PredictionSettings.Default with { MaxPendingCommands = 4 });
        for (int i = 0; i < 6; i++) p.Predict(new Vector2(60f, 0f)); // 6 predicted -> X = 6
        // nothing acked; only the last 4 commands remain to replay from origin -> X = 4
        p.Reconcile(1, new FakeState(Vector2.Zero), lastAcknowledgedSeq: -1);
        Assert.Equal(4f, p.PredictedState.Position.X, 3);
    }
}
```

- [ ] **Step 2: Run the test, verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ClientPredictionTests`
Expected: build error: types not defined.

- [ ] **Step 3: Implement the interfaces and value types**

`KhaozEngine.Netcode/IPredictedState.cs`:
```csharp
using Microsoft.Xna.Framework;

namespace KhaozEngine.Netcode;

/// <summary>
/// A predicted local state whose position participates in reconciliation error smoothing.
/// </summary>
/// <typeparam name="TSelf">The implementing state type (CRTP), so WithPosition stays strongly typed.</typeparam>
public interface IPredictedState<TSelf>
{
    /// <summary>World position used to measure and smooth reconciliation error.</summary>
    Vector2 Position { get; }

    /// <summary>Returns a copy of this state with <paramref name="position"/> applied.</summary>
    TSelf WithPosition(Vector2 position);
}
```

`KhaozEngine.Netcode/ITickSimulator.cs`:
```csharp
namespace KhaozEngine.Netcode;

/// <summary>
/// The game's deterministic per-tick step: advances a state by one command over <paramref name="dt"/>.
/// Used both to predict forward locally and to replay unacknowledged commands during reconciliation,
/// so the same function must drive both paths.
/// </summary>
public interface ITickSimulator<TState, TCommand>
{
    TState Step(in TState state, in TCommand command, float dt);
}
```

`KhaozEngine.Netcode/PredictionSettings.cs`:
```csharp
namespace KhaozEngine.Netcode;

/// <summary>Tunables for <see cref="ClientPrediction{TState,TCommand}"/>.</summary>
public readonly record struct PredictionSettings(
    float TickSeconds,
    int MaxPendingCommands,
    float HardSnapDistance,
    float CorrectionRate,
    float CorrectionDeadZone)
{
    /// <summary>SpaceGame's defaults: 60 Hz tick, 256-command buffer, 100u snap, rate 8, 1.5u dead-zone.</summary>
    public static PredictionSettings Default => new(
        TickSeconds: 1f / 60f,
        MaxPendingCommands: 256,
        HardSnapDistance: 100f,
        CorrectionRate: 8f,
        CorrectionDeadZone: 1.5f);
}

/// <summary>Outcome of a <see cref="ClientPrediction{TState,TCommand}.Reconcile"/> call.</summary>
public readonly record struct ReconciliationResult(
    int AuthoritativeTick,
    float PositionError,
    bool HardSnapApplied);
```

- [ ] **Step 4: Implement `ClientPrediction`**

`KhaozEngine.Netcode/ClientPrediction.cs`:
```csharp
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace KhaozEngine.Netcode;

/// <summary>
/// Client-side prediction with authoritative reconciliation. Each command is predicted locally and
/// retained until the host acknowledges it by sequence number. On every authoritative snapshot the
/// predicted state is rebased to the server basis and the unacknowledged commands are re-simulated on
/// top, so with matching physics no per-snapshot drift accumulates; only a genuine misprediction
/// produces a correction, smoothed via a decaying render offset so it never pops on screen.
/// </summary>
public sealed class ClientPrediction<TState, TCommand>
    where TState : IPredictedState<TState>
{
    private readonly ITickSimulator<TState, TCommand> simulator;
    private readonly PredictionSettings settings;
    private readonly SortedList<int, TCommand> pendingCommands = new();
    private TState predictedState;
    private Vector2 renderOffset;
    private int nextSeq;

    public ClientPrediction(ITickSimulator<TState, TCommand> simulator, PredictionSettings? settings = null)
    {
        this.simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        this.settings = settings ?? PredictionSettings.Default;
    }

    /// <summary>The current predicted (authority-tracking) state.</summary>
    public TState PredictedState => predictedState;

    /// <summary>The predicted state with the smoothing offset applied (what to draw).</summary>
    public TState RenderedState => predictedState.WithPosition(predictedState.Position + renderOffset);

    public void Reset(in TState initialState)
    {
        predictedState = initialState;
        pendingCommands.Clear();
        renderOffset = Vector2.Zero;
        nextSeq = 0;
    }

    /// <summary>Predicts one command forward and retains it for reconciliation. Returns its seq.</summary>
    public int Predict(in TCommand command)
    {
        int seq = nextSeq++;
        pendingCommands[seq] = command;
        predictedState = simulator.Step(predictedState, command, settings.TickSeconds);
        if (pendingCommands.Count > settings.MaxPendingCommands)
        {
            // Bound memory if acknowledgements stop arriving (sustained loss); drop the oldest.
            pendingCommands.RemoveAt(0);
        }

        return seq;
    }

    /// <summary>
    /// Rebases to <paramref name="authoritativeBasis"/>, drops commands the host has acknowledged
    /// (seq &lt;= <paramref name="lastAcknowledgedSeq"/>), replays the rest, and smooths any visible
    /// correction. Large errors hard-snap; sub-dead-zone errors are ignored as float jitter.
    /// </summary>
    public ReconciliationResult Reconcile(int authoritativeTick, in TState authoritativeBasis, int lastAcknowledgedSeq)
    {
        Vector2 previousRenderedPosition = predictedState.Position + renderOffset;

        while (pendingCommands.Count > 0 && pendingCommands.Keys[0] <= lastAcknowledgedSeq)
        {
            pendingCommands.RemoveAt(0);
        }

        TState replayed = authoritativeBasis;
        for (int i = 0; i < pendingCommands.Count; i++)
        {
            replayed = simulator.Step(replayed, pendingCommands.Values[i], settings.TickSeconds);
        }

        predictedState = replayed;

        Vector2 error = previousRenderedPosition - predictedState.Position;
        float positionError = error.Length();
        bool hardSnapApplied = positionError >= settings.HardSnapDistance;
        renderOffset = (hardSnapApplied || positionError <= settings.CorrectionDeadZone) ? Vector2.Zero : error;

        return new ReconciliationResult(authoritativeTick, positionError, hardSnapApplied);
    }

    /// <summary>Decays the smoothing offset toward zero; frame-rate independent within clamping.</summary>
    public void AdvancePresentation(float elapsedSeconds)
    {
        if (renderOffset == Vector2.Zero)
        {
            return;
        }

        float dt = MathF.Max(0f, elapsedSeconds);
        float blend = MathF.Min(1f, settings.CorrectionRate * dt);
        renderOffset = Vector2.Lerp(renderOffset, Vector2.Zero, blend);
        if (renderOffset.LengthSquared() <= settings.CorrectionDeadZone * settings.CorrectionDeadZone)
        {
            renderOffset = Vector2.Zero;
        }
    }
}
```

- [ ] **Step 5: Run the test, verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ClientPredictionTests`
Expected: PASS (6 facts green).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Netcode KhaozEngine.Tests/Netcode/ClientPredictionTests.cs
git commit -m "feat(netcode): generic ClientPrediction with reconciliation + smoothing"
```

---

### Task 3: `RemoteCommandQueue<TCommand>`

**Files:**
- Create: `KhaozEngine.Netcode/RemoteCommandQueue.cs`
- Test: `KhaozEngine.Tests/Netcode/RemoteCommandQueueTests.cs`

- [ ] **Step 1: Write the failing test**

`KhaozEngine.Tests/Netcode/RemoteCommandQueueTests.cs`:
```csharp
using KhaozEngine.Netcode;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class RemoteCommandQueueTests
{
    private static RemoteCommandQueue<int> NewQueue() => new(neutralCommand: -999);

    [Fact]
    public void Dequeue_InSeqOrder_RegardlessOfStoreOrder()
    {
        var q = NewQueue();
        q.Store(slot: 0, seq: 2, command: 22);
        q.Store(slot: 0, seq: 0, command: 20);
        q.Store(slot: 0, seq: 1, command: 21);
        Assert.Equal(20, q.Dequeue(0, out int a0)); Assert.Equal(0, a0);
        Assert.Equal(21, q.Dequeue(0, out int a1)); Assert.Equal(1, a1);
        Assert.Equal(22, q.Dequeue(0, out int a2)); Assert.Equal(2, a2);
    }

    [Fact]
    public void Store_Duplicate_IsIgnored()
    {
        var q = NewQueue();
        q.Store(0, 0, 100);
        q.Store(0, 0, 999); // same (slot,seq) -> ignored, first value kept
        Assert.Equal(100, q.Dequeue(0, out _));
    }

    [Fact]
    public void Store_NegativeSeq_IsIgnored()
    {
        var q = NewQueue();
        q.Store(0, -1, 5);
        Assert.Equal(-999, q.Dequeue(0, out int ack)); // neutral
        Assert.Equal(-1, ack);
    }

    [Fact]
    public void Dequeue_EmptySlot_ReturnsNeutral_AndLastAck()
    {
        var q = NewQueue();
        q.Store(0, 0, 7);
        q.Dequeue(0, out _); // ack now 0
        Assert.Equal(-999, q.Dequeue(0, out int ack)); // empty -> neutral, but ack preserved
        Assert.Equal(0, ack);
    }

    [Fact]
    public void Slots_AreIsolated()
    {
        var q = NewQueue();
        q.Store(0, 0, 10);
        q.Store(1, 0, 20);
        Assert.Equal(20, q.Dequeue(1, out _));
        Assert.Equal(10, q.Dequeue(0, out _));
        Assert.Equal(0, q.GetLastAcknowledgedSeq(0));
        Assert.Equal(0, q.GetLastAcknowledgedSeq(1));
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(2)); // untouched slot
    }

    [Fact]
    public void Reset_Clears()
    {
        var q = NewQueue();
        q.Store(0, 0, 1);
        q.Dequeue(0, out _);
        q.Reset();
        Assert.Equal(-1, q.GetLastAcknowledgedSeq(0));
        Assert.Equal(-999, q.Dequeue(0, out _));
    }
}
```

- [ ] **Step 2: Run the test, verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter RemoteCommandQueueTests`
Expected: build error: `RemoteCommandQueue` not defined.

- [ ] **Step 3: Implement `RemoteCommandQueue`**

`KhaozEngine.Netcode/RemoteCommandQueue.cs`:
```csharp
using System.Collections.Generic;

namespace KhaozEngine.Netcode;

/// <summary>
/// Host-side per-slot command queue. Commands arrive tagged with a monotonic sequence number; the host
/// dequeues them in seq order once per simulation tick, independent of tick-number alignment between
/// client and host. Duplicate deliveries (the client's redundancy retransmit) are silently ignored.
/// Determinism-neutral: it only orders and de-duplicates, never altering command values.
/// </summary>
public sealed class RemoteCommandQueue<TCommand>
{
    private readonly Dictionary<int, SortedList<int, TCommand>> queuesBySlot = new();
    private readonly Dictionary<int, int> lastAcknowledgedSeqBySlot = new();
    private readonly TCommand neutralCommand;

    /// <param name="neutralCommand">Returned by <see cref="Dequeue"/> when a slot's queue is empty.</param>
    public RemoteCommandQueue(TCommand neutralCommand)
    {
        this.neutralCommand = neutralCommand;
    }

    public void Reset()
    {
        queuesBySlot.Clear();
        lastAcknowledgedSeqBySlot.Clear();
    }

    /// <summary>Stores a command. Negative seq and duplicate (slot, seq) pairs are ignored.</summary>
    public void Store(int slot, int seq, in TCommand command)
    {
        if (seq < 0)
        {
            return;
        }

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue))
        {
            queue = new SortedList<int, TCommand>();
            queuesBySlot[slot] = queue;
        }

        if (!queue.ContainsKey(seq))
        {
            queue[seq] = command;
        }
    }

    /// <summary>
    /// Dequeues the lowest-seq command for <paramref name="slot"/>, or the neutral command if empty.
    /// <paramref name="lastAcknowledgedSeq"/> reflects the highest seq processed so far (the host stamps
    /// this on its snapshot so the client can reconcile).
    /// </summary>
    public TCommand Dequeue(int slot, out int lastAcknowledgedSeq)
    {
        lastAcknowledgedSeq = lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);

        if (!queuesBySlot.TryGetValue(slot, out SortedList<int, TCommand>? queue) || queue.Count == 0)
        {
            return neutralCommand;
        }

        int seq = queue.Keys[0];
        TCommand command = queue.Values[0];
        queue.RemoveAt(0);
        lastAcknowledgedSeqBySlot[slot] = seq;
        lastAcknowledgedSeq = seq;
        return command;
    }

    public int GetLastAcknowledgedSeq(int slot) =>
        lastAcknowledgedSeqBySlot.GetValueOrDefault(slot, -1);
}
```

- [ ] **Step 4: Run the test, verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter RemoteCommandQueueTests`
Expected: PASS (6 facts green).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Netcode/RemoteCommandQueue.cs KhaozEngine.Tests/Netcode/RemoteCommandQueueTests.cs
git commit -m "feat(netcode): generic host-side RemoteCommandQueue"
```

---

### Task 4: `KhaozEngine.Netcode.LiteNetLib` project + `ChannelSplitter`

**Files:**
- Create: `KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj`
- Create: `KhaozEngine.Netcode.LiteNetLib/ChannelSplitter.cs`
- Create: `KhaozEngine.Netcode.LiteNetLib/README.md` (placeholder; full content in Task 6)
- Modify: `KhaozEngine.slnx`
- Modify: `KhaozEngine.Tests/KhaozEngine.Tests.csproj`
- Test: `KhaozEngine.Tests/Netcode/ChannelSplitterTests.cs`

- [ ] **Step 1: Create the package project**

`KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <PackageId>KhaozEngine.Netcode.LiteNetLib</PackageId>
    <Description>LiteNetLib channel-split kernel for entity-update batches. IChannelSplittable&lt;T&gt; lets a game's batch DTO declare its unreliable (position/transient, latest-wins) vs reliable (spawns/destroys/events, must-arrive-ordered) content and extract each sub-batch; ChannelSplitter.Send drives "split, send each non-empty part on its channel" (Sequenced vs ReliableOrdered) so reliable events never head-of-line-block position updates. ToDeliveryMethod maps NetChannelReliability to LiteNetLib's DeliveryMethod. The game keeps its own batch type and field layout.</Description>
    <PackageReadmeFile>README.md</PackageReadmeFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="LiteNetLib" Version="2.1.2" />
    <None Include="README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="KhaozEngine.Tests" />
  </ItemGroup>
</Project>
```

`KhaozEngine.Netcode.LiteNetLib/README.md` (placeholder):
```markdown
# KhaozEngine.Netcode.LiteNetLib

LiteNetLib channel-split kernel. See repo CHANGELOG.
```

- [ ] **Step 2: Add to solution and test project**

In `KhaozEngine.slnx`, add immediately after the `KhaozEngine.Netcode/KhaozEngine.Netcode.csproj` line:
```xml
  <Project Path="KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj" />
```

In `KhaozEngine.Tests/KhaozEngine.Tests.csproj`, add immediately after the `KhaozEngine.Netcode` `ProjectReference`:
```xml
    <ProjectReference Include="../KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj" />
```

- [ ] **Step 3: Write the failing test**

`KhaozEngine.Tests/Netcode/ChannelSplitterTests.cs`:
```csharp
using System.Collections.Generic;
using KhaozEngine.Netcode.LiteNetLib;
using LiteNetLib;
using Xunit;

namespace KhaozEngine.Tests.Netcode;

public class ChannelSplitterTests
{
    private readonly record struct FakeBatch(bool Unreliable, bool Reliable) : IChannelSplittable<FakeBatch>
    {
        public bool HasUnreliableContent => Unreliable;
        public bool HasReliableContent => Reliable;
        public FakeBatch ExtractUnreliable() => new(Unreliable: true, Reliable: false);
        public FakeBatch ExtractReliable() => new(Unreliable: false, Reliable: true);
    }

    [Fact]
    public void Send_BothContents_SendsTwoPartsOnCorrectChannels()
    {
        var sent = new List<(FakeBatch Batch, DeliveryMethod Delivery)>();
        ChannelSplitter.Send(new FakeBatch(true, true), (b, d) => sent.Add((b, d)));
        Assert.Equal(2, sent.Count);
        Assert.Equal(DeliveryMethod.Sequenced, sent[0].Delivery);
        Assert.True(sent[0].Batch.HasUnreliableContent);
        Assert.Equal(DeliveryMethod.ReliableOrdered, sent[1].Delivery);
        Assert.True(sent[1].Batch.HasReliableContent);
    }

    [Fact]
    public void Send_OnlyUnreliable_SendsOnePartSequenced()
    {
        var sent = new List<(FakeBatch, DeliveryMethod)>();
        ChannelSplitter.Send(new FakeBatch(true, false), (b, d) => sent.Add((b, d)));
        Assert.Single(sent);
        Assert.Equal(DeliveryMethod.Sequenced, sent[0].Item2);
    }

    [Fact]
    public void Send_Empty_SendsNothing()
    {
        var sent = new List<(FakeBatch, DeliveryMethod)>();
        ChannelSplitter.Send(new FakeBatch(false, false), (b, d) => sent.Add((b, d)));
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData(NetChannelReliability.UnreliableSequenced, DeliveryMethod.Sequenced)]
    [InlineData(NetChannelReliability.ReliableOrdered, DeliveryMethod.ReliableOrdered)]
    public void ToDeliveryMethod_Maps(NetChannelReliability reliability, DeliveryMethod expected)
    {
        Assert.Equal(expected, ChannelSplitter.ToDeliveryMethod(reliability));
    }
}
```

- [ ] **Step 4: Run the test, verify it fails to compile**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ChannelSplitterTests`
Expected: build error: types not defined.

- [ ] **Step 5: Implement `ChannelSplitter`**

`KhaozEngine.Netcode.LiteNetLib/ChannelSplitter.cs`:
```csharp
using System;
using LiteNetLib;

namespace KhaozEngine.Netcode.LiteNetLib;

/// <summary>Reliability class for an entity-update sub-batch.</summary>
public enum NetChannelReliability
{
    /// <summary>Position/transient state: latest value wins, stale packets may be dropped.</summary>
    UnreliableSequenced,

    /// <summary>Events that must arrive in order: spawns, destroys, collects, hits, game state.</summary>
    ReliableOrdered
}

/// <summary>
/// A batch that can be split into a reliable and an unreliable sub-batch for channel-separated sending.
/// </summary>
/// <typeparam name="TSelf">The implementing batch type (CRTP), so extraction stays strongly typed.</typeparam>
public interface IChannelSplittable<TSelf>
{
    bool HasUnreliableContent { get; }
    bool HasReliableContent { get; }

    /// <summary>The position/transient-only sub-batch (reliable fields nulled/cleared).</summary>
    TSelf ExtractUnreliable();

    /// <summary>The events-only sub-batch (position fields nulled/cleared).</summary>
    TSelf ExtractReliable();
}

/// <summary>
/// Splits a batch so position/transient state (Sequenced) is never head-of-line blocked by reliable
/// events (ReliableOrdered). Before splitting, one reliable event forced the whole batch (positions
/// included) onto the reliable channel, so a single lost packet stalled every later position update
/// until retransmit.
/// </summary>
public static class ChannelSplitter
{
    /// <summary>Maps a reliability class to LiteNetLib's delivery method.</summary>
    public static DeliveryMethod ToDeliveryMethod(NetChannelReliability reliability) => reliability switch
    {
        NetChannelReliability.UnreliableSequenced => DeliveryMethod.Sequenced,
        NetChannelReliability.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        _ => throw new ArgumentOutOfRangeException(nameof(reliability), reliability, "Unknown reliability.")
    };

    /// <summary>
    /// Invokes <paramref name="send"/> once per non-empty sub-batch with the matching delivery method:
    /// the unreliable part on Sequenced, the reliable part on ReliableOrdered. Empty parts are skipped.
    /// </summary>
    public static void Send<T>(T batch, Action<T, DeliveryMethod> send) where T : IChannelSplittable<T>
    {
        ArgumentNullException.ThrowIfNull(send);

        if (batch.HasUnreliableContent)
        {
            send(batch.ExtractUnreliable(), ToDeliveryMethod(NetChannelReliability.UnreliableSequenced));
        }

        if (batch.HasReliableContent)
        {
            send(batch.ExtractReliable(), ToDeliveryMethod(NetChannelReliability.ReliableOrdered));
        }
    }
}
```

- [ ] **Step 6: Run the test, verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter ChannelSplitterTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.Netcode.LiteNetLib KhaozEngine.slnx KhaozEngine.Tests/KhaozEngine.Tests.csproj KhaozEngine.Tests/Netcode/ChannelSplitterTests.cs
git commit -m "feat(netcode): KhaozEngine.Netcode.LiteNetLib channel-split kernel"
```

---

### Task 5: Full-suite green check

**Files:** none (verification only)

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all tests pass (the prior ~536 baseline plus the new Netcode tests; report the final count).

- [ ] **Step 2: If anything fails**, fix it before proceeding. Do not edit existing engine tests to make them pass: the new packages are additive and must not affect existing behavior.

---

### Task 6: Package READMEs

**Files:**
- Modify: `KhaozEngine.Netcode/README.md`
- Modify: `KhaozEngine.Netcode.LiteNetLib/README.md`

- [ ] **Step 1: Write `KhaozEngine.Netcode/README.md`**

```markdown
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
```

- [ ] **Step 2: Write `KhaozEngine.Netcode.LiteNetLib/README.md`**

```markdown
# KhaozEngine.Netcode.LiteNetLib

LiteNetLib channel-split kernel: send position/transient state on an unreliable Sequenced channel and
reliable events on a ReliableOrdered channel, so a lost reliable packet never head-of-line-blocks
position updates.

Implement `IChannelSplittable<T>` on your entity-update batch DTO, then let `ChannelSplitter` drive it:

```csharp
readonly record struct EntityBatch(/* ...fields... */) : IChannelSplittable<EntityBatch>
{
    public bool HasUnreliableContent => /* any position/transient field set */;
    public bool HasReliableContent   => /* any event field set */;
    public EntityBatch ExtractUnreliable() => /* copy with event fields nulled */;
    public EntityBatch ExtractReliable()   => /* copy with position fields nulled */;
}

ChannelSplitter.Send(batch, (part, delivery) => netManager.SendToAll(Serialize(part), delivery));
// -> unreliable part on DeliveryMethod.Sequenced, reliable part on DeliveryMethod.ReliableOrdered
```

`ChannelSplitter.ToDeliveryMethod(NetChannelReliability)` exposes the mapping if you send by hand.
```

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Netcode/README.md KhaozEngine.Netcode.LiteNetLib/README.md
git commit -m "docs(netcode): package READMEs"
```

---

### Task 7: CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add a newest-first entry above `## KhaozEngine 4.6.0`**

Insert (replace `4.7.0` with the reconciled version from Task 8 if it changed):
```markdown
## KhaozEngine 4.7.0

Additive. Two new packages extracting SpaceGame's reusable netcode. No change to existing packages.

### KhaozEngine.Netcode (new)

- New package: game-agnostic, transport-free netcode primitives (refs MonoGame for `Vector2`/`MathHelper`).
- `UnitAxisQuantizer`: 8-bit quantization of a unit-range `[-1,1]` axis to a signed byte and back
  (`Quantize` clamps then rounds `*127` away-from-zero; `Dequantize` is `v/127f`). The game keeps its
  own command record + packed field layout. Determinism: this rounding is sim-hash-relevant for any game
  that dequantizes commands before its host-authoritative deterministic sim, so the scheme is fixed.
- `ClientPrediction<TState,TCommand>`: client-side prediction + authoritative reconciliation. Seq-keyed
  pending-command buffer with oldest-drop bound, ack-prune, rebase to an authoritative basis + replay of
  unacknowledged commands, and decaying render-offset error smoothing with hard-snap and dead-zone. Game
  supplies `IPredictedState<TSelf>` (Position + WithPosition) and `ITickSimulator<TState,TCommand>`
  (one deterministic step); tunables via `PredictionSettings` (`PredictionSettings.Default` = 60 Hz,
  256-command buffer, 100u snap, rate 8, 1.5u dead-zone). Returns `ReconciliationResult`.
- `RemoteCommandQueue<TCommand>`: host-side per-slot, seq-ordered command queue. Dedups duplicate
  `(slot,seq)` and negative seqs, returns a caller-supplied neutral command for an empty slot, tracks
  the last-acknowledged seq per slot. Determinism-neutral (orders/dedups only).

### KhaozEngine.Netcode.LiteNetLib (new)

- New package: LiteNetLib channel-split kernel (refs `LiteNetLib 2.1.2`).
- `IChannelSplittable<TSelf>` + `ChannelSplitter.Send`: split a batch into its unreliable
  (position/transient, latest-wins) and reliable (spawns/destroys/events) parts and send each non-empty
  part on its own channel (Sequenced vs ReliableOrdered) so reliable events never head-of-line-block
  position updates. `NetChannelReliability` + `ChannelSplitter.ToDeliveryMethod` expose the mapping. The
  game keeps its own batch DTO and field layout.
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(netcode): CHANGELOG entry for 4.7.0 netcode packages"
```

---

### Task 8: Release ritual (version bump, doc guards, CONSUMERS, pack, tag)

**Files:**
- Modify: `Directory.Build.props`
- Modify: `docs/CONSUMERS.md`
- Modify: `docs/ROADMAP.md`
- Modify: `README.md`

- [ ] **Step 1: Re-confirm the version number is free**

```bash
git fetch
git tag --sort=-v:refname | head -5
git log --oneline origin/main -5
```
`4.4.0`/`4.5.0`/`4.6.0` are taken (Platform / Collision+Pooling / Updates). Use `4.7.0` if it is still
free. If another branch has merged and taken `4.7.0`, rebase onto `main` again and use the next free
number, updating every `4.7.0` below and the CHANGELOG heading from Task 7.

- [ ] **Step 2: Bump `Directory.Build.props`**

Change `<Version>4.6.0</Version>` to `<Version>4.7.0</Version>` (or the reconciled number).

- [ ] **Step 3: Update the three guard declarations**

- `docs/ROADMAP.md` line 3: `Current released version: **4.6.0**.` -> `**4.7.0**.`
- `README.md`: change each of the four `<PackageReference>` example lines from `Version="4.6.0"` to `Version="4.7.0"`.
- `docs/CONSUMERS.md`: change `**Engine current version:** \`4.6.0\`` to `\`4.7.0\``.

- [ ] **Step 4: Add Netcode columns to `docs/CONSUMERS.md` matrices**

In both the "Version matrix" and the "Adoption matrix" tables, append two columns: `Netcode` and
`Netcode.LiteNetLib`. For every consumer row put `-` (none adopt yet). Add the same two columns to the
header and the separator row of each table. Also update the prose note under "Version matrix" so it no
longer claims "all three are on 4.0.0" if that is stale; state the current adoption truthfully (the
existing matrix already shows 4.0.0 pins; leave consumer pins unchanged, only add the new `-` columns).

- [ ] **Step 5: Run the doc-version guard**

Run: `./scripts/check-doc-versions.sh`
Expected: `all engine-version declarations match 4.7.0` (or reconciled number). Fix any FAIL before continuing.

- [ ] **Step 6: Full test suite green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: all pass.

- [ ] **Step 7: Pack both new packages into the local feed**

Run:
```bash
mkdir -p local-feed
dotnet pack KhaozEngine.Netcode/KhaozEngine.Netcode.csproj -c Release -o ./local-feed
dotnet pack KhaozEngine.Netcode.LiteNetLib/KhaozEngine.Netcode.LiteNetLib.csproj -c Release -o ./local-feed
```
Expected: `KhaozEngine.Netcode.4.7.0.nupkg` and `KhaozEngine.Netcode.LiteNetLib.4.7.0.nupkg` (+ `.snupkg`)
written to `local-feed`. (Cumulative; do not delete older versions.)

- [ ] **Step 8: Commit the release**

```bash
git add Directory.Build.props docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "release(4.7.0): KhaozEngine.Netcode + .LiteNetLib packages, bump + docs"
```

- [ ] **Step 9: Tag**

```bash
git tag v4.7.0
```
(Do not push yet; pushing main + tag happens at branch finish, after the user picks a finish option.)

---

## Notes for the implementer

- All new types are `public`; `InternalsVisibleTo KhaozEngine.Tests` is convention, kept for parity.
- Do not touch SpaceGame in this plan. SpaceGame adoption (swapping its private quantizer to
  `UnitAxisQuantizer`, implementing `IPredictedState`/`ITickSimulator`/`IChannelSplittable`, wiring
  `RemoteCommandQueue`) is a separate, hash-gated consumer task: it must re-run SpaceGame's determinism
  suite and confirm the sim hash stays `17709480852979803671` before merging.
- Engine packages all share one version in `Directory.Build.props`; bump once (Task 8), not per package.
