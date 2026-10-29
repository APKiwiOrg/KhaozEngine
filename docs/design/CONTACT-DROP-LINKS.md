# Contact navigation Drop links

Contact profiles independently opt into one-way unpowered descents with `MaxDropMetres`. Zero is the default.
The permission authorizes baked route topology, not manual falling or fall damage. Legacy, aquatic and public
explicit profiles cannot enable it. Numeric allowances must be finite and nonnegative, with signed zero normalized.

## Candidate and physical certificate

After node admission, ordinary directed exits and Stair proofs, sources scan in layer, Z, X and fixed neighbor
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

Workers own their fall state, full-tuning bindings and source sessions. Candidate ordinals precede parallel work,
verdicts merge by ordinal and the lowest serial failure wins. Final snapshot authentication precedes publication.
No trajectory, destination verdict or prefix is borrowed from another body, profile or attempt.

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
The host calls `CompleteDrop()` only after its controller witnesses supported arrival. It advances one transition
and throws when no Drop is active. `Reset()` is explicit cancellation. Hop's geometric lunge contract is unchanged.

## Range execution and hosts

`MoveToRange` owns a ContactDropFollower with Align, Launch, Falling and Landed phases. Alignment follows admitted
support to the named source using the shared physical arrival rule. The follower's broad accept radius never
authorizes departure. Commitments, buffered jumps and unrelated airborne bodies remain Suspended.

Before launch, one bounded preview checks the actual body, walk/run mode, SpeedScale, momentum, timestep and live
environment. It uses the same bounded control law and original-boundary composition as the bake. The preview slice
count is the floor of the baked duration divided by dt, including both Holds. It neither searches for another edge
nor publishes a predicted arrival. The predicted continuation is checked against actual motion, control inputs and
read identity. A changed basis suspends in flight and replans once supported. Known obstruction is Blocked, while unavailable evidence suspends or follows the
existing environment error contract. Flight requests ordinary bounded input, with no jump or direct-driver fallback.
Airborne descent is excluded from ground-stall accounting.

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
| 3 | At least one positive allowance | Format 2 plus 13 bytes per profile and ordered Stair-plus-Drop acceptance bits |

The added identity fields are f32 allowance, f32 WalkSpeed, f32 RunSpeed and u8 AirMomentum. Disabled profiles store
canonical `0, 1, 1, false`, preserving the old normalized-ground identity. Enabled runtime profiles also validate
actual speed and momentum. Retained identity fields still include water, air control, gravity, terminal speed,
braking and body geometry. Load is explicitly version directed, regenerates candidates from stored grids, exits
and physical anchors, bounds counts before allocation and authenticates the ordered acceptance bits.

A same-engine allowance change is ProfilesChanged. EngineChanged retains comparison priority. Consumers must adopt
a released engine and rebake, never patch old acceptance bits or relabel an old graph. Candidate counting is data
only. Full-world bake cost and runtime preview timing require separate authorized measurements. The local horizon
and proof budgets remain fixed, and no engine default grants a game a 6 m permission.
