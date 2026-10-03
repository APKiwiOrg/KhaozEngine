# Baked ground navigation profiles and route-free approach

Status: implemented and verified on `feature/grimhollow-nav-bake` through the
[implementation plan](../superpowers/plans/2026-10-03-nav-profile-bake.md), with rulings O2.21 to O2.26 and N1 to N9
recorded. The full Release build and test run passed at the Task 7 finish and the release note is staged for
`20.20.0`. No release or tag is claimed.

Consumer: Grimhollow continuous movement pivot, player walk-up pathing after P5.
Owner decision, binding on 2026-10-03: player walk-up pathing uses an engine offline bake. The engine adds navigation
profile save and load, a bake step a game calls from its content pipeline, and the allocation fix. Clients load the
baked profile at startup in milliseconds. This lifts round 2's non-goal "navigation persistence"
([round 2 design, Out of scope](CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md#out-of-scope)) for this scope only.
Orchestrator ruling O2.21 adds a route-free approach driver to the same design, section 9.

Base: engine main `8dfe93941`, which contains round 2 A to D and the analytic terrain query view
([ANALYTIC-TERRAIN-QUERY-OWNERSHIP-DESIGN-2026-10-03.md](ANALYTIC-TERRAIN-QUERY-OWNERSHIP-DESIGN-2026-10-03.md)).
Newest tag `v20.18.0`. Main stages `20.18.1` untagged.

## Problem and measured facts

Hollowmere is 192 m by 192 m. At 0.25 m cells that is 768 by 768, or 589,824 columns. One noisy run on the dev Mac
under concurrent builds measured:

| Step | Elapsed | Allocated | Retained |
| --- | ---: | ---: | ---: |
| `PhysicsNavBake.Capture` | 1.39 s | 39 MiB | not measured |
| Player `BuildProfile` | 69.7 s | 18.8 GiB | 19.9 MiB |

A client cannot spend a minute at startup. The work is also almost all transient garbage, about 33.4 KiB per column.

Investigation at v20.18.0 attributes the cost to the proof volume and to per-call allocations inside each proof:

- `BuildProfile` runs one hold proof per captured surface, then one proof per neighbour for each of up to eight
  neighbours per accepted node (`PhysicsNavBake.Profiles.cs:46-62` and `:92-106`).
- Each `GroundTraversalProbe.TryEdge` (`GroundTraversalProbe.cs:39-57`) runs up to 64 `GroundMoveContext.Step`
  calls, each a full `CharacterMovement` step. A 0.25 m edge at the 1 m/s probe pace takes about 8 to 11 slices.
- Every `BepuPhysicsWorld.ComputePenetrationCore` allocates a new `OverlapCollector` holding a fresh `List`
  (`BepuPhysicsWorld.Queries.cs:86` and `:149`). A step can issue several penetration queries.
- `footprint.Accepts` passed as a method group allocates a delegate on every proof (`Profiles.cs:80`).
- With a medium provider, every `TryEdge` builds a new dry `GroundMoveContext` (`GroundTraversalProbe.cs:28-30`),
  which allocates the context and up to three cached local delegates, since a dry context has no medium delegate.

Further facts that shape the format:

- `PhysicsNavColumns`, `PhysicsNavSurface`, `NavAreaFootprint` and the `GroundNavigation` constructor are internal.
- `NavSpace`, `NavGrid`, `NavTraversalGraph`, `NavTraversalLayer` and `NavLink` are public immutable data. `NavGrid`
  has only the `FromWalkable` and `FromSurfaces` factories, both of which rederive blocking from samples.
- A built `GroundNavigation` keeps the captured columns. Its `AllowsSegment` and endpoint resolution read them through
  the footprint, so a persisted profile must carry the columns as well as the graph.
- Nothing in Navigation or Movement serializes. Profile building is sequential and not thread-safe
  (`GroundMoveContext._stepping` and `_origin`, the Bepu world's shared `BufferPool`).
- `TileWorldColliders.Hash` already digests the colliders, but its own documentation warns that a walk surface yawed
  off a quarter turn can hash differently on x64 and ARM64.

## Goals

1. A bake set file holds one capture and one or more named profiles. Loading it yields `GroundNavigation`
   instances equivalent to a fresh `BuildProfile` from the same inputs, proven by tests on fixtures.
2. A stale bake is refused with a typed reason. It is never silently used, and there is no analytic fallback.
3. Load needs no physics world, no ground provider and no proof. Its cost is reading, checking and copying arrays.
4. `BuildProfile` stops allocating per proof. Measured gates in section 2.
5. A game can approach a reach target without a planner, with the same reach, stop ring and suspension rules as
   `MoveToRange`, and receive a typed blocked status when progress stalls. Section 9.
6. Game specifics stay in the game. The engine defines no profile names, file paths, area bits or thresholds.

## Non-goals

- Streaming, partial or incremental rebakes, and region-sized bake tiles.
- Persisting point planners, hop bakes or other `NavSpace` producers. Only physics-checked `GroundNavigation`.
- Parallel or multi-threaded baking. The bake is offline, and the allocation fix is the scoped speed work.
- Compression. Format version 1 is uncompressed. A later format version may add it.
- Cross-version bake reuse. A bake is valid only for the engine version that wrote it.
- A generic bake tool or `ke-tileedit` verb. The classifier is game code. Section 7.
- Wall following or local avoidance in the route-free driver. It walks straight and reports a stall.
- Any Grimhollow file change, version bump or tag.

## Decisions

Scores are design judgments from 1 to 10, higher is better.

### D1. What a bake stores

| Option | Equivalence | Load cost | Format size | Coupling | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| A. Columns plus each profile's final grids, traversal layers and links | 10 | 9 | 8 | 8 | 35 |
| B. Columns plus per-surface hold bits and exits, rerun layer extraction at load | 8 | 6 | 9 | 6 | 29 |
| C. Columns only, rebuild profiles at load | 10 | 1 | 10 | 9 | 30 |

Select A. Load copies the decisions a fresh build made and rederives only the clearance bytes, through the same
`ClearanceTransform` a fresh build uses. B ties load to layer extraction, its intermediate arrays and its timing. C is
the current minute-long startup.

### D2. Who owns the file format

| Option | Boundaries | Public surface | Reuse | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Movement owns the whole format. Navigation gains one public `NavGrid` factory | 9 | 9 | 7 | 25 |
| B. Navigation owns a `NavTraversalGraph` codec, Movement wraps it with columns and identity | 8 | 6 | 8 | 22 |
| C. A new persistence package | 6 | 5 | 8 | 19 |

Select A. Only `GroundNavigation` needs persistence today. The one Navigation addition, rebuilding a surface grid from
a stored blocked mask and heights, is useful without the format. B publishes a second versioned format for which
there is no consumer. C adds a package and an edge for one file type.

### D3. Staleness identity

| Option | Detects content edits | Detects engine semantics | Cross-architecture safe | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Caller-labelled source digests plus engine version, options and full probe tuning | 9 | 10 | 9 | 28 |
| B. Engine-computed collider hash only | 7 | 5 | 4 | 16 |
| C. File timestamps | 2 | 1 | 8 | 11 |

Select A. The engine cannot see the game's document, catalog rows or classifier policy, so the game supplies labelled
SHA-256 digests of them. Movement cannot reference TileWorld.Physics, and `TileWorldColliders.Hash` is not promised
equal across x64 and ARM64. A game may still add it as one labelled source when its world has no off-quarter walk
surfaces. The engine version is part of the identity because a patch can change what the movement core accepts.

### D4. Engine version strictness

| Option | Safety | Rebake cost | Simplicity | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Any engine version change refuses | 10 | 6 | 10 | 26 |
| B. An engine-maintained bake algorithm revision constant | 6 | 9 | 7 | 22 |
| C. No engine check | 2 | 10 | 10 | 22 |

Select A. B depends on a person remembering to bump a constant whenever `CharacterMovement`, the Bepu queries, the
column probe or layer extraction changes, which is exactly the failure a refusal exists to catch. A game's engine pin
moves rarely and its content pipeline rebakes in minutes. The identity uses the Movement assembly's informational
version with any `+metadata` suffix removed.

### D5. Probe tuning coverage

| Option | Safety | False refusals | Maintenance | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Every `MoveTuning` field except the three the probe overwrites | 10 | 7 | 9 | 26 |
| B. Only the four geometry fields the runtime checks | 4 | 10 | 9 | 23 |
| C. A hand-picked list of proof-relevant fields | 7 | 9 | 4 | 20 |

Select A. The probe runs `tuning with { WalkSpeed = 1f, RunSpeed = 1f, AirMomentum = false }`, so those three never
change a decision and are excluded. Gravity, grounded skin, climb speed, traction and slide fields all can. A
reflection test fails when `MoveTuning` gains a field the encoder does not cover. Adding that field to the identity
changes the layout, so it also bumps the format version.

### D6. Bake entry point

| Option | Fits game policy | Tool conventions | Dependency cost | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Library API in Movement, called from a game-owned bake command | 10 | 8 | 10 | 28 |
| B. A `ke-tileedit` verb | 3 | 5 | 4 | 12 |
| C. A new `ke-navbake` tool reading a declarative manifest | 5 | 8 | 6 | 19 |

Select A. `ke-tileedit` is an MCP authoring server over the document. It references neither Physics.Bepu nor
Movement, and it cannot run a game's classifier, such as a pen table or a water habitat rule. A declarative tool would
need an area-rule language the engine does not have. `ke-propbake` works because a prop manifest fully describes its
input, which is not true here.

### D7. Load contract

| Option | Fast refusal | Typed outcome | Hostile input | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Stream load: header, identity compare, then payload, returning a result | 10 | 10 | 9 | 29 |
| B. Exceptions carrying a reason | 8 | 7 | 9 | 24 |
| C. Read whole file, then compare | 5 | 10 | 8 | 23 |

Select A. A stale bake is refused after reading the header and identity block, without reading the payload.
Environment errors from the stream, such as `IOException`, still propagate. Content problems never throw.

### D8. Route-free approach shape (O2.21)

| Option | Existing contracts | Clarity | Reuse | Total |
| --- | ---: | ---: | ---: | ---: |
| A. New `DirectMoveToRange` driver sharing extracted range helpers | 10 | 9 | 9 | 28 |
| B. A planner-less `MoveToRange` constructor | 6 | 5 | 9 | 20 |
| C. Leave it game-side | 10 | 6 | 3 | 19 |

Select A. `MoveToRange` statuses describe routes, and its constructors require a planner by contract. A separate
driver keeps that contract intact and still shares the reach, travel bound, closest-point and stop-ring code.

### D9. Stall windows in ticks or seconds

| Option | Matches reference | Determinism | Variable dt | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Counted ticks | 10 | 10 | 6 | 26 |
| B. Accumulated seconds | 8 | 6 | 9 | 23 |

Select A. The driver is called once per simulation tick and the reference rule counts ticks. Integer windows avoid
float accumulation deciding whether 15 slices of 1/30 s reach 0.5 s. A game converts its seconds once.

## 1. Package and dependency edges

No project reference changes. Movement keeps exactly Locomotion, Navigation and Physics. Hashing uses
`System.Security.Cryptography.IncrementalHash` and encoding uses `System.Buffers.Binary.BinaryPrimitives`, both in the
base library. Navigation gains one public factory and no reference. Physics.Bepu changes only internal query scratch.
The existing Movement architecture guard stays green unchanged.

## 2. Allocation fix

Three changes, all behavior-preserving:

1. **Pooled overlap scratch.** `BepuPhysicsWorld` owns one `List<CollidableReference>` scratch list.
   `ComputePenetrationCore` clears it, fills it through an `OverlapCollector` that references it, and leaves its
   capacity for the next call. Query views reach the same core, so they share the source world's scratch. The world is
   already documented single-threaded. Candidate order, the exclusion gate, batching order and the deepest-contact
   rule are unchanged.
2. **Cached footprint predicate.** `NavAreaFootprint` builds its `Func<Vector3, bool>` once in its constructor and
   exposes it as `AcceptsPredicate`. `PhysicsNavBake.Prove` passes that instance.
3. **Cached dry context.** `GroundMoveContext` exposes an internal lazily created `DryContext`. It returns the context
   itself when `Medium` is null. Otherwise it creates one context with the same ground, normal, physics, clamp and
   `MovementQueries` and a null medium, then keeps it. `TryEdge` uses it. The dry context has its own `_stepping` and
   `_origin`, as the per-call context did.

Targets. The first three are deterministic test gates in the AllocSensitive collections using the existing
`AllocAssert` pattern of one retry.

| # | Measured unit | v20.18.0 | Target |
| --- | --- | --- | --- |
| A1 | Warmed `ComputePenetration` with at least one overlapping static, world and query view | one collector list per call | 0 bytes per call |
| A2 | Warmed `TryEdge` on a medium-bearing context with a footprint predicate, flat Bepu fixture | context, delegates and lists per call | 0 bytes per call |
| A3 | `BuildProfile` on a 16 m by 16 m flat fixture at 0.25 m, 4,096 columns | about 33.4 KiB per column, from the Hollowmere run | at most 1 KiB per column, 4 MiB total |
| A4 | Hollowmere player `BuildProfile`, one consumer observation | 18.8 GiB | at most 0.6 GiB, also about 1 KiB per column |

A1 to A3 run in engine CI. A4 is recorded once by the consumer's bake run, never as an engine test. Elapsed time is
recorded but not gated. Removing allocation removes garbage collection work, while the physics query count stays the
same, so no elapsed target is promised. If A1, A2 or A3 still fails after the three changes, the implementer records the
remaining allocation sites from an allocation trace in the plan Outcome and files an issue. The plan does not widen
into `CharacterMovement` without root approval.

## 3. What a bake set holds

One capture's columns, the capture options and origin, the caller's source digests, and one or more named profiles.
Each profile stores its name, area filter, normalized probe tuning, every layer grid's blocked mask, open-cell
heights and metadata, the exits of its accepted nodes and its accepted link subset. Accepted nodes are exactly the
open cells, because `BuildProfile` accepts `IsPassable(x, z, 0f)`, which is clearance above zero. Candidate links are
regenerated at load with `NavLayerLinks.GenerateGrounded(grids, StepHeight)`, which reads only `SurfaceHeightAt`. Agent radius
and height derive from the stored tuning, so they cannot disagree with it.

All profiles in a set share one capture, matching `PhysicsNavBake`'s existing one-capture, many-profiles model. A
loaded set shares one immutable column instance across its profiles, as a fresh build does.

## 4. Binary format, version 1

Little-endian throughout, written and read with `BinaryPrimitives`. Floats are stored as their IEEE 754 bit pattern
and compared as bits. Counts and dimensions are `int32` and must be nonnegative.

### Container

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | Magic `KENB` |
| 4 | 2 | Format version, `uint16`, value 1 |
| 6 | 2 | Flags, `uint16`, must be 0 |
| 8 | 4 | Identity block length `L`, `uint32`, at most 1 MiB |
| 12 | 8 | Payload length `P`, `uint64` |
| 20 | 32 | SHA-256 of the payload bytes |
| 52 | `L` | Identity block |
| 52 + `L` | `P` | Payload |

The file is exactly `52 + L + P` bytes. Trailing bytes are corrupt. The bake fingerprint is the SHA-256 of the
identity block bytes, exposed as `GroundNavigationBake.Fingerprint` for logs and build manifests.

### Identity block

Canonical, so equal inputs produce equal bytes in any insertion order:

1. Engine version: `uint16` byte count, UTF-8.
2. Options in declaration order: `MinX`, `MinZ`, `MaxX`, `MaxZ`, `CellSize`, `ProbeHeight`, `ProbeRange`,
   `MaxSlopeRadians` as float bits, `MaxCells`, `MaxLayerCells`, `MaxSurfacesPerColumn` as `int32`,
   `EdgeProbeSeconds` as float bits, `MaxEdgeProbeSteps` as `int32`.
3. Sources: `uint16` count, then entries sorted by ordinal label. Each is a `uint8` label length, the ASCII label and
   a 32-byte SHA-256 digest.
4. Profiles: `uint16` count, then entries sorted by ordinal name. Each is a `uint8` name length, the ASCII name,
   `Required` and `Excluded` as `uint32`, and the 27 retained `MoveTuning` fields in declaration order, floats as bits
   and the bool as one byte of 0 or 1. `WalkSpeed`, `RunSpeed` and `AirMomentum` are omitted.

Labels and names are 1 to 64 characters from `a` to `z`, `0` to `9`, `.`, `_`, `-` and `/`. A set needs at least one
source and between 1 and 256 profiles, with unique labels and unique names. Bake refuses
`MaxSurfacesPerColumn` above 255, since per-cell counts are stored as bytes.

### Payload

Capture section:

- Width and height, which must equal the dimensions the options produce.
- Capture origin as three float bits. This is provenance only and is never compared, because a client may run with a
  different floating origin.
- Surface count `S`.
- One `uint8` surface count per cell, row-major in canonical Z then X order. Their sum equals `S`.
- `S` surfaces, each height and headroom as float bits and areas as `uint32`. Headroom may be positive infinity.

Then one section per profile, in identity order:

- Layer count `L`, with `L * width * height` at most `MaxLayerCells`.
- Per layer: width and height, which equal the capture's. `CellSize`, `OriginX`, `OriginZ`, `YawRadians`, `YMin` and
  `YMax` as float bits. A blocked bitset of `ceil(n / 8)` bytes, bit `i` for cell `i`, least significant bit first,
  unused high bits zero. Open-cell heights as float bits in row-major order, one per clear bit. One exit byte per
  open cell in row-major order.
- Candidate count `K` as `int32`, the length of the candidate list regenerated from the grids at bake time. The
  reader bounds `ceil(K / 8)` by the bytes remaining before it regenerates the list, then requires the regenerated
  count to equal `K`, which catches a same-version change in link generation order or count.
- Accepted links: a bitset over the regenerated candidate list in its order, `ceil(K / 8)` bytes for `K` candidates,
  unused high bits zero.

Blocked is `ClearanceAt == 0`, which holds for every grid because `ClearanceTransform` writes zero exactly for blocked
cells. Heights of blocked cells are not stored, because `SurfaceHeightAt` returns null for them and no other member
reads them, including link generation. A loaded grid holds zero there. At bake time the writer checks that the fresh
`Space.Links` equal the regenerated list and throws `InvalidOperationException` otherwise, since a mismatch is an
engine defect, not caller input.

### Reader validation

`Load` encodes the caller's expectation first, so an invalid expectation throws `ArgumentException` to the caller as
`Create` does. Decoding the stored identity never throws. Bytes that are truncated, carry trailing data, or are not
canonical return `Corrupt`. Not canonical covers unsorted or duplicate labels and names, characters outside the label
set, a digest of the wrong length, and an overlapping area filter, which is checked on the raw `uint32` values before
any `NavAreaFilter` is constructed.

Before allocating the payload, the reader bounds `P`. With `C = width * height`, `M = MaxSurfacesPerColumn`, profile
count `N` and `Lmax = floor(MaxLayerCells / C)`, in unsigned 64-bit arithmetic that saturates on overflow:

```text
capture  = 24 + C + 12 * C * M
layer    = 32 + ceil(C / 8) + 5 * C
links    = Lmax * (Lmax - 1) * C            ceil(Kmax / 8) for Kmax = 8 * Lmax * (Lmax - 1) * C
profile  = 4 + Lmax * layer + 4 + links       the second 4 is the stored candidate count
Pmax     = capture + N * profile
```

`Kmax` counts two directed links for each of eight neighbours of every cell, for each layer pair. `P` above
`min(Pmax, Array.MaxLength)` is `Corrupt`. When the stream can seek, `P` must equal `Length - Position`, checked before
the buffer exists. Likewise an identity length `L` above `Length - Position` is `Corrupt` before its buffer exists. The payload buffer is a plain array, since a process loads a bake once.

Every count is then checked against the bytes remaining before anything sized by it is allocated. The decoder
enforces the invariants a fresh build guarantees, mirroring `PhysicsNavBake.Capture`:

- Each per-cell count is at most `MaxSurfacesPerColumn`, and the counts sum to `S`.
- Column heights are finite and strictly ascending. Headroom is not NaN and is at least zero.
- Layer open heights are finite. `YMin` and `YMax` are not NaN.
- Layer dimensions, origin and cell size match the options, and yaw is zero.
- Unused bitset bits are zero. Exits target open cells within the layer.

Only payload decoding sits inside a catch of `ArgumentException`, so the `NavSpace`, `NavTraversalGraph` and
`NavTraversalLayer` constructors' own validation of endpoints, exits and accepted links also returns `Corrupt` with a
raw developer detail.

The payload checksum detects accidental damage. It is not tamper protection. A resealed edit inside valid ranges, such
as accepting a rejected link, loads. The bake is a build artifact with the same trust as the client binary.

## 5. Public API

```csharp
namespace KhaozEngine.Movement;

public sealed class NavBakeSources
{
    public NavBakeSources Add(string label, ReadOnlySpan<byte> sha256);
    public NavBakeSources AddHashOf(string label, ReadOnlySpan<byte> content);
    public NavBakeSources AddHashOf(string label, Stream content);
    public IReadOnlyList<string> Labels { get; }
}

public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas);

public sealed record NavBakeExpectation(PhysicsNavBakeOptions Options, NavBakeSources Sources,
    IReadOnlyList<NavBakeProfile> Profiles);

public enum NavBakeLoadStatus
{
    Loaded, NotABake, UnsupportedFormat, Corrupt,
    EngineChanged, OptionsChanged, SourcesChanged, ProfilesChanged,
}

public sealed record NavBakeLoadResult(NavBakeLoadStatus Status, string Detail, GroundNavigationBake? Bake);

public sealed class GroundNavigationBake
{
    public static GroundNavigationBake Create(PhysicsNavBake capture, NavBakeSources sources,
        IReadOnlyList<NavBakeProfile> profiles);
    public static NavBakeLoadResult Load(Stream source, NavBakeExpectation expected);
    public void WriteTo(Stream destination);
    public ReadOnlySpan<byte> Fingerprint { get; }
    public IReadOnlyList<string> ProfileNames { get; }
    public GroundNavigation GetProfile(string name);
}
```

`NavBakeSources` is a mutable builder. `Create` and `Load` snapshot it. `Add` takes an exact 32-byte digest and
`AddHashOf` digests content with SHA-256. `Labels` is ordinal-sorted.

`Create` builds every profile through `capture.BuildProfile` while the capture is live, so its profiles are exactly
the fresh build. `WriteTo` writes the canonical bytes, and two writes of one set are byte-identical.

`Load` reads the container header and identity block, compares them with the identity encoded from `expected` and,
only when they match, reads and checks the payload. Checks run in container, identity and payload order, and the first
failing check decides the status. The identity comparison reports the first difference in the order engine, options,
sources, profiles. A content mismatch
names the first differing item in `Detail`: the engine versions, the option field, the source label that is missing,
extra or changed, or the profile name and its first differing field. `Loaded` carries the bake and an empty detail.
Every other status carries a null bake. `Detail` is developer text and is never shown to players.

`GetProfile` throws `KeyNotFoundException` for an unknown name. A loaded profile is a `GroundNavigation` like any
other. `MoveToRange`, `AllowsSegment`, `Planner` and runtime tuning validation behave the same for it.

`Navigation` gains:

```csharp
public static NavGrid FromBlockedSurfaces(int width, int height, float cellSize, float originX, float originZ,
    ReadOnlySpan<bool> blocked, ReadOnlySpan<float> heights,
    float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity, float yawRadians = 0f);
```

It validates the same arguments as `FromSurfaces`, requires both spans to hold `width * height` entries and finite
heights on open cells, copies both spans, and computes clearance with `ClearanceTransform.Compute`. Heights on
blocked cells are copied and never observable.

## 6. Equivalence contract

For one capture, sources and profile list, a profile from `Create` and the same profile after `WriteTo` and `Load`
must agree on:

- `Space`: layer count, and per layer `Width`, `Height`, `CellSize`, `OriginX`, `OriginZ`, `YawRadians`, `YMin`,
  `YMax`, `HasSurfaceHeights`, every `ClearanceAt` and every `SurfaceHeightAt` as bits. `Links` in order.
- Graph: `AgentRadius`, `AgentHeight`, every `IsNodePassable` and `ExitMask`, accepted `Links` in order.
- Columns behind the footprint: every column's surfaces as bits.
- Behavior: `AllowsSegment` and both `Planner.FindPath` overloads over a deterministic query set, with equal status
  and waypoints as bits.
- Bytes: writing a loaded bake reproduces the file byte for byte.

Fixtures cover a thin wall, a one metre door with small and wide capsules, a step, a deck over water with area tags,
a rebased world, a two-level Stair seam with and without a fence so accepted and rejected Stair links both round
trip, and the real TileWorld bridge.

## 7. Bake entry point and outputs

A game calls the library from its own content pipeline command:

```csharp
using var capture = PhysicsNavBake.Capture(context, options, classify);
GroundNavigationBake bake = GroundNavigationBake.Create(capture, sources, profiles);
using FileStream file = File.Create(outputPath);
bake.WriteTo(file);
```

The engine names no paths. Consumer guidance: write the file as a generated artifact beside the world it was baked
from, with an extension such as `.kenav`. Run the bake whenever a source digest input, the bake options, a probed
tuning or the engine pin changes. Keep a test in the game that loads the shipped bake with the shipped inputs and
expects `Loaded`, so CI catches a stale artifact before players do. The bake should run the same physics composition
as runtime movement, including the movement query view, as `PhysicsNavBake` already requires.

Source digests are the game's inputs, labelled for diagnosis. Recommended for a tile world: the world document files
in a canonical order, collider options, the catalog rows that drive colliders, heights and medium, and a value naming
the classifier policy and its tables. Use authored inputs rather than `TileWorldColliders.Hash` when the world holds
walk surfaces yawed off a quarter turn, because that hash is not promised equal across x64 and ARM64.

The digests must cover every input a proof reads, not only the colliders: the ground height and normal providers,
the clamp bounds, the medium and the classifier. Normalise line endings to LF before digesting text, so a Windows
checkout with CRLF files digests equal to the machine that baked.

The payload carries the baking machine's float arithmetic. A client on another architecture uses those decisions even
where its own build would differ in the last bits. Those differences sit inside the bounded proof tolerance.
`AllowsSegment` accepts endpoints within the profile's step class, and the live movement core still resolves every
actual step, so a ULP difference cannot admit motion the core refuses.

## 8. Startup validation, refusal and expectations

At startup the game builds its expectation from the same options, tuning and digests it would bake with, calls `Load`
and branches on the status. Anything other than `Loaded` means the bake does not describe this build. The engine
never substitutes a fresh build or an analytic route. What the game does next, such as disabling routed walk-up and
reporting the reason in developer diagnostics, is game policy.

Expectations for the Hollowmere player profile, estimated from the 19.9 MiB retained fresh build and the format above:

| Quantity | Estimate |
| --- | --- |
| File size | about 10 to 14 MiB, dominated by about 7 MiB of surfaces and one dense layer's open heights |
| Retained after load | equal to a fresh build, about 20 MiB |
| Transient load allocation | the payload buffer, the final arrays, per-profile scratch of 7 B per cell shared across profiles, and per-layer clearance scratch of 5 B per cell, about 2 to 2.3 times retained. The engine fixture measured 1,958,264 B allocated against about 0.85 to 0.96 MB retained |
| Load time on the dev Mac | under 150 ms, mainly reading, the payload SHA-256 and one clearance transform per layer |
| Stale refusal time | under 1 ms, header and identity block only |

These are estimates. The plan measures one load on an engine fixture, and the consumer records one Hollowmere load.

## 9. Route-free approach driver (O2.21)

At v20.18.0 both `MoveToRange` constructors require a planner (`MoveToRange.cs:16-28`) and every tick asks the path
follower first (`:51-55`). `PlayerPathMovement.Command` (`PlayerPathMovement.cs:13-35`) needs no route. Grimhollow
P5 ships an interim game-side `DirectWalkUp` until this lands. The engine driver replaces it.

```csharp
public sealed record DirectApproachOptions
{
    public DirectApproachOptions(int stallWindowTicks, float stallTravelMetres,
        int approachWindowTicks, float approachGainMetres);
    public int StallWindowTicks { get; }
    public float StallTravelMetres { get; }
    public int ApproachWindowTicks { get; }
    public float ApproachGainMetres { get; }
}

public sealed class DirectMoveToRange
{
    public DirectMoveToRange(DirectApproachOptions options);
    public RangeSteering Tick(in MoveState body, in MoveTuning tuning, in ReachTarget target, float range,
        bool run, bool targetMoves, float dt, GroundMoveContext context);
    public void Reset();
}

// RangeMoveStatus gains Blocked, appended after Suspended.
```

Window sizes are positive tick counts and distances are finite and positive. There are no defaults.

A window of `N` ticks spans `N` intervals between `N + 1` counted samples. It first becomes eligible on the
`(N + 1)`th counted tick, comparing that tick's sample with the sample `N` counted ticks earlier. Grimhollow R13 at its
30 Hz simulation is a stall window of 15 intervals with 0.1 m and an approach window of 45 intervals with 0.1 m. Those
values appear in tests, not in the engine. The ring buffer holds `max(StallWindowTicks, ApproachWindowTicks) + 1`
samples, allocated in the constructor, so steady ticks allocate nothing.

Tick order, matching `MoveToRange` where the rules overlap:

1. Validate context, tuning, body and `dt` as `MoveToRange` does. Evaluate current exact reach with zero tolerance.
2. An airborne or committed body returns `Suspended`. The tick counts toward neither window and clears neither.
3. A body in range returns `InRange` and clears both windows and any latched block.
4. A latched block returns `Blocked`.
5. A zero travel bound, such as a rooted body with `MoveState.SpeedScale` 0, returns `Following` with zero input and
   counts toward neither window, as `MoveToRange.cs:58` holds. A rooted body is not blocked (ruling O2.25).
6. Otherwise the driver requests travel toward the closest horizontal point of the target shape, the existing
   `MoveToRange` helper, capped by the same travel bound. It preflights the step on a copy through the live context,
   accepting it only when the result is grounded, not swimming and finite. It then shrinks the command with the
   same stop-ring bisection. A refused preflight requests zero for this tick.
7. The tick records a sample of current feet XZ and reach distance, because it asked for movement even when the
   preflight refused. When the stall window is eligible and the net horizontal displacement from its oldest sample to
   now is below `StallTravelMetres`, the driver latches and returns `Blocked`. When `targetMoves` is false, the
   approach window is eligible and the reach distance has fallen by less than `ApproachGainMetres` across it, the
   driver latches and returns `Blocked`.
8. Otherwise it returns `Following` with the command.

A change of target kind, shape, yaw, range or capsule geometry resets internally, as in `MoveToRange`. A change of
`targetMoves` clears the approach window. Caller `Reset` clears everything. `PlayerPathMovement.Command` and
`NpcGroundMovement.Step` already treat every non-`Following` status as idle, so `Blocked` needs no adapter change.

Departures from the reference, recorded so the game can delete its class knowingly:

- Direction uses the closest horizontal point of a box rather than its centre. For capsules and points they are the
  same. A box approach reaches its near face sooner.
- The final fraction comes from live bisection against exact reach, not from a 0.05 minimum fraction, so a short
  last step cannot stall or overshoot.
- A step that would leave the ground or start swimming is refused and counts toward the stall window, so a walk off a
  ledge or into deep water ends `Blocked` instead of falling or swimming.
- A zero travel bound counts toward neither window, so a rooted body holds without ending its walk.
- `InRange` clears both windows, so a followed body that moves away starts fresh windows.
- Stall travel is net displacement across the window, not accumulated path length, so pacing in place is blocked.
- `Blocked` stays latched until `InRange` or `Reset`. The game ends the walk on the first `Blocked`.
- The driver holds no target identity. Replacing the target with another one of the same shape needs `Reset`.
- The caller passes `targetMoves` true for any body target, such as a creature or player, and false for static
  objects and points.
- Call `Tick` exactly once per simulation tick, since the windows count ticks.

## 10. Test strategy

| Proof | Scenarios | Home |
| --- | --- | --- |
| Allocation | A1 world and view, A2 medium and dry contexts, A3 per-column budget, unchanged penetration results | Game.Tests Physics, Movement.Tests |
| Grid factory | Equal clearance and heights from a surface grid, span validation, input copies | Game.Tests Navigation |
| Identity | Canonical ordering, every option and retained tuning field, the three excluded fields, labels and digests, engine version | Movement.Tests |
| Round trip | Section 6 on every fixture including the open and fenced Stair seam, byte determinism of two creates and of rewrite | Movement.Tests, TileWorld.Physics.Tests |
| Refusal | Each status, first differing item in `Detail`, non-canonical identity, truncation, trailing bytes, flipped payload byte, hostile counts, seekable length mismatch, value invariants, unused bitset bits, invalid expectation throws, stale refusal without reading the payload | Movement.Tests |
| Direct driver | Window rules on scripted states over flat ground (stall, approach, suspension, rooted body, in range, `targetMoves`, latch and reset), one physics wall fact, one ledge fact, open approach, stop ring, invalid options, Blocked command is idle | Movement.Tests |
| Real command path | Direct approach through prediction, codec and authority into range, Blocked sends idle | Server.Tests |

No test needs Grimhollow assets. Focused runs per task and one full Release verification at the finish. No local
repetition or stress runs.

## 11. Version and release

Additive public API and a new enum member are a minor change under the engine's SemVer rule. Engine main stages the
patch `20.18.1` from another session. Ruling O2.26: because this work adds public API, the staged version becomes
`20.19.0` at integration, with the `20.18.1` entry folded into it. The delta reliability work rides the same `20.19.0`.
Root performs the selection after rereading main, the version and tags. Only the owner starts a tag. Grimhollow adopts
a released pin. Ruling N9 amends O2.26: main released `v20.19.0` before integration, so this work stages `20.20.0`, or
rides a minor already staged above `20.19.0`, re-read at landing.

## Rulings for Grimhollow

These were owner questions in the first draft and are now recorded rulings. The engine design does not depend on them.

1. O2.22: a Grimhollow script generates the baked file and it is committed beside the world document, gated by a CI
   test that loads it with the shipped inputs.
2. O2.23: the CI load test makes refusal unreachable in a release. At runtime a refused client keeps running, logs the
   status and detail, and uses `DirectMoveToRange` for walk-up.
3. O2.24: P5's server-side creature windows keep their startup bake during P5. The offline bake is revisited for them
   after adoption.

## Out of scope

- Grimhollow files, engine tags and consumer pin moves.
- `ke-tileedit` changes and any new dotnet tool.
- Parallel baking, compression, streaming and partial rebakes.
- `CharacterMovement` allocation work beyond recording it. Ruling N2 and its extension later admitted two
  per-thread capsule caches, one per step and one per slide probe, with public signatures unchanged.
- Issues outside this scope found during execution, which become ledger issues.
