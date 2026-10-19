"""Measure dumped coverage modules with the C8 exhaustive pass, without a native GPU."""
import argparse
import csv
import hashlib
from pathlib import Path
import struct
import subprocess
import tempfile


def instructions(path):
    data = path.read_bytes()
    words = struct.unpack(f"<{len(data) // 4}I", data)
    offset, count, functions, calls = 5, 0, 0, 0
    while offset < len(words):
        length, opcode = words[offset] >> 16, words[offset] & 65535
        if not length or offset + length > len(words):
            raise ValueError(f"{path.name}: malformed SPIR-V")
        functions += opcode == 54
        calls += opcode == 57
        offset += length
        count += 1
    if offset != len(words) or functions != 1 or calls:
        raise ValueError(f"{path.name}: exhaustive inlining did not finish")
    return count


def measure(directory, output):
    rows = []
    with tempfile.TemporaryDirectory(prefix="ke-coverage-size-") as scratch:
        for source in sorted(directory.glob("Coverage*.spv")):
            target = Path(scratch) / source.name
            subprocess.run(["spirv-opt", "--merge-return", "--inline-entry-points-exhaustive",
                            "--eliminate-dead-functions", str(source), "-o", str(target)], check=True)
            subprocess.run(["spirv-val", "--target-env", "vulkan1.3", str(target)], check=True)
            count = instructions(target)
            # Text targets use LF-normalized UTF-8 on every checkout.
            hlsl, msl = (len(source.with_suffix(ext).read_text(encoding="utf-8").encode("utf-8")) for ext in (".hlsl", ".msl"))
            if count > 30000 or hlsl > 100000 or msl > 100000:
                raise ValueError(f"{source.stem}: C8 cap exceeded ({count}, {hlsl}, {msl})")
            rows.append([source.stem, hashlib.sha256(source.read_bytes()).hexdigest(), count, hlsl, msl])
    if not rows:
        raise ValueError("No coverage modules were dumped")
    with output.open("w", newline="") as stream:
        writer = csv.writer(stream, lineterminator="\n")
        writer.writerow(["pipeline", "spirv_sha256", "inline_instructions", "hlsl_bytes", "msl_bytes"])
        writer.writerows(rows)
    for row in rows:
        print(row[0], *row[2:])


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    measure(args.directory, args.output)
