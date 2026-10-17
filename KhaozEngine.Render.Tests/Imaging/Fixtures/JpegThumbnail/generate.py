"""Reproduce the small baseline fixtures with libjpeg-turbo cjpeg 3.2.0."""
from pathlib import Path
import subprocess
import tempfile

here = Path(__file__).resolve().parent
with tempfile.TemporaryDirectory(dir=here) as temporary:
    root = Path(temporary)
    gray = root / "grayscale.pgm"
    gray.write_bytes(b"P5\n64 48\n255\n" + bytes(32 if x < 32 else 224 for y in range(48) for x in range(64)))
    colour = root / "colour.ppm"
    colour.write_bytes(b"P6\n64 64\n255\n" + b"".join(bytes((255, 0, 0) if x < 32 else (0, 0, 255)) for y in range(64) for x in range(64)))
    scans = root / "sequential.scans"
    scans.write_text("0;\n1;\n2;\n")
    subprocess.run(["cjpeg", "-baseline", "-quality", "90", "-grayscale", "-restart", "5B", "-outfile", str(here / "grayscale-halves.jpg"), str(gray)], check=True)
    subprocess.run(["cjpeg", "-baseline", "-quality", "90", "-sample", "1x1,1x1,1x1", "-scans", str(scans), "-restart", "3B", "-outfile", str(here / "full-chroma-halves.jpg"), str(colour)], check=True)
