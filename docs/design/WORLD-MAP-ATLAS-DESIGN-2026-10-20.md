# World map atlas rendering

## Intent

Add reusable map rendering controls for a minimalist atlas while retaining the existing CPU raster,
chunk cache and GUI texture path. The requesting consumer approved engine-first implementation.
No consumer geography, colours, names, assets or fixture data enters this public repository.

## Contract

All additions are opt-in. A caller that supplies none of them gets byte-identical existing imagery.
No GPU pipeline, shader, package dependency or source coordinate contract changes.

### Map-only ground palette

Retain the existing three-parameter `TileWorldMapSource` constructor and its optional surfacePlane.
Add a four-parameter overload with explicit `int surfacePlane` and
`IReadOnlyDictionary<ushort, Color>? groundPalette`. The existing overload delegates with null.
Snapshot the supplied mapping at construction so worker
reads cannot race caller mutation. Both underlay and overlay selection retain their existing
semantics. A missing palette entry falls back to the catalog colour. Water detection, heights,
material roles, footprints and the source catalogs remain unchanged.

This is adapter configuration. The generic raster still consumes `WorldMapSurfaceGrid.Ground`.

### Elevation contours

Add `WorldMapContours(float IntervalMetres, Color Color, float WidthPixels = 0.75f)` and nullable
`WorldMapStyle.Contours`. Interval and width must be finite and positive. Colours follow existing
style colour validation. No arbitrary game-specific interval or palette is an engine default.

Contour lines follow multiples of IntervalMetres on the sampled ground height. Draw them on dry
ground after relief and before footprints. Use actual height units, not quantized source storage.
Do not contour water, absent ground or a flat plateau that happens to equal an interval. Derive
coverage in raster pixels with antialiasing. Interpolation and sampling must give identical
border pixels when a continuous fixture crosses positive or negative chunk coordinates.
Raster level changes keep the configured width in pixels. Null preserves the old path exactly.

Keep the implementation in a cohesive contour raster type. Prefer bounded per-pixel sampling over
whole-world contour extraction. Contour sampling reach participates in cache invalidation margins.

### Vector footprint symbols

Add immutable `WorldMapSymbolStroke(Vector2 Start, Vector2 End)` and
`WorldMapSymbolGeometry(IEnumerable<Vector2> outline, IEnumerable<WorldMapSymbolStroke>? strokes = null)`.
Expose read-only `Outline` and `Strokes`. Snapshot inputs and validate finite normalized coordinates
in [-1, 1], at least three nondegenerate outline vertices and a simple closed outline. Open strokes
are independent segments and may lie outside a concave fill, but remain inside the normalized box.
This permits a trunk below a canopy. The last outline point connects to the first without a repeated endpoint.

Add optional `WorldMapSymbol.Geometry`. Null retains the existing rectangle/ellipse shape.
Custom outlines may be concave. Rotate and scale them through the existing footprint and
SizeMetres rules. Polygon fills use Fill, its boundary and internal strokes use Outline. Outline
and detail widths use the existing OutlinePixels, measured in raster pixels even for non-square
symbols. Internal strokes are rendered over fill without double-darkening shared coverage.
An opaque white fill with a half-alpha black internal stroke yields RGB 128, independent of the
terrain underneath. A stroke outside the polygon blends over terrain. Preserve the legacy boundary
fill/outline composition. The union rule applies to intersecting detail strokes, not removal of fill.
A building shadow uses the filled outline silhouette and does not reproduce its detail lines.

This permits game-authored broadleaf outlines, roof ridges and bridge rails without engine-owned
art styles. Geometry stays inside the normalized footprint, so existing spatial collection and
sizing reach remain authoritative. Antialias through the same fixed sample pattern as current
symbols, with no raw vector paths reaching the GPU.

### Standard offscreen map view

Add `WorldMapView(WorldMapChunkCache cache, Render2DContext context)` beside the existing
surface constructor. The existing constructor and its ownership remain unchanged.
The snapshot context owns every texture it creates until capture submission finishes.
View disposal or cache eviction must therefore release managed view references without disposing
snapshot-owned textures early. The snapshot context frees them at its normal teardown.
Borrowed fonts and icon sprites remain caller-owned. This is the standard headless path for drawing
the same map view and game window code used in the client.

## Alternatives considered

| Approach | Compatibility | Reuse | Bounded work | Total |
| --- | --- | --- | --- | --- |
| Opt-in CPU style and geometry additions | 10 | 9 | 9 | 28 |
| Separate vector map renderer | 5 | 8 | 3 | 16 |
| Consumer raster postprocessing | 5 | 3 | 7 | 15 |

The first approach preserves the existing worker/cache/GUI contract and supports other game styles.
It adds CPU shape work only where explicitly selected. A second renderer duplicates lifecycle and
invalidation. Consumer postprocessing cannot correctly derive semantic material and footprint styling.

## Dynamic source contract

Continue consuming the real source. `Changed` invalidates the cache by area and generation.
Revision alone is not an invalidation event. The static TileWorld adapter keeps its documented
immutable-document lifecycle. These rendering options do not pretend to make a static source live.
Tests cover the existing change notification path with contours and custom geometry enabled.

## Verification and release

Use synthetic headless fixtures for palette, contours, geometry, source invalidation, chunk seams,
levels, rotation, non-square sizing, transparency and default-byte compatibility. No private
consumer assets enter fixtures. Run focused area tests per task and one full Release build/suite
at integration. Run whole-tree governance and doc-version checks.

Update living package documentation and the consumer guide. Stage one additive minor version if
there is no already staged release. Do not create a tag. A waiting consumer adopts only after the
owner-approved release handoff completes.
