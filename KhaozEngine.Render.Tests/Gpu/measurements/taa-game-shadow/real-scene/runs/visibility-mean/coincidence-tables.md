# Dot and release coincidence

Percentages use pooled pixel-frame counts. Dots use each arm's own FXAA.
Bilinear-dot overlap compares the release mask to bilinear-only dots, in screen space.
The last-four column includes the current frame. It does not follow a moving surface.
Unset and bilinear-only have no visibility release, so their release overlap is structurally zero.
The max-of-nine release overlay exists only for back15. Its other release entries are unmeasured.
All measured release overlays are identical to normal output outside the marked pixels.

## back15, character

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 1742 | 65.33 | 96.90 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1733 | 72.68 | 96.94 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 851 | 55.19 | 97.06 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 1166 | 65.92 | 93.74 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 313 | 29.75 | 58.15 | 29.92 | 34.82 | 34.08 | 39.37 | 33.96 | 47.47 |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 602 | 30.93 | 41.36 | 36.87 | 52.33 | 46.23 | 49.49 | 45.11 | 56.69 |
| vis-mean-0.1 | native | 374 | 39.00 | 72.19 | 18.91 | 21.12 | 21.97 | 30.32 | 21.74 | 37.13 |
| vis-mean-0.1 | quality | 778 | 45.86 | 64.01 | 18.85 | 30.21 | 30.62 | 39.11 | 26.93 | 44.43 |
| vis-mean-0.15 | native | 503 | 45.58 | 86.68 | 10.45 | 10.14 | 12.93 | 23.15 | 12.46 | 27.61 |
| vis-mean-0.15 | quality | 1023 | 55.28 | 79.96 | 8.96 | 12.02 | 15.01 | 28.04 | 13.46 | 30.19 |
| vis-mean-0.2 | native | 742 | 52.68 | 95.69 | 2.26 | 0.54 | 2.94 | 8.70 | 2.47 | 8.34 |
| vis-mean-0.2 | quality | 1138 | 63.51 | 92.09 | 1.85 | 2.02 | 3.26 | 7.03 | 1.37 | 6.95 |
| vis-mean-dq-0.1 | native | 369 | 37.81 | 70.73 | 18.97 | 21.68 | 22.09 | 30.79 | 21.86 | 37.60 |
| vis-mean-dq-0.1 | quality | 753 | 44.85 | 62.15 | 18.64 | 31.87 | 31.22 | 39.37 | 27.36 | 45.45 |
| vis-centre-dq-0.1 | native | 352 | 37.56 | 73.01 | 19.89 | 19.03 | 21.50 | 34.20 | 21.74 | 37.13 |
| vis-centre-dq-0.1 | quality | 699 | 44.02 | 57.65 | 20.45 | 35.77 | 32.08 | 43.65 | 29.07 | 46.14 |
| vis-mean-dq-large-0.1 | native | 388 | 37.81 | 70.62 | 18.97 | 21.39 | 22.09 | 30.79 | 21.86 | 37.60 |
| vis-mean-dq-large-0.1 | quality | 713 | 44.85 | 60.45 | 18.64 | 33.24 | 31.22 | 39.37 | 27.36 | 45.45 |

## day30, character

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 1097 | 66.05 | 98.54 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1117 | 72.29 | 99.46 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 506 | 54.73 | 96.64 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 784 | 64.07 | 94.52 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 358 | 31.05 | 53.91 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 363 | 33.32 | 51.24 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 393 | 46.67 | 80.92 | 3.76 | 9.41 | 9.88 | 13.04 | 5.93 | 26.48 |
| vis-mean-0.1 | quality | 602 | 55.68 | 81.06 | 3.91 | 9.97 | 12.24 | 16.33 | 7.65 | 32.65 |
| vis-mean-0.15 | native | 444 | 51.43 | 88.96 | 1.78 | 5.86 | 7.91 | 10.47 | 4.74 | 20.75 |
| vis-mean-0.15 | quality | 739 | 61.59 | 87.69 | 1.41 | 5.28 | 7.14 | 11.48 | 4.85 | 19.64 |
| vis-mean-0.2 | native | 501 | 54.63 | 97.01 | 0.06 | 0.00 | 0.40 | 1.38 | 0.40 | 1.38 |
| vis-mean-0.2 | quality | 778 | 64.05 | 96.02 | 0.01 | 0.00 | 0.00 | 0.26 | 0.00 | 0.00 |
| vis-mean-dq-0.1 | native | 396 | 45.84 | 77.78 | 3.89 | 12.12 | 11.86 | 17.00 | 7.91 | 31.82 |
| vis-mean-dq-0.1 | quality | 598 | 54.43 | 79.43 | 3.77 | 11.04 | 13.27 | 17.73 | 8.67 | 35.33 |
| vis-centre-dq-0.1 | native | 370 | 43.39 | 78.11 | 5.83 | 14.05 | 16.21 | 40.91 | 11.86 | 36.96 |
| vis-centre-dq-0.1 | quality | 530 | 49.71 | 73.58 | 6.79 | 16.98 | 22.07 | 38.90 | 16.84 | 48.85 |
| vis-mean-dq-large-0.1 | native | 399 | 45.84 | 77.44 | 3.89 | 12.28 | 11.86 | 17.00 | 7.91 | 31.82 |
| vis-mean-dq-large-0.1 | quality | 593 | 54.43 | 79.60 | 3.77 | 10.79 | 13.27 | 17.73 | 8.67 | 35.33 |

## back60, character

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 1355 | 62.54 | 98.23 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1535 | 70.67 | 99.74 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 742 | 51.77 | 95.15 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 1065 | 63.18 | 96.53 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 447 | 30.79 | 61.74 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 662 | 33.11 | 60.57 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 583 | 45.16 | 83.36 | 3.69 | 7.03 | 7.95 | 14.56 | 7.28 | 26.01 |
| vis-mean-0.1 | quality | 925 | 55.21 | 87.78 | 3.90 | 7.78 | 12.49 | 17.00 | 9.95 | 34.18 |
| vis-mean-0.15 | native | 746 | 49.98 | 91.29 | 0.83 | 2.41 | 3.10 | 6.06 | 2.96 | 10.92 |
| vis-mean-0.15 | quality | 1049 | 61.41 | 94.57 | 0.84 | 2.10 | 2.82 | 5.07 | 2.44 | 9.95 |
| vis-mean-0.2 | native | 745 | 51.19 | 93.96 | 0.32 | 0.81 | 0.81 | 2.70 | 0.67 | 2.83 |
| vis-mean-0.2 | quality | 1051 | 62.72 | 96.29 | 0.20 | 0.48 | 0.66 | 1.69 | 0.38 | 1.69 |
| vis-mean-dq-0.1 | native | 581 | 43.71 | 81.58 | 3.81 | 8.61 | 9.03 | 15.77 | 8.36 | 28.57 |
| vis-mean-dq-0.1 | quality | 925 | 53.33 | 86.92 | 3.75 | 8.65 | 13.24 | 18.12 | 10.70 | 36.06 |
| vis-centre-dq-0.1 | native | 555 | 40.66 | 83.60 | 6.00 | 7.93 | 14.29 | 34.77 | 13.48 | 33.29 |
| vis-centre-dq-0.1 | quality | 811 | 49.09 | 83.48 | 6.85 | 11.71 | 19.34 | 35.77 | 16.62 | 45.92 |
| vis-mean-dq-large-0.1 | native | 581 | 43.71 | 81.58 | 3.81 | 8.61 | 9.03 | 15.77 | 8.36 | 28.57 |
| vis-mean-dq-large-0.1 | quality | 887 | 53.33 | 86.36 | 3.75 | 8.79 | 13.24 | 18.12 | 10.70 | 36.06 |

## ground15, character

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 371 | 40.72 | 97.30 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 340 | 48.15 | 100.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 371 | 40.65 | 97.30 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 341 | 48.09 | 100.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 360 | 34.29 | 49.72 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 320 | 39.21 | 49.38 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 361 | 39.18 | 73.96 | 0.58 | 21.88 | 22.64 | 40.97 | 20.49 | 42.59 |
| vis-mean-0.1 | quality | 324 | 45.74 | 76.54 | 0.98 | 22.84 | 24.34 | 36.07 | 23.17 | 41.94 |
| vis-mean-0.15 | native | 360 | 40.21 | 90.28 | 0.19 | 7.22 | 7.55 | 28.84 | 7.01 | 23.99 |
| vis-mean-0.15 | quality | 338 | 47.46 | 86.98 | 0.30 | 12.13 | 12.90 | 23.46 | 11.73 | 32.26 |
| vis-mean-0.2 | native | 371 | 40.58 | 96.23 | 0.04 | 1.08 | 1.08 | 7.28 | 1.08 | 4.04 |
| vis-mean-0.2 | quality | 348 | 47.89 | 94.25 | 0.11 | 5.17 | 4.99 | 9.97 | 4.99 | 13.78 |
| vis-mean-dq-0.1 | native | 371 | 37.61 | 53.37 | 0.54 | 40.97 | 40.16 | 49.87 | 36.39 | 50.13 |
| vis-mean-dq-0.1 | quality | 330 | 44.13 | 55.45 | 0.57 | 36.67 | 36.66 | 48.09 | 34.60 | 51.61 |
| vis-centre-dq-0.1 | native | 368 | 38.65 | 63.59 | 0.37 | 29.62 | 29.11 | 49.87 | 26.95 | 47.98 |
| vis-centre-dq-0.1 | quality | 329 | 44.93 | 57.14 | 0.48 | 35.56 | 35.19 | 48.09 | 33.14 | 51.03 |
| vis-mean-dq-large-0.1 | native | 393 | 37.61 | 52.67 | 0.54 | 43.77 | 40.16 | 49.87 | 36.39 | 50.13 |
| vis-mean-dq-large-0.1 | quality | 405 | 44.13 | 50.62 | 0.57 | 40.99 | 36.66 | 48.09 | 34.60 | 51.61 |

## back15, ground

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 485 | 14.16 | 84.74 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1881 | 24.96 | 71.66 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 108 | 9.70 | 76.85 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 1034 | 19.11 | 46.91 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 26 | 6.37 | 3.85 | 35.58 | 96.15 | 94.44 | 98.15 | 88.89 | 100.00 |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 735 | 11.24 | 0.82 | 41.40 | 98.91 | 94.00 | 96.52 | 91.97 | 98.16 |
| vis-mean-0.1 | native | 45 | 7.82 | 44.44 | 23.61 | 33.33 | 64.81 | 75.00 | 60.19 | 86.11 |
| vis-mean-0.1 | quality | 819 | 15.07 | 15.02 | 23.61 | 77.90 | 76.69 | 82.40 | 77.66 | 86.36 |
| vis-mean-0.15 | native | 57 | 8.36 | 56.14 | 16.97 | 22.81 | 54.63 | 58.33 | 45.37 | 61.11 |
| vis-mean-0.15 | quality | 865 | 16.20 | 20.46 | 16.39 | 70.17 | 71.47 | 77.08 | 73.11 | 82.98 |
| vis-mean-0.2 | native | 66 | 8.78 | 60.61 | 10.40 | 18.18 | 43.52 | 51.85 | 37.96 | 53.70 |
| vis-mean-0.2 | quality | 893 | 17.32 | 26.65 | 8.85 | 48.49 | 50.58 | 61.22 | 47.97 | 75.44 |
| vis-mean-dq-0.1 | native | 46 | 7.82 | 45.65 | 23.61 | 32.61 | 64.81 | 74.07 | 60.19 | 86.11 |
| vis-mean-dq-0.1 | quality | 819 | 15.07 | 15.02 | 23.62 | 77.90 | 76.69 | 82.40 | 77.76 | 86.36 |
| vis-centre-dq-0.1 | native | 36 | 7.76 | 27.78 | 24.02 | 69.44 | 81.48 | 94.44 | 70.37 | 96.30 |
| vis-centre-dq-0.1 | quality | 802 | 14.78 | 9.10 | 24.26 | 88.03 | 83.56 | 91.30 | 85.11 | 96.32 |
| vis-mean-dq-large-0.1 | native | 46 | 7.82 | 45.65 | 23.61 | 32.61 | 64.81 | 74.07 | 60.19 | 86.11 |
| vis-mean-dq-large-0.1 | quality | 819 | 15.07 | 15.02 | 23.62 | 77.90 | 76.69 | 82.40 | 77.76 | 86.36 |

## day30, ground

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 310 | 14.85 | 93.23 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 906 | 26.58 | 85.43 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 16 | 10.01 | 93.75 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 275 | 19.19 | 61.82 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 0 | 8.67 | n/a | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 138 | 15.25 | 17.39 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 0 | 9.42 | n/a | 4.87 | n/a | 37.50 | 37.50 | 31.25 | 93.75 |
| vis-mean-0.1 | quality | 147 | 17.95 | 30.61 | 4.85 | 43.54 | 33.82 | 36.00 | 16.36 | 75.64 |
| vis-mean-0.15 | native | 0 | 9.65 | n/a | 3.07 | n/a | 25.00 | 37.50 | 25.00 | 75.00 |
| vis-mean-0.15 | quality | 197 | 18.58 | 51.78 | 2.78 | 26.40 | 24.00 | 28.00 | 9.45 | 47.27 |
| vis-mean-0.2 | native | 3 | 9.86 | 100.00 | 1.58 | 0.00 | 18.75 | 31.25 | 18.75 | 56.25 |
| vis-mean-0.2 | quality | 236 | 18.99 | 56.36 | 1.15 | 8.05 | 8.36 | 10.91 | 1.82 | 14.55 |
| vis-mean-dq-0.1 | native | 0 | 9.42 | n/a | 4.87 | n/a | 37.50 | 37.50 | 31.25 | 93.75 |
| vis-mean-dq-0.1 | quality | 147 | 17.95 | 30.61 | 4.86 | 43.54 | 33.82 | 36.00 | 16.36 | 75.64 |
| vis-centre-dq-0.1 | native | 0 | 9.42 | n/a | 5.04 | n/a | 37.50 | 37.50 | 31.25 | 93.75 |
| vis-centre-dq-0.1 | quality | 143 | 17.70 | 25.17 | 5.28 | 46.15 | 33.45 | 38.55 | 16.73 | 75.27 |
| vis-mean-dq-large-0.1 | native | 0 | 9.42 | n/a | 4.87 | n/a | 37.50 | 37.50 | 31.25 | 93.75 |
| vis-mean-dq-large-0.1 | quality | 147 | 17.95 | 30.61 | 4.86 | 43.54 | 33.82 | 36.00 | 16.36 | 75.64 |

## back60, ground

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 379 | 15.25 | 91.03 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1303 | 27.14 | 85.65 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 28 | 9.87 | 92.86 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 423 | 19.25 | 57.21 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 0 | 8.32 | n/a | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 256 | 14.65 | 8.59 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 4 | 9.18 | 100.00 | 5.70 | 0.00 | 14.29 | 17.86 | 17.86 | 50.00 |
| vis-mean-0.1 | quality | 308 | 17.80 | 27.27 | 5.51 | 37.34 | 31.44 | 35.46 | 22.93 | 75.65 |
| vis-mean-0.15 | native | 7 | 9.48 | 100.00 | 3.44 | 0.00 | 10.71 | 14.29 | 14.29 | 46.43 |
| vis-mean-0.15 | quality | 399 | 18.62 | 49.12 | 2.89 | 17.79 | 18.68 | 25.77 | 11.82 | 47.99 |
| vis-mean-0.2 | native | 29 | 9.81 | 93.10 | 0.77 | 0.00 | 0.00 | 0.00 | 3.57 | 3.57 |
| vis-mean-0.2 | quality | 424 | 19.20 | 56.84 | 0.39 | 3.30 | 3.31 | 4.96 | 1.42 | 6.38 |
| vis-mean-dq-0.1 | native | 4 | 9.18 | 100.00 | 5.70 | 0.00 | 14.29 | 17.86 | 17.86 | 50.00 |
| vis-mean-dq-0.1 | quality | 308 | 17.80 | 27.27 | 5.52 | 37.34 | 31.44 | 35.46 | 22.93 | 75.65 |
| vis-centre-dq-0.1 | native | 7 | 9.15 | 100.00 | 5.89 | 0.00 | 14.29 | 21.43 | 17.86 | 50.00 |
| vis-centre-dq-0.1 | quality | 301 | 17.47 | 22.92 | 6.11 | 39.53 | 33.10 | 39.24 | 24.82 | 81.32 |
| vis-mean-dq-large-0.1 | native | 4 | 9.18 | 100.00 | 5.70 | 0.00 | 14.29 | 17.86 | 17.86 | 50.00 |
| vis-mean-dq-large-0.1 | quality | 308 | 17.80 | 27.27 | 5.52 | 37.34 | 31.44 | 35.46 | 22.93 | 75.65 |

## ground15, ground

| Arm | Mode | Dots | Locked % | Dots locked % | Band released % | Dots released % | Bilinear dots released % | Bilinear dots within 1 px % | Bilinear dots released previous frame % | Bilinear dots released last four % |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline | native | 512 | 9.89 | 85.16 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| baseline | quality | 1305 | 18.78 | 70.42 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | native | 396 | 8.25 | 90.15 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3 | quality | 948 | 17.27 | 59.18 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 | 0.00 |
| shadow-bilinear-3x3+vis-release-0.1 | native | 201 | 4.46 | 8.96 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| shadow-bilinear-3x3+vis-release-0.1 | quality | 618 | 9.22 | 2.75 | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured | unmeasured |
| vis-mean-0.1 | native | 232 | 5.43 | 42.24 | 11.68 | 43.53 | 31.57 | 54.04 | 31.57 | 57.07 |
| vis-mean-0.1 | quality | 754 | 11.81 | 29.44 | 13.48 | 62.60 | 58.23 | 65.51 | 60.02 | 75.74 |
| vis-mean-0.15 | native | 286 | 6.69 | 59.09 | 6.63 | 24.48 | 21.46 | 39.90 | 19.19 | 45.71 |
| vis-mean-0.15 | quality | 823 | 14.35 | 43.62 | 6.55 | 41.07 | 42.30 | 48.84 | 46.62 | 64.35 |
| vis-mean-0.2 | native | 329 | 7.67 | 69.30 | 2.59 | 12.46 | 14.14 | 23.74 | 10.61 | 33.59 |
| vis-mean-0.2 | quality | 894 | 16.22 | 53.13 | 2.00 | 17.67 | 20.04 | 27.32 | 21.52 | 43.88 |
| vis-mean-dq-0.1 | native | 262 | 6.21 | 46.56 | 8.19 | 31.30 | 24.75 | 52.78 | 28.03 | 57.32 |
| vis-mean-dq-0.1 | quality | 777 | 13.05 | 28.19 | 9.57 | 62.42 | 58.12 | 66.98 | 59.18 | 74.47 |
| vis-centre-dq-0.1 | native | 290 | 6.34 | 53.45 | 7.69 | 21.38 | 19.19 | 49.75 | 21.21 | 50.76 |
| vis-centre-dq-0.1 | quality | 823 | 13.41 | 34.63 | 8.68 | 55.89 | 56.75 | 64.98 | 57.49 | 72.68 |
| vis-mean-dq-large-0.1 | native | 214 | 6.21 | 45.33 | 8.19 | 38.32 | 24.75 | 52.78 | 28.03 | 57.32 |
| vis-mean-dq-large-0.1 | quality | 988 | 13.05 | 19.74 | 9.57 | 53.04 | 58.12 | 66.98 | 59.18 | 74.47 |
