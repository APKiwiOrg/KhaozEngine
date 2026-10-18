# Full local guard failures

One process per token, 1360 tests and 48 temporal correctness classes each, zero skipped.
The two timing benchmark classes are excluded under the no-load rule.

## unset

1360 passed, 0 failed.

## shadow-bilinear-3x3

1359 passed, 1 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010485 above v21.2.0 0.009307 plus a tenth | 0.010485000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

## shadow-bilinear-3x3,vis-release-0.1

1354 passed, 6 failed.

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

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004590698 above 1.5 times the 1 px tail 0.002863098 | 0.004590698 | approx 0.004294647 | acceptance regression |
| Native S 8.0 px retreating: textured tail 0.008260028 above 1.5 times the 1 px tail 0.005470012 | 0.008260028 | approx 0.008205018 | acceptance regression |

## vis-mean-0.1

1354 passed, 6 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004627341 above 1.5 times the 1 px tail 0.002999368 | 0.004627341 | approx 0.004499052 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

## vis-mean-0.15

1354 passed, 6 failed.

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
| Native S 8.0 px advancing: textured tail 0.004661668 above 1.5 times the 1 px tail 0.003047064 | 0.004661668 | approx 0.004570596 | acceptance regression |

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
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

## vis-mean-0.2

1354 passed, 6 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004694436 above 1.5 times the 1 px tail 0.003089056 | 0.004694436 | approx 0.004633584 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
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

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

## vis-mean-dq-0.1

1354 passed, 6 failed.

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
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
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

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_with_the_edge_outline_on

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: none differ | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 15 values differ on 8 frames from frame 22, the largest by 0.00048828125 (4 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004627341 above 1.5 times the 1 px tail 0.002999069 | 0.004627341 | approx 0.004498604 | acceptance regression |

## vis-centre-dq-0.1

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

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004654312 above 1.5 times the 1 px tail 0.003006249 | 0.004654312 | approx 0.004509374 | acceptance regression |

### KhaozEngine.Tests.Gpu.TemporalEntryIdentityGpuTests.Both_entry_points_write_the_same_history_on_every_frame(preset: Performance)

Inherited original archive noop identity.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| the entry points stored different state: | see message | exact identity, zero differences | inherited noop identity |
|   colour: 596 values differ on 17 frames from frame 31, the largest by 0.010253906 (21 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   confidence: none differ | see message | exact identity, zero differences | inherited noop identity |
|   lock: 8 values differ on 3 frames from frame 27, the largest by 0.00024414062 (2 half-float steps) | see message | exact identity, zero differences | inherited noop identity |
|   mark: none differ | see message | exact identity, zero differences | inherited noop identity |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
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

## vis-mean-dq-large-0.1

1355 passed, 5 failed.

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

Acceptance failure.

| Metric | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010435 above v21.2.0 0.009307 plus a tenth | 0.010435000 | 0.010238250 | acceptance regression |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
| Quality M 0.5 trailing flat: temporal error 0.006479 above v21.2.0 0.005763 plus a tenth | 0.006479000 | 0.006339850 | acceptance regression |
| Quality M 1.0 trailing textured: reference changed from 0.035586 to 0.035615 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Quality M 1.0 trailing flat: error 0.000273 above v21.2.0 0.000223 plus a tenth | 0.000273000 | 0.000245850 | acceptance regression |
| Quality M 1.0 trailing flat: temporal error 0.000297 above v21.2.0 0.000223 plus a tenth | 0.000297000 | 0.000245850 | acceptance regression |
| Quality M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 trailing textured: reference changed from 0.056131 to 0.056194 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 trailing textured: reference changed from 0.066148 to 0.066177 | 0.000029000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 trailing textured: reference changed from 0.063109 to 0.063160 | 0.000051000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 leading flat: reference changed from 0.008146 to 0.008074 | 0.000072000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 trailing textured: reference changed from 0.038593 to 0.038637 | 0.000044000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 trailing flat: error 0.000378 above v21.2.0 0.000292 plus a tenth | 0.000378000 | 0.000321750 | acceptance regression |
| Quality MS 1.0 trailing flat: temporal error 0.000488 above v21.2.0 0.000382 plus a tenth | 0.000488000 | 0.000420750 | acceptance regression |
| Quality MS 2.0 leading flat: reference changed from 0.013942 to 0.014041 | 0.000099000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 trailing textured: reference changed from 0.058579 to 0.058518 | 0.000061000 | 0.000001000 | expected bilinear reference movement |
| Native M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
| Native M 0.5 trailing flat: error 0.001018 above v21.2.0 0.000904 plus a tenth | 0.001018000 | 0.000994950 | acceptance regression |
| Native M 0.5 trailing flat: temporal error 0.001113 above v21.2.0 0.000991 plus a tenth | 0.001113000 | 0.001090650 | acceptance regression |
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

Final lag failure messages use nine decimal places and rim messages use six. Calculated bounds retain that rounding. Assertions compare the unrounded measurements.
