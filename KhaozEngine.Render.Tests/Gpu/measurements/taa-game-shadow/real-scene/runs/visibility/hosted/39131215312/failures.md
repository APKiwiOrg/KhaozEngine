# Hosted visibility failures

Run 39131215312, source `e62236e561120669f6ca6be4c237c8cb46d50554`.

Bounds use the precision printed by each run. Assertions compare unrounded measurements.
Bilinear pinned-reference movement is separated from temporal regressions.

## direct3d11-native

{'Total tests': 'Unknown', 'Passed': 893, 'Failed': 312, 'Skipped': None}

Visibility archive token vis-release-0.1, startup mask 16777216, MRT enabled True

312 failing test records from the aborted leg. Every name, exception and message is in [results.json](results.json). These constructor failures have no numeric visibility bound.

## metal-native

{'Total tests': 1360, 'Passed': 1350, 'Failed': 10, 'Skipped': None}

Visibility archive token vis-release-0.1, startup mask 16777216, MRT enabled True

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_by_the_followed_surface_reads_back_unchanged_where_it_stopped

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.67578125 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.675781250 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_where_a_moving_surface_showed_reads_back_unchanged

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.6748046875 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.674804688 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_by_a_band_pixel_reads_back_unchanged_where_its_history_is_kept

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.67578125 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.675781250 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality C pan 2.0 px: textured band 0.0071 above 0.0055 plus a tenth | 0.007100000 | 0.006050000 | acceptance failure |
| Native S 8.0 px advancing: textured tail 0.0046 above 1.5 times the 1 px tail 0.0030 | 0.004600000 | 0.004500000 | acceptance failure |
| Native S 8.0 px retreating: textured tail 0.0082 above 1.5 times the 1 px tail 0.0054 | 0.008200000 | 0.008100000 | acceptance failure |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

entry identity, original noop control fails this class locally

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | 0.010253906 | 0.000000000 | entry identity, original noop control fails this class locally |
| confidence: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | 0.000244141 | 0.000000000 | entry identity, original noop control fails this class locally |
| mark: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

entry identity, original noop control fails this class locally

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| colour: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| confidence: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | 0.000488281 | 0.000000000 | entry identity, original noop control fails this class locally |
| mark: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

entry identity, original noop control fails this class locally

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| colour: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| confidence: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | 0.000488281 | 0.000000000 | entry identity, original noop control fails this class locally |
| mark: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

entry identity, original noop control fails this class locally

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| colour: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| confidence: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |
| lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | 0.000488281 | 0.000000000 | entry identity, original noop control fails this class locally |
| mark: none differ | see message | not a numeric measurement | entry identity, original noop control fails this class locally |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.010898 above v21.2.0 0.008060 plus a tenth | 0.010898000 | 0.008866550 | acceptance failure |

### KhaozEngine.Tests.Render3D.PointShadowAllocationTests.PreparePointShadowsOnASteadyFrameAllocatesNothing

invalid allocation measurement, infrastructure or runtime

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| PreparePointShadows over 20 steady frames: invalid allocation measurement because the no-GC region ended before the window completed. Budget=4194304 bytes, process=0 bytes, thread=0 bytes, other threads=0 bytes, test messages reported in the window=0, collections(gen0/gen1/gen2)=0/0/0. Runtime reason: NoGCRegion mode must be set. | see message | not a numeric measurement | invalid allocation measurement, infrastructure or runtime |

## vulkan-native

{'Total tests': 1360, 'Passed': 1355, 'Failed': 5, 'Skipped': None}

Visibility archive token vis-release-0.1, startup mask 16777216, MRT enabled True

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality C pan 2.0 px: textured band 0.0072 above 0.0056 plus a tenth | 0.007200000 | 0.006160000 | acceptance failure |
| Native S 8.0 px advancing: textured tail 0.0046 above 1.5 times the 1 px tail 0.0031 | 0.004600000 | 0.004650000 | acceptance failure |
| Native S 8.0 px retreating: textured tail 0.0082 above 1.5 times the 1 px tail 0.0054 | 0.008200000 | 0.008100000 | acceptance failure |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.010485 above v21.2.0 0.008078 plus a tenth | 0.010485000 | 0.008886350 | acceptance failure |

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_by_the_followed_surface_reads_back_unchanged_where_it_stopped

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.67578125 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.675781250 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_where_a_moving_surface_showed_reads_back_unchanged

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.6748046875 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.674804688 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |

### KhaozEngine.Tests.Gpu.TemporalStoredLockGpuTests.A_lock_stored_by_a_band_pixel_reads_back_unchanged_where_its_history_is_kept

seeded fully lit history initialization bug, fixed in dab3a8981

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values are not within tolerance 0.0001 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Expected: 0.67578125 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| Actual:   0 | see message | not a numeric measurement | seeded fully lit history initialization bug, fixed in dab3a8981 |
| absolute seeded-lock error | 0.675781250 | 0.000100000 | seeded fully lit history initialization bug, fixed in dab3a8981 |
