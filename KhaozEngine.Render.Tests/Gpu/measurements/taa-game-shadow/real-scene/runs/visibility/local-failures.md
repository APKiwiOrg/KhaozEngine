# Full local guard failures

One process per token, 1360 tests and 48 temporal correctness classes each, zero skipped.
The two timing benchmark classes are excluded under the no-load rule.

## noop

1356 passed, 4 failed.

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

## show-vis-release

1355 passed, 5 failed.

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalDebugViewAllocationGpuTests.A_steady_resolving_frame_allocates_nothing_whatever_view_is_selected(view: None)

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Assert.Equal() Failure: Values differ | see message | exact identity, zero differences | explicit overlay builds the debug pass, control-only structural mismatch |
| Expected: False | see message | exact identity, zero differences | explicit overlay builds the debug pass, control-only structural mismatch |
| Actual:   True | see message | exact identity, zero differences | explicit overlay builds the debug pass, control-only structural mismatch |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

## vis-release-0.1

1354 passed, 6 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.010898 above v21.2.0 0.008060 plus a tenth | 0.010898000 | 0.008866550 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality C pan 2.0 px: textured band 0.007134914 above 0.005500000 plus a tenth | 0.007134914 | 0.006050000 | acceptance regression |
| Native S 8.0 px advancing: textured tail 0.004641015 above 1.5 times the 1 px tail 0.003020217 | 0.004641015 | approx 0.004530325 | acceptance regression |
| Native S 8.0 px retreating: textured tail 0.008185859 above 1.5 times the 1 px tail 0.005379213 | 0.008185859 | approx 0.008068819 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

## vis-release-0.2

1354 passed, 6 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.010292 above v21.2.0 0.008060 plus a tenth | 0.010292000 | 0.008866550 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality C pan 2.0 px: textured band 0.006256783 above 0.005500000 plus a tenth | 0.006256783 | 0.006050000 | acceptance regression |
| Native S 8.0 px advancing: textured tail 0.004648908 above 1.5 times the 1 px tail 0.003075458 | 0.004648908 | approx 0.004613187 | acceptance regression |
| Native S 8.0 px retreating: textured tail 0.008191692 above 1.5 times the 1 px tail 0.005423310 | 0.008191692 | approx 0.008134965 | acceptance regression |

## vis-release-0.3

1355 passed, 5 failed.

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.009746 above v21.2.0 0.008060 plus a tenth | 0.009746000 | 0.008866550 | acceptance regression |

## vis-reset-0.2

1353 passed, 7 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality C pan 2.0 px: textured band 0.007309765 above 0.005500000 plus a tenth | 0.007309765 | 0.006050000 | acceptance regression |
| Native C pan 1.0 px: textured band 0.005762113 above 0.004600000 plus a tenth | 0.005762113 | 0.005060000 | acceptance regression |
| Native C pan 2.0 px: textured band 0.005724477 above 0.004600000 plus a tenth | 0.005724477 | 0.005060000 | acceptance regression |
| Native C pan 8.0 px: textured band 0.006283297 above 0.005500000 plus a tenth | 0.006283297 | 0.006050000 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 1.0 trailing textured: temporal error 0.010415 above v21.2.0 0.008060 plus a tenth | 0.010415000 | 0.008866550 | acceptance regression |
| Quality M 1.0 trailing flat: error 0.000453 above v21.2.0 0.000223 plus a tenth | 0.000453000 | 0.000245850 | acceptance regression |
| Quality M 1.0 trailing flat: temporal error 0.000517 above v21.2.0 0.000223 plus a tenth | 0.000517000 | 0.000245850 | acceptance regression |
| Quality M 4.0 trailing flat: error 0.000453 above v21.2.0 0.000272 plus a tenth | 0.000453000 | 0.000299750 | acceptance regression |
| Quality M 4.0 trailing flat: temporal error 0.000517 above v21.2.0 0.000295 plus a tenth | 0.000517000 | 0.000325050 | acceptance regression |
| Quality MS 1.0 trailing flat: error 0.000461 above v21.2.0 0.000292 plus a tenth | 0.000461000 | 0.000321750 | acceptance regression |
| Quality MS 1.0 trailing flat: temporal error 0.000527 above v21.2.0 0.000382 plus a tenth | 0.000527000 | 0.000420750 | acceptance regression |
| Native M 0.5 trailing flat: error 0.002050 above v21.2.0 0.000904 plus a tenth | 0.002050000 | 0.000994950 | acceptance regression |
| Native M 0.5 trailing flat: temporal error 0.002263 above v21.2.0 0.000991 plus a tenth | 0.002263000 | 0.001090650 | acceptance regression |
| Native M 1.0 trailing flat: error 0.000161 above v21.2.0 0.000057 plus a tenth | 0.000161000 | 0.000063250 | acceptance regression |
| Native M 1.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000057 plus a tenth | 0.000152000 | 0.000063250 | acceptance regression |
| Native M 2.0 trailing flat: error 0.000161 above v21.2.0 0.000057 plus a tenth | 0.000161000 | 0.000063250 | acceptance regression |
| Native M 2.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000057 plus a tenth | 0.000152000 | 0.000063250 | acceptance regression |
| Native M 4.0 trailing flat: error 0.000161 above v21.2.0 0.000035 plus a tenth | 0.000161000 | 0.000039050 | acceptance regression |
| Native M 4.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000032 plus a tenth | 0.000152000 | 0.000035750 | acceptance regression |
| Native M 8.0 trailing flat: error 0.000161 above v21.2.0 0.000077 plus a tenth | 0.000161000 | 0.000085250 | acceptance regression |
| Native M 8.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000057 plus a tenth | 0.000152000 | 0.000063250 | acceptance regression |
| Native MS 1.0 trailing flat: error 0.000161 above v21.2.0 0.000140 plus a tenth | 0.000161000 | 0.000154550 | acceptance regression |
| Native MS 1.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000128 plus a tenth | 0.000152000 | 0.000141350 | acceptance regression |
| Native MS 2.0 trailing flat: error 0.000161 above v21.2.0 0.000140 plus a tenth | 0.000161000 | 0.000154550 | acceptance regression |
| Native MS 2.0 trailing flat: temporal error 0.000152 above v21.2.0 0.000128 plus a tenth | 0.000152000 | 0.000141350 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalShadowEdgeLagGpuTests.Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality still: textured band 0.006988889 above 0.006380000 | 0.006988889 | 0.006380000 | acceptance regression |
| Native still: textured band 0.005660087 above 0.004840000 | 0.005660087 | 0.004840000 | acceptance regression |

## shadow-bilinear-3x3,vis-release-0.1

1354 passed, 6 failed.

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
| Quality M 1.0 trailing textured: temporal error 0.010086 above v21.2.0 0.008060 plus a tenth | 0.010086000 | 0.008866550 | acceptance regression |
| Quality M 1.0 trailing textured: reference changed from 0.035586 to 0.035615 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 trailing textured: reference changed from 0.056131 to 0.056194 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 trailing textured: reference changed from 0.066148 to 0.066177 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 trailing textured: reference changed from 0.063109 to 0.063160 | 0.000051000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 leading flat: reference changed from 0.008146 to 0.008074 | 0.000072000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 trailing textured: reference changed from 0.038593 to 0.038637 | 0.000044000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 leading flat: reference changed from 0.013942 to 0.014041 | 0.000099000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 trailing textured: reference changed from 0.058579 to 0.058518 | 0.000061000 | 0.000001000 | expected bilinear reference movement |
| Native M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
| Native M 1.0 trailing textured: reference changed from 0.035586 to 0.035615 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 trailing textured: reference changed from 0.056131 to 0.056194 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 trailing textured: reference changed from 0.066148 to 0.066177 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 trailing textured: reference changed from 0.063109 to 0.063160 | 0.000051000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 leading flat: reference changed from 0.008146 to 0.008074 | 0.000072000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 trailing textured: reference changed from 0.038593 to 0.038637 | 0.000044000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 leading flat: reference changed from 0.013942 to 0.014041 | 0.000099000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 trailing textured: reference changed from 0.058579 to 0.058518 | 0.000061000 | 0.000001000 | expected bilinear reference movement |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Quality)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Native)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 34 values differ on 18 frames from frame 17, the largest by 0.00048828125 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004590698 above 1.5 times the 1 px tail 0.002863098 | 0.004590698 | approx 0.004294647 | acceptance regression |
| Native S 8.0 px retreating: textured tail 0.008260028 above 1.5 times the 1 px tail 0.005470012 | 0.008260028 | approx 0.008205018 | acceptance regression |

Final lag failure messages use nine decimal places and rim messages use six. Calculated bounds retain that rounding. Assertions compare the unrounded measurements.
