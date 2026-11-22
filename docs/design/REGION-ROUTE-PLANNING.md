# Fixed-quota region route planning

## Request and owner lifetime

A follower can keep one region request across ticks. The request freezes the start feet, goal region,
agent radius, query budget and the straightener's one-shot fallback. The request uses the same edge
order, costs, progress scoring and waypoint rules as synchronous planning. For those inputs it returns
the same status, waypoint count, kinds, layers and position bits under every quota schedule. A quota
cut never publishes a provisional route. Complete, Partial and Unreachable are all finished results.

A request takes one prewarmed workspace from its profile's pool. The lease covers start admission,
region membership, search, reconstruction and optional route straightening. The same pool serves the
profile and any of its bound straighteners. If a lease is busy, requests queue in arrival order and
spend nothing until granted one. Completion, cancellation and Reset release the lease. The oldest
waiter gets it immediately and starts work on its own next tick. Disposing the pool cancels all leased
and queued requests. A finished route already released its lease and can still be adopted.

The pool validates profile identity and query limits before acquiring a lease or consuming the
straightener's fallback. Start admission and each shortcut guard use the same capture, height and
crossing rules as synchronous admission. Each guard resolves both endpoints and their layers, scans
footprint rows and columns, then checks directed crossings. Straightening retains required anchors and
checks the centre, positive and negative side lines in order. The cursors retain scan state between
calls without allocating a scan buffer. Beginning a request allocates its handle and admission.
Prewarmed scratch stays fixed during advances, and a nonempty published route allocates its result.

While the route is pending, the follower drops the previous path and holds at zero motion.
`MoveToRange` preserves its stall window during this wait. An exhausted Partial route's cooldown
wait still counts toward stall. A begin that finishes immediately can steer on that call. If a later
slice finishes, it still holds on that call and the owner considers adoption on the next tick. This
keeps a final slice and an expensive steering or arrival step on separate calls.

Adoption checks current membership first. It accepts the result if the actual feet equal the frozen
start or resolve to the same admitted start node. Otherwise the follower starts from the current feet.
Goal drift alone lets the request finish, so a moving target cannot starve a request by repeatedly
restarting it. After adoption the usual goal drift, corridor and terminal checks can refresh the route.
Target identity, teleport and manual cancellation require Reset. Changing the mover's target shape,
range or capsule clears pending work through its shape reset.

Consumers opt in through a follower or a mover. Direct request handles and straightener-specific
pools stay engine-owned so the public route goes through the owner and its space, lease, budget and
adoption checks. Point queries and custom planners plan synchronously. A view with query-time area
exclusions also plans synchronously because one search edge can scan a whole footprint under that
traversal constraint. Including that scan in one ordinary edge charge would hide its work.

## Work contract

Every indivisible operation spends a fixed positive integer weight. A call runs the first operation
and then stops before any next operation that would exceed the quota. It spends at most the larger of
its quota and the largest weight. `PathPlanningConfig.MinimumQuotaUnits` covers the largest weight.
A held request with total work W greater than zero finishes within W calls with a workspace. For a
fixed quota q at least the largest weight L, a nonfinal call spends at least q - L + 1, so the request
finishes within `ceil(W / (q - L + 1))` calls. A zero-work request finishes on the first call with a
workspace. A mover that is suspended or waiting for a lease makes no advances on those ticks.

Equal frozen inputs and equal workspace stamp history spend equal units. Publication additionally
depends on the owner's calls and quotas, exact workspace grant order and adoption. To replay it,
hold simulation inputs, engine version, quotas, pool sizes and memberships, per-tick call order and
Reset or cancellation events fixed. The numeric kernel also determines cross-platform bit equality.

A weight counts work, so data can change its elapsed cost. An edge may push to a heap, a captured column
may hold many surfaces, a predicate may perform caller work and publication may allocate storage for a
large route. The quota leaves those times and first-use costs unbounded. Synchronous queries keep their
own scratch and directly drive the same route rules without leasing a workspace.

## Weight table

| Work kind | Units | One operation |
| --- | ---: | --- |
| Layer | 2 | Probe one layer while resolving the starting feet. |
| Snap | 2 | Test one candidate cell for a snapped start. |
| MemberBox | 2 | Prepare the candidate region bounds for one layer. |
| Member | 4 | Test one candidate cell and record its membership. |
| CertificateLabel | 1 | Check one region member's connectivity label. |
| CertificateProgress | 1 | Compare one node in the certified component against one member. |
| Wrap | 2 | Clear one fixed block of stamps on workspace generation wrap. |
| Seed | 4 | Seed the open set with the start node. |
| Pop | 4 | Take one open-set entry and check its search state. |
| SearchProgress | 1 | Compare the current search node against one region member. |
| Neighbor | 6 | Test one ordinary neighbour, including a possible heap push. |
| Link | 3 | Test one stored directed link, including a possible heap push. |
| Parent | 1 | Follow one parent during reconstruction. |
| Reverse | 1 | Swap one pair while reversing the reconstructed chain. |
| Decode | 2 | Decode one node as a waypoint. |
| Allocate | 2 | Allocate the finished search route's waypoint array. |
| Copy | 1 | Copy one decoded waypoint into that array. |
| EndpointStart | 3 | Validate an endpoint and set up its footprint bounds. |
| SegmentStart | 3 | Validate and set up one swept segment footprint. |
| FootprintRow | 3 | Project the conservative candidate bounds of one footprint row. |
| FootprintColumn | 7 | Test exact geometry and the surfaces in one captured column. |
| EndpointLayer | 5 | Test one endpoint layer's surface against the current nearest candidate. |
| CellCrossing | 6 | Check one directed graph crossing, including a cross-layer Stair. |
| ValidateFeet | 5 | Check one raw waypoint's resting feet. |
| Candidate | 7 | Test one shortcut candidate's predecessor, kind, layer and resting feet. |
| SideOffsets | 5 | Compute one pair of side offsets after the centre guard passes. |
| KeepAnchor | 5 | Check and record one retained waypoint in prewarmed storage. |
| StraighteningAllocate | 2 | Allocate the final straightened waypoint array. |
| StraighteningCopy | 1 | Copy one retained waypoint into that array. |

## Calibration

The route calibration diagnostic used fixed fixtures, phase windows and whole requests cut at several
quotas. It warmed the requests, recorded per-kind operation counts separately and timed slices in
held no-collection windows. Workloads dominated by a kind and isolated phase probes estimated its
cost. The crossing probe compared windows of 128 and 256 crossings, with a mixed fixture as a cross
check. The data price Neighbor, Pop and SearchProgress as an expansion bundle. They cannot price
those three separately. The projected-row probe is indicative at the clock's resolution, so the
mixed fixture supplied the FootprintRow estimate.

The measurements came from one busy development machine on 2026-11-22 (Apple M2 Max, arm64, .NET 10,
tiered compilation off). Rescaling the measured operation costs gives 5.7 to 6.7 ns per unit for the
common kinds under the calibrated table, about 6 ns. Parent and Copy are about 4.3 and 4.4 ns per unit
at weight 1. Search in the constrained unreachable fixture was about 2.7 ns per unit against 6.7 ns
for the open-ground expansion fixture. The model therefore transfers poorly to some searches. For
the footprint-heavy requests at quotas of 1,024, 4,096 and 16,384 units, measured under the weights
in force before calibration, the 99th percentile advance cost per unit was about 1.2 to 1.45 times
the median. Advances were not measured again under the calibrated table. Quotas of 64 and 256 were
left out of every estimate because the clock reads are a visible share of so short an advance. The
measurements supply no elapsed-time bound.

FootprintSetup, shared by EndpointStart and SegmentStart, and the straightening feet, candidate and
offset charges were set by scaling their related segment charges and rounding up. Straightening
allocation and copy keep the analogous search weights. Layer, MemberBox, CertificateLabel, Seed,
Wrap and Allocate lack separate cost estimates and keep their weights. Member and Link were measured
and their charges target the common per-unit cost. The numerical table only changes accounting.
The same operations and route bits are covered by literal traces and publication digests.

The calibration ranked integer tables by the spread of cost per unit across kinds that accounted for
at least 1 percent of a whole request. A table with the minimum spread of 1.32 uses Neighbor 12, Pop 8,
SearchProgress 2, FootprintColumn 11, CellCrossing 9, EndpointLayer 7, FootprintRow 4 and Decode 3. The
shipped table's spread is 1.55. Parent and Copy hold under 2 percent of each request and already have
the minimum weight of 1. Among kinds with at least 2 percent of a request, the shipped table's spread
is 1.17 compared with 1.29 for the minimum-spread table.

The shipped table takes the same or fewer calls at all three estimated time budgets on the measured
request shapes. At 0.3 ms it projects 5, 15 and 6 calls for the median, largest and unreachable
requests. The minimum-spread table projects 5, 16 and 7. The shipped table keeps the expansion bundle
weights and keeps the largest operation at 7 units instead of 12. A smaller largest operation lowers
the quota floor exposed by `PathPlanningConfig.MinimumQuotaUnits` and the quota a call can leave unused
when its next operation does not fit, at most 6 units instead of 11. Those tradeoffs carry more weight
than reducing the spread of the two rare reconstruction steps. See the [consumer guide](../USING-KHAOZENGINE.md#planning-routes-across-calls)
for measuring quotas on deployment hardware.
