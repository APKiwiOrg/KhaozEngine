# Single WARP probe and offline findings

Run [37482238342](https://github.com/APKiwiOrg/KhaozEngine/actions/runs/37482238342), job
112333092389, attempt 1, failed the original 8 mm assertion. Exactly one test executed, with one
failure and zero skips. Code is be72718673d79d83d34d3467a262e95bbe845151, isolated proof is
8632b57391f1a0cf6d67bf77430c578f8d4fba50. Never integrate the proof workflow commit.

The diagnostic artifact is complete. The final workflow gate deliberately fails when the assertion
fails. The probe step's API conclusion is success because it has continue-on-error, so TRX is the
assertion authority. This was not an infrastructure timeout or an artifact-writing failure.

Artifact 11421552678 has archive SHA-256
ef5ce8d9e72cf5f0d2638aedb0537e48dc80094b95ac73c5313b04d58a4743b5. Independent checks validated
the exact proof/tree identity, assembly revision agreement, healthy native D3D11 software device,
13 by 61 populated probes, 13 residuals and both 147,456-byte capture hashes. All 793 recorded
channel pairs match their capture bytes, and every stored ratio matches float32 division.

The adjacent hosted.json and offline.json files retain identities, hashes and numerical results.
The adjacent offline.py script accepts an extracted artifact directory and an output JSON path.
It uses only the Python standard library, reads existing pixels and never renders or runs tests.

## Results

| Calculation on the retained capture | Crossing bump in mm | Largest outside residual in mm |
| --- | ---: | ---: |
| Original float32 pipeline, bit-for-bit replay | -8.728723 | 8.491099 |
| Same texels with double-precision arithmetic | -8.728821 | 8.491008 |
| Same texels with trapezoidal endpoint weights | -8.728821 | 8.491008 |
| Bilinear sampling about pixel centres | -6.291010 | 8.781988 |
| Screen X shifted -0.25 pixel, discrete reads | -6.200081 | 17.527858 |
| Screen X shifted +0.25 pixel, discrete reads | -5.580898 | 8.065486 |
| Screen Y shifted -0.25 pixel, discrete reads | -10.497865 | 9.585350 |
| Screen Y shifted +0.25 pixel, discrete reads | -7.110269 | 12.104093 |

Every original station reads 61 distinct texels. Its endpoints are exactly lit=1 and shadowed=0.
The endpoint correction removes the same 15 mm offset from each reach value, so detrending cancels
it. It does not explain the crossing bump. Reprojection using the stored matrix has no integer pixel
index mismatch and a maximum double-reference discrepancy below 0.000019 pixel. Float accumulation
differences are also much smaller than the 0.7287 mm assertion overrun.

Bilinear sampling uses pixel centres at n+0.5 and interpolates the plain and shadow captures before
forming their ratio. The quarter-pixel variants change sampling phase only. They are sensitivity
calculations from the same captured images, not new captures, tests or proposed acceptance criteria.

## Disposition

The known WARP signature is reproduced with complete evidence. Sampling choice moves this statistic
by more than its failure margin. That supports investigating the measurement's sensitivity, but
does not establish that sampling is the only cause. Bilinear smoothing can hide a real discontinuity.
No clamped-face negative control, atlas readback or per-tap capture exists in this artifact.

Do not widen the bound, replace the assertion, call the failure intermittent or exonerate the filter.
The historical rounded vectors still do not establish historical pixel byte equality. No further
GPU run, matrix, retry or stress was performed. Catalog C remains unresolved.

Recommended next step is a written diagnostic design that compares a deliberately clamped-face
negative control with the current filter under a sampling method demonstrably able to detect that
control. Any code preparation and later finite GPU run need their own approved scope. Keep this
original assertion and all captured evidence as the baseline.
