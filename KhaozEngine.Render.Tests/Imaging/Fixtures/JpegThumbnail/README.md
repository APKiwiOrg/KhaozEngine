# JPEG thumbnail fixtures

Generated with libjpeg-turbo `cjpeg` 3.2.0 (build 20260630). Reproduce both fixtures from the repository root:

```sh
build-slot --label e2-t11-fixtures -- python3 KhaozEngine.Render.Tests/Imaging/Fixtures/JpegThumbnail/generate.py
```

`generate.py` writes temporary PGM and PPM source images and a scan script beside the fixtures and removes them afterward.
These are authored pixel splits, with no external image input.

- `grayscale-halves.jpg`: 64x48, left 32, right 224, one grayscale component at 1x1 sampling.
  The exact encoder command is `cjpeg -baseline -quality 90 -grayscale -restart 5B -outfile grayscale-halves.jpg grayscale.pgm`.
- `full-chroma-halves.jpg`: 64x64, left red (255,0,0), right blue (0,0,255), three components at 1x1 sampling (4:4:4), each in a separate sequential scan.
  The exact encoder command is `cjpeg -baseline -quality 90 -sample 1x1,1x1,1x1 -scans sequential.scans -restart 3B -outfile full-chroma-halves.jpg colour.ppm`.
  `sequential.scans` contains three lines, with component indices 0, 1 and 2, each followed by the scan delimiter required by `cjpeg`.

Both fixtures include restart markers, including the RST7 to RST0 wrap. Writer-generated tests cover 4:2:0 without restarts.
