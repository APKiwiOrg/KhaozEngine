# Bug fix: walkable-surface side-block shoves you off domed prop tops

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Collision + Locomotion) — bug fix in the 7.55 surface/collision interaction

## Symptom

Jumping onto a rock fails: landing on (or descending onto) a rock teleports the player to the side and
drops them onto the ground. The flat-topped demo platform behaves correctly; domed/bumpy rocks do not.
(7.56 animated characters is cosmetic and unrelated.)

## Root cause (verified by inspection)

The height-aware side-block gate in `KhaozEngine.Collision.WorldColliders.Resolve(footY)`:

```csharp
if (footY >= c.Top - skin) continue;   // standing on/above it -> not a side hit
```

skips a collider's side-block only when the feet are at/above `WorldCollider.Top` — the prop's **single
max solid top**. But the walkable surface (`WorldSurfaces.Query(x,z)`, a baked top-down height grid) is
**usually below** that max on a domed/bumpy rock, and `CharacterMovement.Step` lands the capsule on that
surface (`support = max(terrain, surface)`). So once you're standing on the rock, `footY = surfaceHeight
< collider.Top - skin` → the gate misclassifies "standing on the surface" as "hitting the side" → the
collider pushes the capsule centre out by the radius → it leaves the rock and falls to the ground.

Only points within `skin` (5 cm) of the collider's max `Top` are standable, so a domed rock shoves you
off almost everywhere; a flat platform (surface ≈ `Top`) works.

## Fix

The "am I standing on it?" gate must use the **walkable surface height under the player**, not the
collider's single max `Top`. Once the feet are at/above the surface they rest on, the prop's side must
stop blocking.

Implementation (chat picks the cleanest; the tests define correctness):
- Thread the per-position support/surface height into the height-aware `Resolve` from
  `CharacterMovement.Step` (it already computes `Support(x,z)` = `max(terrain, surface)`), and gate the
  side-block on `footY >= surfaceUnderPlayer - skin` instead of `footY >= collider.Top - skin`; or
- per-collider, gate against that collider's surface-top at `(x,z)` (couple the walkable surface to its
  collider) — more precise but more work.

Keep a genuinely-below-a-prop-side approach (feet below the surface you'd stand on) still blocked, and
keep neighbouring taller props blocking where feasible (if the simple support-gate makes a taller
adjacent prop walkable-through while you stand on a shorter one, note it as a known limitation — the
domed-rock fix is the priority).

## Tests (TDD — write the failing test first)

1. **Unit (the root cause)**: a domed prop where `surface(x,z) < collider.Top`. A capsule whose feet are
   at the surface height is **not** pushed out (currently it is). A capsule whose feet are below the
   surface (hitting the side) **is** pushed.
2. **Movement integration**: `CharacterMovement.Step` lands a falling capsule on a domed rock's surface,
   and the **next** tick does **not** shove it off — it stays on the rock and can walk its top.
3. **Regression**: the flat platform still standable; trees still block (no surface, full side-block);
   standing on the rock's highest point still works; nothing below the surface walks through a side.

## Scope

- In: the gate fix (Collision/Locomotion) + the three tests; **patch** version bump; CHANGELOG note.
- Out: a broad redesign of the collider/surface coupling; the neighbouring-taller-prop edge case
  (note it if the simple fix introduces it); anything in Render3D (independent of the GltfLoader fix).

## Engine-first / sequencing

`Locomotion` + `Collision` fix; every game with walkable surfaces benefits. Independent of the GltfLoader
node-transform fix and the animation work (different files), so it can run now. If another engine release
is in flight, check tags and bump past.
