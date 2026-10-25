# Native authoring contracts

Status: implemented native surfaces, headless geometry, bounded water, prefabs, markers and foliage.
The [MapDoc README](../../KhaozEngine.MapDoc/README.md) owns API use, formats, limits and refusal details.
The [physics README](../../KhaozEngine.MapDoc.Physics/README.md) owns installation and physical queries.
This reference defines the surviving decision identifiers beside the boundaries they govern.

Core MapDoc remains independent of physics, rendering, GPU, editor, TileWorld and game policy.
Format adapters and headless installation live in separate packages. Public proofs use synthetic
fixtures. Private source, provenance, coordinates, digests and detailed comparison reports remain
outside public artifacts. A missing private input fails that acceptance path rather than skipping it.

## Authored surfaces and scoped acquisition

The following D identifiers are scoped to native authored terrain.

| Id | Current contract and reason |
| --- | --- |
| D1 | The private oracle harness is outside the solution/public CI, with a guarded entry and sanitized failures. Public synthetic proofs cannot certify private source parity. |
| D2 | A partial save compares the exact loaded manifest generation before writes/sweep. IsPartial includes unread surface storage as well as tiles. A surfaces-only window cannot bypass no-loss checks or claim complete identity. |
| D3 | Authored lattice addresses and exact generated world vertices are distinct. Shared-corner owners and anchored record references preserve exact ownership across negative slots and mixed resolutions. |
| D4 | Fine conversion removes the coarse physical cells and creates exact matching rim/subdivision seams. It does not snap a straddling record or silently move its owner. |
| D5 | Acquire publishes detached factory-owned witnesses/results. A stored source pins its manifest generation and never refreshes into newer data. Missing old payload remains missing. |
| D7 | Lower/upper bounds may use arbitrary rational lattices. Common refinement uses exact clipping and counts every attempted polygon pair before bounding-box rejection. One immutable bound-face context per call compiles the demanded union once. |
| D9 | LegacyExteriorV1 is explicit compatibility-only lower coverage in an Exterior/OpenTop recipe. Known legacy void/NoDraw may return released bilinear fallback, never a physical face, capture support, cave floor or missing-data replacement. |
| R1-T5-1 | Native editor/history mutation carries its matching immutable asset closure explicitly. Replaying a snapshot against an unrelated closure cannot establish its authored identity. |

MapExactValue is the reduced long/long scalar with wide intermediates. Exact overflow is
NotRepresentable, not a capacity failure or tolerance. Authored denominators stop at 64, while
generated refinement intersections have no authored-address denominator restriction. Whole-metre
anchor subtraction precedes float conversion. Paint cuts do not open or fill physical apertures.

Scoped acquisition shares one unique patch-key allowance across discovery, anchors and bounds.
Visits charge before cache checks, metadata/ranges before inspection and decode attempts before I/O.
Successful new page decodes and operation-local deltas remain separate counters. Known-empty and
unavailable reservations still spend keys. Unknown incidence, missing geometry or overflow never
publishes a complete prefix. The captured view does not reread its producer after publication.

## Physical and water boundaries

OA22 keeps support seating and movement policy out of native geometry construction. Stance candidates
use a caller-bound validator. Terrain installation preserves canonical faces in chunks of at most
1024 triangles, with each exact vertex at most 64 m per axis from its chosen whole-metre anchor.
Registration subtracts the physics origin once and rolls back failed insertions. Interaction envelopes
derive from collider geometry, with a one-metre minimum vertical reach height. That minimum is not
action range, foliage clearance or a scaled physical collider.

Navigation tile identities and affected-tile invalidation are data contracts. Incremental rebake,
on-demand loading and route policy are separate owners. Physical relations use an authenticated
physics lease and caller-supplied segment/polyline. They do not infer a route or live door state.
Portals retain authored-open semantics.

OA27 defines one stored SurfaceY, exact XZ domain and explicit covered spaces per water body.
OA28 permits no implicit exterior. OA29 leaves capsule overlap/span certification to the movement
adapter. Point wetness is lower-inclusive and upper-exclusive, with ceiling ties closed. A body
meeting no covered footprint refuses. Missing or ambiguous membership never becomes dry.
The half-open XZ boundary policy (OA7) defers to space ownership where boundaries coincide.

R4-P1 places containment at geometric coverage, publication, compile and build boundaries. R4-P4
requires the complete water set at every publication because a loaded document has no reusable
containment certificate. Terrain or space edits can therefore refuse even when they do not directly
edit water. A present empty bounded-water section means dry, while absence retains legacy routing.
Water remains separate from static descriptors. Body levels never follow terrain edits implicitly.

## Prefabs, interiors and publication

OA32 retains format version in native identity. A format migration moves that identity even when the
new optional section is absent. R5-P identifiers refer to the implemented prefab contracts below.

| Id | Contract and rationale |
| --- | --- |
| R5-P1 | Resolver (1,2), AuthoredBindingsV2, expansion 1 and effective query policy 2 remain explicit identities. Analytic routing stays separate. |
| R5-P2 | Instances, bindings, overrides, transfers and building intent occur once in native globals. Immutable local geometry belongs to prefab resources, not tile copies. |
| R5-P3 | Effective transforms compose in binary64 until a shared point freeze. Public authored transform composition retains its API. Backend pose approximation is a separate boundary. |
| R5-P4 | Every local/world reference occurrence has a typed scope slot. Explicit Surface children contain no embedded records, which have their own bound Record homes. Text equality cannot substitute for scope. |
| R5-P5 | Deepest room occupancy carves ancestors into identified fragments with boundary provenance. Repeated parent intervals remain distinct fragments. |
| R5-P6 | A room prism references an authored space, parent and storey. A semantic boundary does not manufacture a physical wall. |
| R5-P7 | Positive-volume peer overlap refuses publication. Roof groups are explicit sets, not transitive portal flood fill. |
| R5-P8 | Support resources install before seating queries. Physics consumes their descriptors. Uneven-site seating is diagnostic, and independent coincident floors remain ambiguous. |
| R5-P9 | Render-only material subdivisions preserve canonical parent faces. Explicit priority and ordinal paint ID resolve order, while equal-priority conflicting positive-area paint refuses. |
| R5-P10 | Water compiles occupied fragments and their provenance. The older space/XZ classifier returns Unresolved when fragments disagree. |
| R5-P11 | Roof links use only their named groups and the explicit inventory. Unresolved membership keeps Auto roofs visible. Hidden intent preserves shadows/sky picking and disables colour/boom blocking without changing statics. |
| R5-P12 | Publish immutable asset bytes before one document/closure switch. A refused switch may leave unreachable immutable bytes, never changed roots. Undo restores the captured closure. |
| R5-P13 | Preflight all retained and expanded collections before copying/remapping. Shared immutable bytes do not waive per-instance expansion charges. |
| R5-P15 | Unpack preserves standalone homes, precise poses, identities and retired transfer reservations. Existing placement commands own later component edits and exact history. |
| R5-P16 | ParentSpace is optional only for parent-independent resources. Rooms, water and parent references require authored context, with no inferred exterior. |
| R5-P17 | Closure-aware preparation receives the matching closure. Native history accepts only after outer publication. Focused write sets and complete-snapshot callbacks have explicit separate contracts. |
| R5-P18 | Light payload 1 positions/radii use owner-frame metres. Marker data retains XYZ/yaw and caller metadata. Annotation expansion is not rendering. |
| R5-P19 | Remove retains the previous typed override. Restore reuses customization and reservations instead of allocating replacements. |
| R5-P20 | Missing-Y prefab support requests transform once, sample the effective floor and install that world height directly. The sampled height is never scaled again. |
| R5-P21 | Accepted generic native callbacks publish fully captured before/after documents with full validation. Focused commands retain named write-set isolation. |
| R5-P22 | Unpacked attached support is materialized under transferred IDs. Standalone expansion reuses it and suppresses duplicate materialization. |
| R5-P23 | Leaf light resources produce scoped, bound implicit Light children with transfer lifecycle. Explicit children retain distinct ownership. |
| R5-P24 | Asset-local materialization rows preserve captured precise support/light frames through extraction, export and local edits. Suppression precedes implicit enumeration so there is one producer. |

## Marker authority and compatibility

OA36 unifies native markers under building intent and prefab Marker children. Roles, habitat and
spawn policy are consumer vocabulary. Enabled views do not replace exhaustive validation/persistence.

| Id | Contract and rationale |
| --- | --- |
| R6-P1 | Payload 2 requires Explicit or Support. Support stores positive-zero Y plus a closed typed binding. No height mode or owner is guessed. Payload 1 retains its bytes. |
| R6-P2 | Effective value provenance is separate from witness-bearing MarkerSupports. Existing payload-1 consumers do not acquire fabricated provenance. |
| R6-P3 | Global ordinal names/IDs, disabled markers included, validate before support reads, callbacks or statics. Repeated prefab names require explicit overrides rather than suffixes. |
| R6-P4 | An immutable median-split point index with outward bounds narrows rectangle candidates before exact half-open XZ testing. Membership runs only for returned points and retains typed refusal. |
| R6-P5 | Legacy spawn adoption explicitly maps source kind/ID to destination ID, name, role and height. Source lists clear atomically only after every destination is accepted. |
| R6-P6, R6-P13 | Root metadata and verified prefab bytes select an enabled caller-role start by distance, then ordinal ID, without support reads. This is not a windowed native editing session or whole-world identity proof. |
| R6-P7 | Unadopted native spawn readouts require a caller-named Surface owner. There is no guessed topmost floor, zero height or analytic fallback. |
| R6-P8 | Disabled bound markers still validate support. Spatial changes add Residency only, metadata changes add no invalidation and same-value edits have empty effects. |
| R6-P11 | Initial keyed placement overrides allow a marker-bearing prefab's rename in the same accepted edit, before uniqueness validation. |
| R6-P12 | Annotation identity remains effective-annotations/1 for payload-1-only markers and selects /2 when any effective marker has payload 2. Payload-1 row encoding remains unchanged. |
| R6-P14 | Bound height is the shared support query's float WorldY, then its exact represented value. Markers and placements use one seating authority, not separate exact/float rules. |

## Native foliage ownership

OA39 separates sparse density from explicit placements and legacy analytic scatter. Compatibility
policy legacy-tile/1 preserves released interpolation, normals and eligibility. A geometric replacement
needs a new policy version and differential. Surface/Space bindings never select another floor on
missing, ambiguous, unavailable or over-budget data.

| Id | Contract and rationale |
| --- | --- |
| R7-P1 | Density payload uses bounded 32-square byte chunks. Sparse addressability does not promise full-area allocation. Missing chunks read zero, advertised unavailable chunks refuse and explicit zero chunks retain identity. |
| R7-P2, R7-R2 | Tiled storage uses verified content-addressed packs per layer/storage region. Logical chunk digests stay independent of packing. |
| R7-P3, R7-R1 | Capture one complete immutable generation context under separate construction budgets, reuse it across chunk calls and refuse partial residency before expansion. No whole-payload serialization per chunk. |
| R7-P4, R7-R4, R7-R5, R7-R7 | Normalize both binding kinds to selected support surface. Clearance compares each owner's seated support through the layer's ordinal table, not geometric Y or a roof's clearing target. Solid/Door require equal ordinals, RoofAbove greater. Unmapped owners fall back to canonical surface identity for Solid/Door only. |
| R7-P5, R7-R3 | Differential comparison belongs to stable 64-square lattice-address blocks. Generation still belongs to world rectangles. Moving a jittered sample between chunks cannot turn a retained address into remove/add. |
| R7-P6 | Query totals bound retained samples/bytes, and differential totals bound candidates, changes and reconciliation. Observations remain provisional until Complete. A refusal issues no usable output or token. |
| R7-P7 | Public MapDocumentEditFinalizer joins pure, command, callback and wire publication. Foliage invalidation is cosmetic and adds no Physics/Nav/Residency. |
| R7-P8 | Brush uses byte density, ToEven rounding and bounded work. Hardness one includes the radius at full strength, while lower hardness has zero boundary weight. The differential token binds before/proposal identities. |
| R7-P9 | Compatibility scale/thinning rank compare exact binary32 bits. Position/orientation tolerances in differential acceptance cannot silently expand after a mismatch. |
| R7-P10 | Render handoff transforms source-unit-adjusted bounds with the original CPU matrix and explicit motion padding. Missing extent/padding refuses instead of becoming zero. Styling does not change generation. |

The layer ID participates in sample identity but not randomness. Occupied space is separate from
support provenance. Owner-based clearance transforms effective placement geometry once, never the
one-metre interaction envelope. Chunk-local preparation and shared support selection avoid one full
support query per sample. No timing claim follows from counted work or conservative retained-byte
charges. Rendering, residency and appearance acceptance remain separate from this headless handoff.
