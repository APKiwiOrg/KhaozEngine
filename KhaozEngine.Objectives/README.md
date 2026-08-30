# KhaozEngine.Objectives

Game-agnostic objective / goal tracking for achievements, challenges, quests, and dailies. One reusable
pipeline every game hooks: **signals -> counters -> declarative conditions -> completion event**. In the
`Foundation` umbrella (usable by clients and headless servers alike). Deterministic, presentation-free, and
persistence-agnostic. No JSON, no external deps beyond `KhaozEngine.App` (for `StringId`).

The framework never names a domain concept. It knows opaque metric-key strings, numeric targets, and scopes.
"depth" / "ore" / "bars" / "enemies" are the game's words, passed as strings.

## The pipeline

1. **The game reports named metrics.** `Report("ore.mined", 1)` accumulates; `Observe("depth.max", 50)` tracks a
   peak. Keys are opaque strings the game chose.
2. **The tracker holds counters in scopes.** `Persistent` (never resets) and `Session` (resets on demand). One
   Report / Observe updates both. The game calls `ResetScope(MetricScope.Session)` at its own run / prestige
   boundary. The framework never knows what a "run" is - `ResetScope` is the entire "all-time vs single-run"
   mechanism.
3. **Objectives are declarative conditions over metrics** (pure data the game supplies): `AtLeast(key, target,
   scope)` (accumulator >=), `Reached(key, target, scope)` (peak >=, for "reach depth N"), `AtMost(key, target,
   scope)` (constraint / negative goals), AND-composed. An `AtMost` is a constraint rather than a goal: it holds
   until it is violated. Pair it with an `AtLeast` / `Reached` that gates it, or see the constraint-only rule
   below.
4. **Only objectives watching the changed key are re-evaluated** (indexed by key) - the perf contract, never a
   full scan.
5. **On completion, `ObjectiveCompleted` fires.** Idempotent: completes once, stays completed, never re-fires,
   survives Capture / Restore.

## `ObjectiveTracker`

```csharp
public sealed class ObjectiveTracker
{
    event Action<ObjectiveCompletion>? ObjectiveCompleted;

    void Register(ObjectiveDefinition definition);
    void RegisterRange(IEnumerable<ObjectiveDefinition> definitions);

    void Report(string key, double amount = 1);   // accumulator (Sum) -> AtLeast / AtMost
    void Observe(string key, double value);        // peak (Max)       -> Reached
    void ResetScope(MetricScope scope);            // the run boundary

    bool IsComplete(string objectiveId);
    bool IsRegistered(string objectiveId);
    double GetSum(string key, MetricScope scope);
    double GetMax(string key, MetricScope scope);
    ObjectiveProgress GetProgress(string objectiveId);           // completion + per-condition (current, target)
    IReadOnlyList<ObjectiveProgress> GetAllProgress();
    void EvaluateAll();                                          // surface patched-in already-satisfied objectives

    ObjectivesSnapshot Capture();                  // game folds this into its own save
    void Restore(ObjectivesSnapshot snapshot);
}
```

Metric model: each key holds a `Sum` (fed by `Report`) and a `Max` (fed by `Observe`) per scope. `AtLeast` /
`AtMost` read `Sum`; `Reached` reads `Max`. So one key can back both an accumulator goal and a peak goal without
ambiguity.

## Integration contract (the entire surface a game touches)

1. Register objective definitions from the game's own data pipeline (the framework provides the model +
   registration; it owns no JSON).
2. `Report` / `Observe` at event sites.
3. `ResetScope(MetricScope.Session)` at the run boundary.
4. Subscribe to `ObjectiveCompleted`.
5. `Capture` / `Restore` a serializable snapshot via the game's own save.

Recommended lifecycle: **subscribe -> Register -> Restore**. Register-before-Restore is preferred but not
required - a completed id restored ahead of its definition binds silently when the definition registers.

**Constraint-only objectives need step 6: `EvaluateAll()` at the run boundary.** An objective whose every
condition is an `AtMost` ("finish the level buying no upgrades") holds on empty counters, so nothing derives its
completion: not `Register`, not a `Report`, not `Restore`. On an empty counter set there is no way to tell "not
violated" from "not started", and only the game knows when the run ended. Call `EvaluateAll()` at that point and
the surviving constraints complete. Mixed objectives need none of this: their `AtLeast` / `Reached` condition
already gates them, and they complete on the report that satisfies it.

## The seam (stays game-side)

- **Rewards / points / trees.** An objective carries an opaque `Metadata` payload the framework echoes back on
  `ObjectiveCompletion.Metadata`. The game reads it (e.g. a `tier:hard` tag to pick a point pool / tree node) -
  the framework knows none of it.
- **Save transport.** `Capture` exposes a plain snapshot; the game serializes it (System.Text.Json, etc.) into
  its own save. The framework takes no serialization dependency.
- **Display text.** Presentation-free core. An objective optionally references a localized `Name` / `Description`
  by `StringId` (never a raw literal), for a progress log.

## Usage

```csharp
using KhaozEngine.Objectives;

var tracker = new ObjectiveTracker();
tracker.ObjectiveCompleted += c => Award(c.ObjectiveId, c.Metadata);

// Declarative definitions from the game's data pipeline.
tracker.Register(ObjectiveDefinition.Create("copper.master",
    ObjectiveCondition.AtLeast("bars.copper", 500, MetricScope.Persistent)));

tracker.Register(new ObjectiveDefinition("deep.no.upgrades",
    conditions: new[]
    {
        ObjectiveCondition.Reached("depth.max", 100, MetricScope.Session),
        ObjectiveCondition.AtMost("upgrades.bought", 0, MetricScope.Session),
    },
    metadata: "tier:hard"));

tracker.Restore(savedSnapshot);   // after Register

// Event sites.
tracker.Report("bars.copper", 3);
tracker.Observe("depth.max", 120);

// Run boundary (the game decides what a run is).
tracker.ResetScope(MetricScope.Session);

// Progress log without any bookkeeping of your own.
foreach (var p in tracker.GetAllProgress())
    foreach (var cond in p.Conditions)
        DrawBar(cond.Current, cond.Target, cond.IsSatisfied);

// Save.
var snapshot = tracker.Capture();   // game serializes + writes this
```

## Out of scope (v1)

Temporal / sequential conditions ("X then Y within 10s") and any reward / currency / tree logic.

## Testing

Fully headless. See `KhaozEngine.Game.Tests/Objectives/` (accumulation + Observe-max across scopes, ResetScope
clears Session not Persistent, each condition kind + AND-composition, completion fires exactly once and does not
re-fire after Restore, snapshot round-trip, index-by-key guard, progress introspection, and the Nullwake
reference consumer).

```bash
dotnet test KhaozEngine.Game.Tests/KhaozEngine.Game.Tests.csproj
```
