# Contact navigation Drop links

Contact profiles independently opt into one-way unpowered descents with `MaxDropMetres`. Zero is the default.
The permission authorizes baked route topology, not manual falling or fall damage. Legacy, aquatic and public
explicit profiles cannot enable it. Numeric allowances must be finite and nonnegative, with signed zero normalized.

## Candidate and physical certificate

Sharp Drops use the following candidate and physical certificate. After node admission, ordinary directed exits
and Stair proofs, sources scan in layer, Z, X and fixed neighbor
order. A direction with an accepted ordinary exit is not a brink. For cell size c and capsule radius r the local
horizon is `1 + ceil(2*r/c)` cells. The nearest column with an admitted lower anchor supplies its highest eligible
surface, with layer-index ties. A supported step-band continuation stops the scan. Require
`StepHeight < sourceRestingY - targetRestingY <= MaxDropMetres` using double subtraction of stored floats.
There is no same-column fall, reverse edge, wide-gap search or creature permission inherited from another profile.
Stair candidates retain their order and Drop candidates follow in canonical first-occurrence order.

Both usable walk and run modes must prove the complete fall through real Contact movement at actual pace and
momentum. A separate full-tuning binding starts each attempt at canonical rest. Initial and post-landing zero-input
Holds count inside the existing `EdgeProbeSeconds * MaxEdgeProbeSteps` duration. Every outcome must be Advanced.
The proof witnesses loss of support, the first air landing, walkable target support and the shared 3D 1 mm arrival
sphere. A same-tick fall and landing uses the controller's selected-route evidence. The monotonic entered-swimming
flag refuses even a transient swim between dry tick endpoints. Supported wading remains eligible.

The complete projected capsule corridor must have known captured columns with eligible first downward surfaces.
A forbidden surface cannot be skipped to find an allowed surface underneath. Air poses can stand above the normal
supported footprint band only inside this transition certificate. The certificate composes with the original
ordinary play-area boundary for all proposed shell routes and resolved air substeps. Highest supported launch
height counts against the whole descent. No clipping, jump, velocity snap or endpoint interpolation proves an edge.
Unavailable environment evidence refuses the whole bake. A known obstruction refuses only the candidate.
Unavailable pacing and step evidence use `ContactDropProofException` with a stable reason and their original
availability or step outcome. It remains an `InvalidOperationException` for existing catches.

Workers own their fall state, full-tuning bindings and source sessions. Candidate ordinals precede parallel work,
verdicts merge by ordinal and the lowest serial failure wins. Final snapshot authentication precedes publication.
No trajectory, destination verdict or prefix is borrowed from another body, profile or attempt.

## Continuous terrain decision

Continuous terrain transitions have explicit capture/profile opt-ins and a shared physical certificate.
KENB 4 retains their frozen proof dependencies and candidate workloads. The default sharp-ledge candidate horizon, corridor and
first-air-landing rules above remain in force. Retaining steep geometry or scanning farther alone cannot certify
a terrain descent.

The approved opt-in semantics permit a player body to slide on authenticated steep faces in a captured
continuous descent chain. Its first walkable support may lie in a bounded downstream fringe of the named
target's own physical landing patch, before ordinary input reaches that admitted anchor. An intermediate shelf,
another raw layer at equal height or unrelated ground cannot supply this first support. Selected steep contacts
and the first walkable support, including transient support inside a tick, need authenticated route evidence.

Locomotion provides an optional bounded selected-route witness for this prerequisite. The certified tile-ground
installation supplies canonical analytic feature keys from the same captured drawn triangles as its height field,
with shared-edge source adjacency. A key is not inferred from height or a process-local physics handle. Missing
analytic identity is explicit unavailable evidence. The witness retains first walkable support even when Slide
or Air leaves it again before the tick ends, and accumulates selected medium history. Existing coarse flags and
movement results remain unchanged.

An internal physical capture and pure terrain envelope retain these source keys alongside actual installed
geometry, including nonstandable faces, roofs and objects. Complete coverage, known emptiness, unavailable data
and out-of-bounds space are distinct. Original admitted layers bind to captured raw ordinals and height bits.
The geometry-derived band fringes and directional indexes find wider terrain descents without changing the sharp
recipe. The height band certifies ground inside the exact doubled-radius capsule projection, including its
circular caps. Height limits stay fixed. Coverage, source identity, overlap, exact ground area and every
obstruction gate keep their whole-cell scope. Exact coverage and occupancy checks provide a corridor prerequisite.
They do not replace the actual motion certificate. Publication also requires the explicit profile permission and
authenticated KENB 4 workload. See the
[internal evidence contract](../../KhaozEngine.Movement/README.md#internal-terrain-descent-evidence).

The internal terrain proof uses one `ContactTerrainDropPolicy` with LaunchPrefix, Descending, LandingFringe and
TerminalHold phases. Full-tuning walk and run each start from their own real source Hold. Every selected steep
feature must belong to the authenticated downward chain. First walkable support must belong to the named target's
own connected patch inside the finite downstream fringe. The policy retains exclusive transition ownership there,
and rejects another patch, lost support or exhausted duration. It never adds ordinary nodes or segment permission.
Terrain following emits the terminal zero-input command while retaining its Drop lock. It acknowledges only after
the next supplied body authenticates that issued Hold, so the wrapper cannot replace it with a later Walk.
Terminal support must resolve through the original target layer's raw cell patch, satisfy the full ordinary
footprint and the unchanged 1 mm sphere, then survive a real zero-input Hold. The original boundary, highest-launch
descent accounting, medium history and 64 slices at 30 Hz remain mandatory. Geometry without a captured terrain
band does not gain a terrain proof. Sharp topology always attempts the existing sharp proof first.

Terrain opt-in is additive. Every sharp-topology candidate attempts the unchanged sharp proof first. A refusal
may attempt terrain when applicable. New terrain topology attempts terrain only. Every accepted link records its
proving route, which is the sole policy selector. Creatures retain zero permission by default.

Bake, preview and following share the terrain policy. Runtime selects only by the stored route, before reading
evidence. Sharp-route links retain the existing sharp preview and follower. Every terrain attempt owns its
corridor, selected-support witness and phase state. Cached certificates contain immutable topology and their
first construction is synchronized across callers. Loading a file needs no physics and grants no new permission.
The consumer must opt in and rebake, then supply its ordinary live movement context and actual body to execute.
Finite water-coverage uncertainty above the existing 1 mm budget refuses a Drop as `PrecisionExceeded`.
The Contact step reports `PrecisionRefused`, so sharp and terrain following return `Blocked`, including during
alignment. Actual unresolved, invalid or mismatched environment evidence retains its prior error/suspension path.
No precision threshold or physical gate is widened.

Call `GroundNavigation.PrepareTerrainDescents()` during loading, outside a frame or simulation tick. It builds the
synchronized certificate cache without physics and is safe for concurrent callers. First use can exceed a frame
or tick budget. Preparation caches immutable topology only and never replaces the live preview or actual Hold.

## Planning and following

Ordinary exits and accepted links remain separate. Public `NavTraversalGraph.CanTraverse` is their union.
Neighbor expansion reads only exit masks, while `GroundNavigation.AllowsSegment` admits supported exits and Stair
links. A Drop cannot become ordinary walking because it joins adjacent cells.

A Drop costs its world XZ octile displacement plus its positive physical descent in metres. Hop retains its cost
knob and Stair its source-cell cost. Incoming edge kinds travel with search parents, so reconstruction annotates
the edge actually chosen. A cheaper ordinary exit remains Walk. Drop source and landing are mandatory physical
centers, including first, final and consecutive transitions. Smoothing and straightening cannot remove either.
An unguarded exact point goal gets a separate final Walk waypoint when it differs from a Drop landing.

Region priority falls back to zero when any link invalidates its spatial lower bound. Point queries on Drop
topology also use a whole-query zero priority when cross-layer or cheap links invalidate their octile bound.
Progress remains separate, so this does not turn a useful partial approach into an arbitrary endpoint.

`PathFollower` exposes `PathFollowState.Dropping`, zero `WorldDir` and a nullable `DropTraversal`. Its
`NavDropTraversal(Link, StartFeet, LandingFeet)` retains cell, layer and physical anchor identity. A physical
`NavSpace` and source waypoint are required. A Drop landing is never consumed by proximity, layer lookup, passed
collinear logic, goal membership or goal drift. While locked the follower does not replan or change its landing.
Before the lock nothing is committed. Region membership on the ledge is genuine arrival and retires the route
without launching, while point proximity cannot witness the lower goal and stays refused. A replan due when the
next transition is an unlocked Drop runs at once, ahead of cooldown, because a deferred walk is reversible and a
launch is not. A Drop next on that refreshed route locks in the same tick under the ordinary cooldown.
The host calls `CompleteDrop()` only after its controller witnesses supported arrival. It advances one transition
and throws when no Drop is active. `Reset()` is explicit cancellation. Hop's geometric lunge contract is unchanged.
The lock takes precedence over terminal refresh and point-intent checks. A supported acknowledgement of a final
Complete Drop preserves its reached-terminal witness after the marker is consumed. Once unlocked, a stale region
or point route can refresh immediately even before cooldown expires and below the retarget tolerance. A fresh
terminal replacement still keeps the ordinary cooldown, and a point endpoint completes only its current intent
or current geometric arrival. No airborne query or endpoint coordinate equality is needed.
`MoveToRange` advances the locked follower's cooldown exactly once per active tick, including suspended flight and
the supported completion tick. Route selection after acknowledgement spends no additional elapsed time, so an
expired cooldown lets the current goal replace the old route before its next transition in that landing tick.

## Range execution and hosts

`MoveToRange` owns a ContactDropFollower dispatched by the stored proving route. Sharp execution keeps its
Align, Launch, Falling and Landed phases. Terrain execution uses LaunchPrefix, Descending, LandingFringe and
TerminalHold from the shared policy. Alignment follows admitted
support to the named source using the shared physical arrival rule. The follower's broad accept radius never
authorizes departure. Commitments, buffered jumps and unrelated airborne bodies remain Suspended.

Supported preparation is reversible, including Align, rooted holds and launch-prefix steps that retain support.
Before issuing departure, the host reconsiders actual current range membership and refreshes due goal drift through
the follower's single query site, even inside cooldown. Membership or a replacement without that Drop releases its
untraversed preparation without calling `CompleteDrop()`. An unchanged Drop retains its prepared state and preview.
A refreshed Drop keeps the ordinary cooldown and locks normally. Refresh requires ordinary raw admission at the
actual supported pose. A prefix outside that admission can return Unreachable and cancel preparation, without
borrowing the old source node as a substitute query origin. Each tick queries at most once. The first command
whose proved step enters certified descent, including steep sliding or a same-tick fall and landing, commits the
transition through the supported lower fringe until terminal Hold. Unsupported
ticks never replan or retarget that flight, even when the latest goal contains the airborne body.

Before launch, one bounded preview checks the actual body, walk/run mode, SpeedScale, momentum, timestep and live
environment. It uses the same bounded control law and original-boundary composition as the bake. The preview slice
count is the floor of the baked duration divided by dt, including both Holds. It neither searches for another edge
nor publishes a predicted arrival. The predicted continuation is checked against actual motion, control inputs and
read identity. A changed basis suspends in flight and replans once supported. Known obstruction is Blocked, while unavailable evidence suspends or follows the
existing environment error contract. A missing retained dependency instead ends the attempt as `Blocked` with
`RangeSteering.DropRefusal` equal to `ContactDropRuntimeRefusal.DependencyMissing`. It never escapes to the host,
means absent geometry or selects the sharp policy. Flight requests ordinary bounded input, with no jump or direct-driver fallback.
Airborne descent is excluded from ground-stall accounting.

Player hosts pass one `PlayerPathInput` carrying this tick's finite camera yaw to `MoveToRange.Tick`, then create
the submitted command through that same value. Preview and live continuation use `EffectiveMovementBinding.Step`
with the exact ordinary camera-relative command, including fractional axis rounding. NPC callers retain the
world-space `StepTowards` contract. Exact body, run mode, timestep, frame and read-basis authentication remains in
force. Camera rotation between ticks is valid when the host shares that tick's input with the preview.

Supported completion comes from the actual supplied body, target cell/layer, non-swimming support and the 3D arrival
sphere, followed by a real supported Hold. Unexpected actual support cancels the remaining route and replans there.
Reset releases traversal while ordinary gravity and collision continue. `RangeMoveStatus.Dropping` carries requested
input. Both `NpcGroundMovement.Step` and `PlayerPathMovement.Command` transmit it alongside Following. Hosts still
own authority, advance the real controller and decide interaction timing from actual reach.

## Storage and compatibility

| Format | Selection | Identity and topology |
| --- | --- | --- |
| 1 | All Legacy, zero allowance | Frozen legacy layout and Stair recipe |
| 2 | Contact present, all allowances zero | Frozen Contact identity, resting heights and Stair recipe |
| 3 | No evidence capture, at least one positive allowance | Format 2 plus 13 bytes per profile and ordered Stair-plus-Drop acceptance bits |
| 4 | Evidence capture enabled | Explicit terrain flags, canonical evidence union and authenticated profile workloads |

The added identity fields are f32 allowance, f32 WalkSpeed, f32 RunSpeed and u8 AirMomentum. Disabled profiles store
canonical `0, 1, 1, false`, preserving the old normalized-ground identity. Enabled runtime profiles also validate
actual speed and momentum. Retained identity fields still include water, air control, gravity, terminal speed,
braking and body geometry. Load is explicitly version directed. Formats 1 through 3 regenerate their frozen
candidate recipes. Format 4 stores canonical ordered integer endpoints and validates bounds, admission, encoding
and storage capacity before use. Disabled profiles additionally match the frozen recipe and order with scalar
arithmetic only. Enabled profiles validate Stair/Drop shapes and positive non-aquatic OrdinaryContact Drop
permission without terrain regeneration. It authenticates acceptance and the proving route for each accepted link.

A same-engine allowance or profile terrain flag change is ProfilesChanged. Capture evidence selection changes are
OptionsChanged. Format 4 retains only accepted terrain proof reads and local certificate inputs, including raw
identities, whole queried columns and needed complete solid groups. Sharp/refused proof dependencies, recipe
closure and the global steep inventory are omitted. Excluded evidence throws DependencyMissing. Certificates
reconstruct on first use. Persisted f32 slope cosine and f64 footprint reach bits remove platform trigonometry
from load and reconstruction. The digest binds identity, evidence, profile bytes, candidate bytes and route facts.
Refused verdicts are authenticated bake-time facts and are never replayed on load. No trajectories are stored.

A same-engine allowance change is ProfilesChanged. EngineChanged retains comparison priority. Consumers must adopt
a released engine and rebake, never patch old acceptance bits or relabel an old graph. Candidate counting is data
only. Full-world bake cost and runtime preview timing require separate authorized measurements. The local horizon
and proof budgets remain fixed, and no engine default grants a game a 6 m permission.
