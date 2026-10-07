# Low lip Physics correspondence boundary review

Status: source-only peer assessment. No API, fallback, anchor implementation or compute authorization.
Reviewed current engine main `e34cd884be10c22bee10b82b76aacc7a3514dd17` and swimming
`997739a134b45529f955ab91d32e9c6156b8cd46`. Pivot owns the #1270 repair and owner decision.

## Existing capabilities do not establish the required relation

- `KhaozEngine.Physics/Queries.cs` returns distance, point, normal and nullable static handle for rays
  and sweeps. Neither result identifies a geometric face or expresses correspondence uncertainty.
- `KhaozEngine.Physics.Bepu/HitHandlers.cs` receives a ray `childIndex` but does not retain it.
  The nearest-hit handler also culls other candidates. Equal-distance competing faces are not a
  complete incident-face set. The sweep handler retains no geometric face and sets location/normal
  to default on zero-time initial overlap.
- `BepuPhysicsWorld.Queries.cs` resolves the static handle, not a child/face identity. A single static
  can contain many mesh triangles or compound children. Same-static equality cannot identify its face.
- `IPhysicsWorldQueryView` preserves selection and source identity, but adds no face operation.
  `PhysicsGroundProbe` and `PhysicsColumnProbe` wrap downward rays. They cannot recover discarded
  geometric provenance or certify an edge miss. `ComputePenetration` returns one deepest MTV only.
- Swimming's unreleased `IPhysicsCapsuleContacts` adds independent contact constraints, not geometric
  face facts. `CapsuleContactCollector.AddManifold` takes the normal/feature from a collision manifold,
  discards its contact position and can deduplicate constraints differing only in feature ID.
  `CapsuleMeshContacts` applies contact smoothing. No public feature-to-face resolver exists.
- Swimming's new `CapsuleSweepResult` represents distance bounds only and has no implemented Bepu
  certificate. It supplies neither this correspondence nor an error budget that #1270 may borrow.

The 24-case observations reported by pivot are consistent with this missing contract. This review
read the owned low-lip proposal and its recorded result, but did not rerun or independently validate
those runtime measurements. A few-micrometre inset or a more permissive ray is not a source-backed
remedy. A successful ray against the same static also cannot exclude an unrelated nearby face.

## Narrow optional capability to take to the owner

If the legacy repair continues, propose a separate optional, selected-view geometric-feature query.
Its job is to prove a local capsule/solid feature relation at a supplied pose. It does not certify a
swept interval, select a gameplay support height, or sample native terrain/space membership.
Exact signatures and numerical bounds still need design and approval.

The minimum contract needs:

1. The actual capsule and pose, expected live static handle and bounded local geometric neighborhood.
   An old `SweepHit.Point` may narrow a search only as an untrusted hint. It cannot be the proof of
   correspondence or justify expanding a numeric bound until a face appears.
2. Exact selected-view restrictions, mobility/filter semantics and static identity. An excluded,
   missing, stale or dynamic target cannot become a static support result. A bare integer from another
   source is not portable identity. Fresh lookup and geometric validation happen inside the captured
   source/generation/frame read interval, with no unrestricted-owner query fallback.
3. Backend-derived geometric plane/normal, finite face or patch membership and local child/feature
   provenance, plus the proved relation to the capsule's actual closest/contact feature. Plane
   extension alone is insufficient. Supporting triangles must belong to the queried solid geometry.
4. Explicit face-interior versus edge/vertex incidence. All relevant incident geometry must be covered
   before returning a usable result. Coplanar aliases can only coalesce under a proved rule.
   Distinct competing features, uncertain membership and incomplete neighborhoods refuse without a
   usable partial answer. A nearest normal or arbitrary first face is not a substitute.
5. Separate known absence/ambiguity from unsupported or unresolved work. The accepted shape set,
   transforms, coordinate/scale domain, spatial/angular error and finite work caps must be explicit.
   The measured ray offsets are examples, not a universal bound. No imported swim tolerance applies.
6. Output lifetime and numeric policy sufficient for the legacy caller and its bake identity to
   validate the result. No wire identity, native resource registry or new bake format is implied.

A finite local face query can use real backend shape geometry. It must not become a second native
sampler. It can share a backend geometry helper later only after that helper has a real shared shape
and both owners review the affected semantics. The current capsule-contact path must not be silently
retrofitted to mean geometric-face provenance.

## Decision that must precede the API

A genuine top/wall corner has two incident geometric faces. Requiring a unique face everywhere would
correctly refuse that crease but could also leave the target low-lip repair unavailable. Conversely,
choosing the flattest face of any nearby static would silently authorize unrelated support.

The owner-facing design must state when a completely identified incident feature is allowed to
supply one top support face, and when competing support interpretations remain ambiguous. That rule
belongs with the legacy support eligibility policy, constrained by backend-proved finite geometry.
Do not hide it in an epsilon or call every two-face crease an automatically resolved top.

Recommendation: acknowledge that the existing seam is insufficient, then request the narrow optional
feature-correspondence design with that edge/support rule made explicit. Keep the repair gate closed
until the geometric relation and numerical domain are proved. This assessment does not approve the
new API or claim it will make all existing corner fixtures eligible.

## Draft shared-seam review

Reviewed the complete proposed spec at
`docs/superpowers/specs/2026-10-07-capsule-feature-correspondence-design.md` in the owning low-lip
worktree, SHA-256 `93ea3b111e55d3daae10f686cf0d3eb072a116516833e01b529838c70e66e0de`.
This is a bounded lifetime/selection compatibility review, not numerical verification or API approval.

The optional feature query is compatible in direction. It neither replaces existing lease signatures
nor expands the certified-sweep contract. Its explicit static target, installed geometry, incident
feature classification and independently declared numerical domain preserve the agreed separation.
The following lifetime details must be explicit before freezing the shared portion:

1. Authenticate the actual owner-issued live lease and reject wrong-thread use before attempting
   `EnterQuery` or any other monitor acquisition. `ReadLease.AssertCurrent` deliberately checks thread
   first in `BepuPhysicsWorld.QueryLease.cs`. Calling a new forwarding method from a foreign thread
   must not block on the gate held by the very lease being validated. After entry, recheck the actual
   receiver and currentness before publication. No signature replacement is needed.
2. Bind consumption to the exact original lease instance, not only source/origin/generation. Acquiring
   and disposing a read lease does not advance `_queryGeneration`. A later lease can therefore have
   identical metadata after the first expires. The result needs an enforceable original-lease binding
   or validation operation retaining it. A later live lease must not revive an old result.
3. The lease certifies its owner, not a selected view. `QueryView.AcquireQueryReadLease` returns the
   owner's lease. Preserve the actual query receiver separately by reference identity and forward its
   exact exclusions/filter. Do not infer selection from `lease.SourceWorld`, and do not fall back to
   that owner. The draft's receiver field is the right boundary. A receiver's disposal must invalidate
   result use even when owner/generation metadata still match.
4. Keep lazy derived geometry caches under the existing gate without treating cache publication as a
   physical mutation. Actual shape/pose changes remain under the mutation fence and advance the
   generation. A generation change is conservative evidence of a changed source, not a portable
   geometry hash or an exact count of successful geometry edits. The current fence can advance it
   before a mutation that subsequently fails validation.

Two contract clarifications avoid overstating proof. `StaticHandle` is an integer, so lookup cannot
prove which source originally issued an equal-valued integer. Capturing it from the selected source
within the live interval is a caller obligation unless a future separately designed scoped token
encodes that provenance. Do not promise detection of all foreign integer handles. Also state that
refusal/exception leaves the caller face span untouched, using private bounded scratch and committing
only after complete validation. Zero written count alone should not hide partially published faces.

No change to the existing Physics lease interfaces is requested. The shared `QueryView` implementation
still requires serialized edits, and any extraction of the prerequisite needs its own reviewed,
verified integration. None of the proposed 2048 m domain or face-query error ceilings is a certified
sweep policy, F3 tolerance amendment or native support acceptance.
