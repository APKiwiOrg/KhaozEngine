# Archived real-scene reference, not candidate measurements

Source: diagnostic archive c1d32238a. These are the 21.4.0 behavior and the diagnostic kill-0.1 token, not the
production candidates on this branch. All use first measured sun time 0.49777777777777776, 2560x1440 and 16 frames.
Back15 is the back edge at 1/15 s with a five-minute day. Day30 changes that day to thirty minutes. Back60 changes
that frame time to 1/60 s. Ground15 is the ground edge at 1/15 s with a five-minute day. Luma is 0..1.

| Condition | Arm | Mode | Character grain | Change mean | Dots | Ground grain | Change mean | Dots |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| back15 | baseline | taa-native | 0.014287 | 0.017949 | 1742 | 0.006708 | 0.012495 | 485 |
| back15 | baseline | taa-quality | 0.014156 | 0.017574 | 1733 | 0.007330 | 0.012354 | 1881 |
| back15 | lock-luma-kill-0.1 | taa-native | 0.007217 | 0.018895 | 592 | 0.006338 | 0.012507 | 144 |
| back15 | lock-luma-kill-0.1 | taa-quality | 0.008852 | 0.018299 | 805 | 0.006706 | 0.012420 | 1036 |
| back15 | fxaa | fxaa | 0.002961 | 0.019114 | 0 | 0.002130 | 0.013105 | 0 |
| day30 | baseline | taa-native | 0.008413 | 0.003710 | 1097 | 0.005930 | 0.002342 | 310 |
| day30 | baseline | taa-quality | 0.009148 | 0.003660 | 1117 | 0.006355 | 0.002379 | 906 |
| day30 | lock-luma-kill-0.1 | taa-native | 0.006122 | 0.003736 | 512 | 0.005494 | 0.002303 | 49 |
| day30 | lock-luma-kill-0.1 | taa-quality | 0.006733 | 0.003521 | 497 | 0.005632 | 0.002306 | 291 |
| day30 | fxaa | fxaa | 0.002976 | 0.004018 | 0 | 0.002071 | 0.002023 | 0 |
| back60 | baseline | taa-native | 0.009675 | 0.004882 | 1355 | 0.006055 | 0.003282 | 379 |
| back60 | baseline | taa-quality | 0.010472 | 0.004995 | 1535 | 0.006729 | 0.003268 | 1303 |
| back60 | lock-luma-kill-0.1 | taa-native | 0.006755 | 0.004887 | 729 | 0.005594 | 0.003237 | 61 |
| back60 | lock-luma-kill-0.1 | taa-quality | 0.007599 | 0.004874 | 756 | 0.006034 | 0.003197 | 490 |
| back60 | fxaa | fxaa | 0.002955 | 0.004700 | 0 | 0.002090 | 0.003068 | 0 |
| ground15 | baseline | taa-native | 0.002957 | 0.001162 | 371 | 0.005274 | 0.003726 | 512 |
| ground15 | baseline | taa-quality | 0.003024 | 0.001182 | 340 | 0.005695 | 0.003778 | 1305 |
| ground15 | lock-luma-kill-0.1 | taa-native | 0.002926 | 0.001187 | 345 | 0.004760 | 0.003812 | 269 |
| ground15 | lock-luma-kill-0.1 | taa-quality | 0.003004 | 0.001200 | 335 | 0.004922 | 0.003820 | 723 |
| ground15 | fxaa | fxaa | 0.002280 | 0.001351 | 0 | 0.002343 | 0.003339 | 0 |

The candidate real-scene comparison and 4x character crops remain blocked on correctness. The JSON also preserves grain p95.
