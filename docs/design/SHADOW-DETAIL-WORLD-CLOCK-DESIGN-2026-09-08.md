# Shadow detail profiles and shared world clock

Date: 2026-09-08.

Status: Implemented in 18.35.0.

Grimhollow is waiting on two capabilities that already exist in pieces across the engine and Ruinborne:

- full directional shadow maps can be configured at arbitrary resolutions, but the engine offers no named
  detail profiles
- `SunCycle` owns the lighting curve, while Ruinborne still owns a bespoke authoritative clock, wire codec,
  client mirror, command queue, and persisted anchor

Grimhollow is the second game that needs the clock stack. Copying it would create two protocol and persistence
implementations for the same rule. The engine-first decision is to add both shared capabilities to KhaozEngine,
release them, then let Grimhollow adopt that release.

## Decisions

### Shadow map detail

Add `ShadowMapDetail` to `KhaozEngine.Render3D` with three values:

| Detail | Per-cascade resolution | Technique |
|---|---:|---|
| `Low` | 1024 | `ShadowMode.ShadowMap` |
| `Default` | 2048 | `ShadowMode.ShadowMap` |
| `High` | 3072 | `ShadowMode.ShadowMap` |

Add a factory on `ShadowSettings` that returns a new, uncommitted settings object for one detail. Every
profile sets `Mode` to `ShadowMap` and changes only `ShadowMapResolution`. Cascade count, reach, blend, bias,
and strength keep the engine defaults unless the consumer changes them before scene construction.

The existing `ShadowMode.Off` and `ShadowMode.Blob` remain available. They are separate technique choices for
games that need them. The new detail enum never maps to either one. Unknown detail values normalize to
`Default` rather than disabling shadows or requesting an unbounded atlas.

The profile is a construction-time choice because the atlas is allocated and bound while `Scene3D` is built.
Consumers that change detail in a settings screen must persist the choice and explain that a restart is needed.
The engine will not rebuild a live scene for a quality change.

### World clock kernel

Add the clock stack to `KhaozEngine.Simulation`, which is the dependency-free home of authoritative simulation
timing. Rendering remains outside it. A 2D, 3D, or headless consumer can use the same clock.

The public surface is:

- `WorldClock`, a normalized time-of-day accumulator with a positive day length and non-negative time scale
- `WorldClockState`, an immutable snapshot carrying time of day, day length in seconds, and time scale
- `WorldClockCommand` and `WorldClockCommandKind` for setting time, time scale, or day length
- `WorldClockCodec`, the strict little-endian state and command codec
- `WorldClockHost`, the authoritative tick-side queue, periodic broadcast, immediate command broadcast,
  late-join push, and lock-free published snapshot
- `WorldClockMirror`, the client-side state receiver and between-message accumulator
- `WorldClockAnchor`, the versioned wall-clock anchor used to continue time across a restart

`WorldClock` wraps finite time values into `[0,1)`. It rejects non-finite values. Day length is mutable so an
authenticated operator can change it live. It must remain positive. Time scale accepts zero to freeze time and
otherwise remains non-negative.

`WorldClockHostOptions` owns operational bounds and cadence. Defaults match the Ruinborne behavior that has
already been exercised in production:

- state broadcast every 5 real seconds
- maximum time scale 1000
- minimum day length 60 real seconds

The host receives transport-free callbacks for broadcast and send-to-one. It owns no game-message kind and no
reliability enum. The game wraps the bytes in its own protocol and chooses reliable ordered delivery. This keeps
`Simulation` independent of `Netcode` while retaining one implementation of the hard threading rules.

Authorization stays with the game. A client-originated command reaches `WorldClockHost.EnqueueCommand` only
after the game has proved that seat may control time. An admin endpoint may enqueue through its own authenticated
path. The host validates and clamps the command again before it mutates the clock.

### Codec

The state payload is 12 bytes, little-endian:

```text
[timeOfDay:f32][dayLengthSeconds:f32][timeScale:f32]
```

The command payload is 5 bytes:

```text
[command:u8][value:f32]
```

Decoders require the exact payload length and finite values. A state additionally requires a positive day
length and a non-negative time scale. A command additionally requires a known command byte. Invalid payloads
return false and do not mutate prior state.

### Host and mirror flow

The authoritative host is tick-thread owned. Network and admin threads only enqueue commands or late-join seat
ids. Each tick:

1. advance the clock by the real tick delta
2. drain commands and apply validated values
3. broadcast after a command or when the five-second interval expires
4. send a current snapshot to queued late joiners
5. publish one immutable state object for cross-thread admin reads

The mirror is absent until its first valid state payload. Before that, the game keeps its authored noon lighting.
After a state arrives, the mirror reconstructs the clock from all three fields and advances locally each frame.
Later state messages snap it back to authority.

### Persisted anchor

`WorldClockAnchor` stores a UTC Unix-seconds instant and the normalized time of day at that instant in a
versioned 20-byte record. It performs elapsed-time arithmetic in `double`, clamps a backward wall-clock jump to
zero elapsed time, wraps before narrowing to `float`, and returns null for corrupt or unknown records.

The engine owns the record and math. The game owns the small `IWorldStore` adapter because `Simulation` must not
depend on `WorldStore`, and `WorldStore` must remain a general keyed blob store rather than know one simulation
type. A consumer periodically saves the anchor and saves once more during its shutdown drain.

The anchor continues time across downtime at the boot day length and a scale of 1. Time scale and live day-length
changes are operational controls and reset to server configuration after restart. This matches Ruinborne's
existing time-scale behavior and avoids a forgotten accelerated test clock surviving a deployment.

## Alternatives considered

### Keep both features in Grimhollow

This is the smallest immediate diff, but it contradicts the explicit engine request and would copy Ruinborne's
clock into a second game. Every later correction would need to land twice. Rejected.

### Put the world clock in Render3D beside `SunCycle`

This makes the common client call convenient, but a headless server is the authority and must not depend on a
renderer. It would also make the clock unavailable to a 2D or server-only game without pulling the wrong layer.
Rejected.

### Rebuild the scene when shadow detail changes

This would make the resolution selector live, but it tears down every uploaded world mesh and render resource for
one settings click. The restart note is honest, cheaper, and consistent with the atlas construction contract.
Rejected.

### Add `WorldClock` persistence directly to `KhaozEngine.WorldStore`

That would couple the general storage seam to one simulation model or add a dependency edge from storage back to
simulation. Keeping the reusable anchor in `Simulation` and the few storage calls in the consumer preserves the
package graph. Rejected.

## Verification

The engine change ships with headless tests for:

- all three shadow profiles, their exact resolutions, `ShadowMap` mode, fresh instances, and invalid-value fallback
- clock wrapping, freeze, scaling, live day-length changes, and non-finite refusal
- exact state and command codec shapes plus malformed payload rejection
- host tick ordering, periodic and immediate broadcasts, late-join pushes, bounds, and snapshot publication
- mirror initialization, local advance, authoritative correction, and malformed-state retention
- anchor round trip, corruption refusal, backward wall-clock handling, long downtime precision, and wrap

`KhaozEngine.Render.Tests`, `KhaozEngine.Simulation.Tests`, the full solution, documentation guards, prose and
dash checks, and file-size checks must pass before the version is packed. Grimhollow is pinned and waiting, so
the engine release is tagged after merge under the repository's sanctioned exception.
