# Contact follow-ups C verification

## Task 1 baseline provenance

Production and support oracles were unchanged at `28273267c28785f566f4c8c50c84e008cd3e3df1`
when both freezes ran. The anchor capture compared all 1,952 original support-equivalence
rows and passed. Capture does not enable answer replacement or suppress a mismatch.
Every serialized float is its signed 32-bit pattern. Scene digests cover the original shape,
child, vertex, index and pose bits in construction order. The PRNG seeds and draw order remain.
The historical query generator is tested separately from the represented support oracle.

- Query fixture SHA-256 `1a77126404f2b33af252074ac40176a4d3ba462147f77a861b9de1169254b4b9`.
- Exact-install fixture SHA-256 ecda8a376cadedf630c0e39d48e0a7914ba3be4c1b570b1e84820b244b917e75.
- Exact-install rows 24. Identity, exact Y half-turn and cyclic installs, both zero signs,
  boxes, hull-centroid wrappers, coincident compound children and meshes. Complete footprint and
  neighborhood result values, provenance, witnesses, errors and join words are serialized as raw bits.
  Footprint reference capture is also compared bit for bit. Reference metadata is authenticated
  against the actual query world and lease before serialization.

The original zero-origin compound initial tick was the first red regression.
`CapturedStairExplicitSweepTests.CapturedStairZeroOriginInitialTickUsesRepresentedSupport`
compiled and failed with the following assertion, before any production or oracle edit.

```text
Assert.Equal() Failure: Values differ
Expected: Advanced
Actual:   Blocked
```

The refused request's axis is `(-0.14999999f, 0)`, radius `0.15f`, band
`[0.09999996423721313, 0.9000000655651093]`, slope cosine `0.66913056f`, contact band
`0.0001f`. The axis proposal starts at `(-0.14999999f, 0.91600007f, 0)`, the foot
proposal at `(-0.14999999f, 1.0560001f, 0)`. Both have identity orientation and
`0.80200005f` distance. These are round-trip operands from the actual request, not reconstructed
rounded diagnostics. The original test probe sizes and tuning remain unchanged.

## Audited source dispositions

The numbered table below follows the 51-site audit in research-1494 sections 1 to 4.
Task 1 covers the planar doors. Curved-specific source changes belong to Task 2.
