# TileWorld post-movement hook design

**Status:** Complete in 18.40.0. Program [#858](https://github.com/APKiwiOrg/KhaozEngine/issues/858).

## Problem

`TileWorldServer.OnBeforeTick` runs before command drain. A consumer action that becomes due on the same tick as
an `Attack` or `WalkTo` command therefore sees the previous route and combat target. It can commit a durable
result before the newly admitted command becomes visible.

The existing interaction and combat events are later than the needed deadline. By then the engine has already
resolved the consequence that the consumer may need to cancel.

## Decision

Add one optional event:

```csharp
public event Action<float>? OnAfterMovement;
```

Raise it once per whole server simulation tick at this fixed boundary:

```text
OnBeforeTick
command and actor input
host.Tick
ProcessHandoffs
SyncGhosts
OnAfterMovement
ResolveActions
ResolveCombat
serve
```

The handler receives the configured tick duration. It sees applied player and actor movement, current combat and
interaction targets, the final owner after an in-process region handoff, and refreshed border ghosts. State it
writes is available to action and combat resolution and is encoded into an owner-cell snapshot served on the same
tick. Border ghosts receive the write at the next sync because this hook runs after the current sync has completed.

Frames that do not complete a whole simulation tick do not raise the event. A catch-up call raises it once for
each whole tick that actually runs.

## Rejected alternatives

- Moving `OnBeforeTick` would break its existing contract for authoring state before movement.
- Raising a command-admission event would expose input before movement and before ownership settles.
- Raising the hook after actions, combat or serve would miss the cancellation deadline or defer writes to the
  next snapshot.

## Verification

Headless tests cover same-tick `Attack` and `WalkTo` visibility, whole-tick cadence, ordering before interactions
and combat, same-tick replication of handler writes, and ownership plus ghost state across a region handoff.
