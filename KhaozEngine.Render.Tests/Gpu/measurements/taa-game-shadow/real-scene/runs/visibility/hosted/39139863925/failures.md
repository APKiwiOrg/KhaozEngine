# Hosted visibility failures

Run 39139863925, source `50c8c6f3657e23df28fe33fffea845691169f0e1`.

Bounds use the precision printed by each run. Assertions compare unrounded measurements.
Bilinear pinned-reference movement is separated from temporal regressions.

## direct3d11-native

{'Total tests': 1360, 'Passed': 1352, 'Failed': 8, 'Skipped': None}

Visibility archive token shadow-bilinear-3x3,vis-release-0.1, startup mask 20971520, MRT enabled True

### KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests.AStillFrameCountsAlmostNothing

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

### KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests.ARequestIsReadBackOnceAtTheNextPrepareFrame

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

### KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests.TheDisoccludedCountIsTheResolvesOwnDecisionAtEverySample

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

### KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests.ARevealedBackgroundIsCountedAsDisoccluded

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

### KhaozEngine.Tests.Gpu.TemporalCountProbeGpuTests.ASharpChangeOfColourIsCountedAsClipped

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010531 above v21.2.0 0.009361 plus a tenth | 0.010531000 | 0.010297650 | acceptance failure |
| Quality M 0.5 trailing textured: reference changed from 0.018941 to 0.018955 | 0.000014000 | 0.000001000 | expected bilinear reference movement |
| Quality M 1.0 trailing textured: temporal error 0.009788 above v21.2.0 0.008346 plus a tenth | 0.009788000 | 0.009181150 | acceptance failure |
| Quality M 1.0 trailing textured: reference changed from 0.035613 to 0.035635 | 0.000022000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 trailing textured: reference changed from 0.056161 to 0.056221 | 0.000060000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 trailing textured: reference changed from 0.066151 to 0.066177 | 0.000026000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 trailing textured: reference changed from 0.063139 to 0.063187 | 0.000048000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 leading flat: reference changed from 0.008146 to 0.008074 | 0.000072000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 trailing textured: reference changed from 0.038602 to 0.038644 | 0.000042000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 leading flat: reference changed from 0.013942 to 0.014041 | 0.000099000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 trailing textured: reference changed from 0.058590 to 0.058529 | 0.000061000 | 0.000001000 | expected bilinear reference movement |
| Native M 0.5 trailing textured: reference changed from 0.018941 to 0.018955 | 0.000014000 | 0.000001000 | expected bilinear reference movement |
| Native M 1.0 trailing textured: reference changed from 0.035613 to 0.035635 | 0.000022000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 trailing textured: reference changed from 0.056161 to 0.056221 | 0.000060000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 trailing textured: reference changed from 0.066151 to 0.066177 | 0.000026000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 trailing textured: reference changed from 0.063139 to 0.063187 | 0.000048000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 leading flat: reference changed from 0.008146 to 0.008074 | 0.000072000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 trailing textured: reference changed from 0.038602 to 0.038644 | 0.000042000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 leading flat: reference changed from 0.013942 to 0.014041 | 0.000099000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 trailing textured: reference changed from 0.058590 to 0.058529 | 0.000061000 | 0.000001000 | expected bilinear reference movement |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004592178 above 1.5 times the 1 px tail 0.002835155 | 0.004592178 | 0.004252733 | acceptance failure |
| Native S 8.0 px retreating: textured tail 0.008249826 above 1.5 times the 1 px tail 0.005451895 | 0.008249826 | 0.008177843 | acceptance failure |

### KhaozEngine.Tests.Gpu.TemporalCountProbeAllocationGpuTests.A_steady_frame_without_a_request_allocates_nothing_and_reads_nothing_back

D3D11 shader validation blocks this fact, baseline status unmeasured

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| KhaozEngine.Gpu.ShaderValidationException : d3d11 program adc057aa1373 [fragment]: FXC rejected the cross-compiled HLSL for profile ps_5_0 (HRESULT 0x80004005). This is HLSL that SPIRV-Cross emitted, so the line numbers below are the EMITTED source, not the GLSL. FXC said: KhaozEngine.Render.Tests\bin\Release\net10.0\d3d11 program adc057aa1373 [fragment](82,58-63): error X4555: cannot use casts on l-values | see message | not a numeric measurement | D3D11 shader validation blocks this fact, baseline status unmeasured |

## metal-native

{'Total tests': 1360, 'Passed': 1354, 'Failed': 6, 'Skipped': None}

Visibility archive token shadow-bilinear-3x3,vis-release-0.1, startup mask 20971520, MRT enabled True

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010466 above v21.2.0 0.009307 plus a tenth | 0.010466000 | 0.010238250 | acceptance failure |
| Quality M 0.5 trailing textured: reference changed from 0.018943 to 0.018951 | 0.000008000 | 0.000001000 | expected bilinear reference movement |
| Quality M 1.0 trailing textured: temporal error 0.010086 above v21.2.0 0.008060 plus a tenth | 0.010086000 | 0.008866550 | acceptance failure |
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

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004590698 above 1.5 times the 1 px tail 0.002863098 | 0.004590698 | 0.004294647 | acceptance failure |
| Native S 8.0 px retreating: textured tail 0.008260028 above 1.5 times the 1 px tail 0.005470012 | 0.008260028 | 0.008205018 | acceptance failure |

## vulkan-native

{'Total tests': 1360, 'Passed': 1358, 'Failed': 2, 'Skipped': None}

Visibility archive token shadow-bilinear-3x3,vis-release-0.1, startup mask 20971520, MRT enabled True

### KhaozEngine.Tests.Gpu.TemporalMovingShadeLagGpuTests.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Native S 8.0 px advancing: textured tail 0.004587868 above 1.5 times the 1 px tail 0.002917836 | 0.004587868 | 0.004376754 | acceptance failure |

### KhaozEngine.Tests.Gpu.TemporalMovingShadeRimGpuTests.Moving_shadow_receivers_keep_their_leading_and_trailing_rims

acceptance failure

| Metric or exception | Value | Bound | Classification |
| --- | ---: | ---: | --- |
| Quality M 0.5 leading flat: error 0.010496 above v21.2.0 0.009379 plus a tenth | 0.010496000 | 0.010317450 | acceptance failure |
| Quality M 0.5 trailing textured: reference changed from 0.018923 to 0.018925 | 0.000002000 | 0.000001000 | expected bilinear reference movement |
| Quality M 1.0 trailing textured: temporal error 0.009544 above v21.2.0 0.008078 plus a tenth | 0.009544000 | 0.008886350 | acceptance failure |
| Quality M 1.0 trailing textured: reference changed from 0.035582 to 0.035597 | 0.000015000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Quality M 2.0 trailing textured: reference changed from 0.056094 to 0.056137 | 0.000043000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Quality M 4.0 trailing textured: reference changed from 0.066060 to 0.066091 | 0.000031000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Quality M 8.0 trailing textured: reference changed from 0.063028 to 0.063070 | 0.000042000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 leading flat: reference changed from 0.008145 to 0.008075 | 0.000070000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 1.0 trailing textured: reference changed from 0.038604 to 0.038654 | 0.000050000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 leading flat: reference changed from 0.013941 to 0.014041 | 0.000100000 | 0.000001000 | expected bilinear reference movement |
| Quality MS 2.0 trailing textured: reference changed from 0.058566 to 0.058497 | 0.000069000 | 0.000001000 | expected bilinear reference movement |
| Native M 0.5 trailing textured: reference changed from 0.018923 to 0.018925 | 0.000002000 | 0.000001000 | expected bilinear reference movement |
| Native M 1.0 trailing textured: reference changed from 0.035582 to 0.035597 | 0.000015000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 leading flat: reference changed from 0.006865 to 0.006928 | 0.000063000 | 0.000001000 | expected bilinear reference movement |
| Native M 2.0 trailing textured: reference changed from 0.056094 to 0.056137 | 0.000043000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 leading flat: reference changed from 0.031935 to 0.032271 | 0.000336000 | 0.000001000 | expected bilinear reference movement |
| Native M 4.0 trailing textured: reference changed from 0.066060 to 0.066091 | 0.000031000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 leading flat: reference changed from 0.088200 to 0.088301 | 0.000101000 | 0.000001000 | expected bilinear reference movement |
| Native M 8.0 trailing textured: reference changed from 0.063028 to 0.063070 | 0.000042000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 leading flat: reference changed from 0.008145 to 0.008075 | 0.000070000 | 0.000001000 | expected bilinear reference movement |
| Native MS 1.0 trailing textured: reference changed from 0.038604 to 0.038654 | 0.000050000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 leading flat: reference changed from 0.013941 to 0.014041 | 0.000100000 | 0.000001000 | expected bilinear reference movement |
| Native MS 2.0 trailing textured: reference changed from 0.058566 to 0.058497 | 0.000069000 | 0.000001000 | expected bilinear reference movement |
