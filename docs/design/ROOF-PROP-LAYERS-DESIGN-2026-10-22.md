# Roof prop layers

Accepted implementation contract for 21.12.0, 2026-10-22.

Ordinary roofs stopped at `PropDrawRadius`, while other prop layers could retain merged geometry through Decor.
Buildings need the latter reach without exposing a hidden roof or removing the shadow above an interior.

| Decision | Contract and reason |
|---|---|
| D1 | Derive roof layers from catalog `IsRoof`. Every selected archetype must be a roof or every one must be non-roof. Refuse mixed layers before upload with the layer and one archetype of each kind, so one cluster has one visibility policy. Keep selection exclusive across layers. |
| D2 | Refuse `CutInsideInteriors` on roof layers. Roof removal uses the observer's house rule, rather than the interior-height cut used by props inside buildings. |
| D3 | Share one roof visibility rule between ordinary and layered submission. Retain authored object ID, plane and footprint in detached generation snapshots, so both paths make the same decision and stale HLOD handles keep their own metadata. |
| D4 | Visible individuals use normal layer LOD and HLOD fades. Hidden individuals submit shadow-only at identical transforms and fades when the layer casts shadows, preserving shade without colour. |
| D5 | All-visible merged clusters draw normally. All-hidden clusters submit merged shadow-only. Mixed visibility forbids merged colour and falls back to resident individuals without the HLOD fade floor. Without individuals, submit merged shadow-only, which fails toward shade rather than leaking a hidden roof. |
| D6 | Retain merged roof clusters through Decor exactly like other layers. Submit neither colour nor shadow past `DrawRadius`, preserving a bounded layer lifetime and draw reach. |
| D7 | No configured roof layer leaves ordinary roof submission, `PropDrawRadius` and the synchronous map-editor profile unchanged. Existing consumers opt in deliberately. |
| D8 | Indoor flags, connected interiors, roof links and interior-cut inputs remain document queries, independent of draw routing. Moving roofs into a layer cannot make the house disappear from those queries. |
| D9 | Extend the scene adapter and shared cluster renderer for visibility-aware individual and merged shadow submission. Visible and shadow-only placement counters include layers, counting a complementary representation pair once. New routing lives in dedicated types to keep build and lifetime ownership cohesive. |

The consumer-shaped fixture uses separate structures and roof layers. Both have draw radius 576 m, LOD
distance 0, HLOD distance 192 m, HLOD crossfade width 32 m, weld cell 0, shadows enabled and interior cut disabled.
Zero weld cell preserves the authored geometry while merging it, so straight walls keep their shape.

Headless tests cover construction gates, ordinary-path exclusion, per-house hiding, mode overrides, Decor,
partial-cluster fallback with and without individuals, generation replacement, transforms, fades and radius culls.
The living API contract is in `docs/USING-KHAOZENGINE.md` and the TileWorld.Render3D and Terrain.Render3D READMEs.
