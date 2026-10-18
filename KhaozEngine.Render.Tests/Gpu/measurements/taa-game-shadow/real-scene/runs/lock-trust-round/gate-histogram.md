## back15 taa-native
band pixels 39873, dots 1739, pixels differing from noop outside a code 0, noop band pixels already equal to a code colour 0

### show-gate

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| -2 | unlocked | 13825 | 34.7% | 54 | 3.1% |
| -1 | locked and released this frame | 2460 | 6.2% | 21 | 1.2% |
| 1 | upscale gate (Jitter.z >= ShadingMaxUpscale) | 0 | 0.0% | 0 | 0.0% |
| 2 | ridge or box-width gate | 9374 | 23.5% | 695 | 40.0% |
| 3 | history within reset extents of box centre | 13339 | 33.5% | 447 | 25.7% |
| 4 | upscaled, own shift under extents | 0 | 0.0% | 0 | 0.0% |
| 5 | ring threshold | 812 | 2.0% | 490 | 28.2% |
| 6 | opposite-pair requirement | 63 | 0.2% | 32 | 1.8% |
| 7 | no history | 0 | 0.0% | 0 | 0.0% |

### show-gate-extent

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| 0 | not painted | 25659 | 64.4% | 770 | 44.3% |
| 1 | box > 3 ridgeSteps, fires at ridgeStep | 809 | 2.0% | 147 | 8.5% |
| 2 | box <= ridgeStep | 3542 | 8.9% | 89 | 5.1% |
| 3 | box 1-3 ridgeSteps, still blocked at ridgeStep | 5835 | 14.6% | 327 | 18.8% |
| 4 | box 1-3 ridgeSteps, fires at ridgeStep | 1480 | 3.7% | 244 | 14.0% |
| 5 | box > 3 ridgeSteps, still blocked at ridgeStep | 2548 | 6.4% | 162 | 9.3% |

### show-lock-age

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| 0 | not painted | 13825 | 34.7% | 54 | 3.1% |
| 1 | ridge this frame | 2124 | 5.3% | 201 | 11.6% |
| 2 | carried, age 1 | 3714 | 9.3% | 428 | 24.6% |
| 3 | carried, age 2 to 3 | 8031 | 20.1% | 674 | 38.8% |
| 4 | carried, age 4+ | 12179 | 30.5% | 382 | 22.0% |

### dots, gate by lock age

| Gate | ridge this frame | carried, age 1 | carried, age 2 to 3 | carried, age 4+ |
| --- | ---: | ---: | ---: | ---: |
| unlocked | 0 | 0 | 0 | 0 |
| released | 20 | 0 | 0 | 1 |
| ridge or box-width gate | 0 | 143 | 359 | 193 |
| history within reset extents of box centre | 181 | 129 | 74 | 63 |
| ring threshold | 0 | 138 | 229 | 123 |
| opposite-pair requirement | 0 | 18 | 12 | 2 |

## back15 taa-quality
band pixels 39873, dots 1733, pixels differing from noop outside a code 0, noop band pixels already equal to a code colour 0

### show-gate

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| -2 | unlocked | 10894 | 27.3% | 55 | 3.2% |
| -1 | locked and released this frame | 2526 | 6.3% | 5 | 0.3% |
| 1 | upscale gate (Jitter.z >= ShadingMaxUpscale) | 0 | 0.0% | 0 | 0.0% |
| 2 | ridge or box-width gate | 6481 | 16.3% | 278 | 16.0% |
| 3 | history within reset extents of box centre | 18417 | 46.2% | 670 | 38.7% |
| 4 | upscaled, own shift under extents | 795 | 2.0% | 228 | 13.2% |
| 5 | ring threshold | 701 | 1.8% | 463 | 26.7% |
| 6 | opposite-pair requirement | 59 | 0.1% | 34 | 2.0% |
| 7 | no history | 0 | 0.0% | 0 | 0.0% |

### show-gate-extent

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| 0 | not painted | 19901 | 49.9% | 338 | 19.5% |
| 1 | box > 3 ridgeSteps, fires at ridgeStep | 1059 | 2.7% | 239 | 13.8% |
| 2 | box <= ridgeStep | 4012 | 10.1% | 36 | 2.1% |
| 3 | box 1-3 ridgeSteps, still blocked at ridgeStep | 8614 | 21.6% | 405 | 23.4% |
| 4 | box 1-3 ridgeSteps, fires at ridgeStep | 1934 | 4.9% | 413 | 23.8% |
| 5 | box > 3 ridgeSteps, still blocked at ridgeStep | 4353 | 10.9% | 302 | 17.4% |

### show-lock-age

| Code | Meaning | Band pixels | Band share | Dots | Dot share |
| ---: | --- | ---: | ---: | ---: | ---: |
| 0 | not painted | 10894 | 27.3% | 55 | 3.2% |
| 1 | ridge this frame | 4285 | 10.7% | 365 | 21.1% |
| 2 | carried, age 1 | 4978 | 12.5% | 469 | 27.1% |
| 3 | carried, age 2 to 3 | 8594 | 21.6% | 564 | 32.5% |
| 4 | carried, age 4+ | 11122 | 27.9% | 280 | 16.2% |

### dots, gate by lock age

| Gate | ridge this frame | carried, age 1 | carried, age 2 to 3 | carried, age 4+ |
| --- | ---: | ---: | ---: | ---: |
| unlocked | 0 | 0 | 0 | 0 |
| released | 5 | 0 | 0 | 0 |
| ridge or box-width gate | 0 | 67 | 157 | 54 |
| history within reset extents of box centre | 243 | 189 | 133 | 105 |
| upscaled, own shift under extents | 113 | 48 | 39 | 28 |
| ring threshold | 4 | 144 | 225 | 90 |
| opposite-pair requirement | 0 | 21 | 10 | 3 |

