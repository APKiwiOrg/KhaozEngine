"""Back15 release overlays and forced-entry proof, serial offscreen captures."""
import os
from pathlib import Path
import subprocess
import sys

game, output = map(lambda p: Path(p).resolve(), sys.argv[1:3])
cases = [("vis-release-0.1+show-vis-release", "vis-release-0.1,show-vis-release", None),
         ("vis-reset-0.2+show-vis-release", "vis-reset-0.2,show-vis-release", None),
         ("shadow-bilinear-3x3+vis-release-0.1+show-vis-release", "shadow-bilinear-3x3,vis-release-0.1,show-vis-release", None),
         ("vis-release-0.1-forced-fused", "vis-release-0.1", "fused"),
         ("vis-release-0.1-forced-split", "vis-release-0.1", "split"),
         ("vis-release-0.1+show-locked-final", "vis-release-0.1,show-locked", None)]
for name, token, entry in cases:
    assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], cwd=game, text=True).strip() == str(game)
    assert subprocess.check_output(["git", "branch", "--show-current"], cwd=game, text=True).strip() == "probe/taa-grain-engine"
    name = "back15-"+name
    command = ["build-slot", "--heavy", "--label", "vis-"+name, "--", "dotnet", "run", "--project",
               "tools/SnapshotTool", "-c", "Release", "--no-build", "--", "shadowgrain", str(output/name),
               "--spot", "back-edge", "--frame-time", "1/15", "--start-time", "0.49777777777777776",
               "--temporal-diag", token]
    if entry:
        command += ["--temporal-resolve", entry]
    env = dict(os.environ, KE_GRAPHICS_BACKEND="metal-native")
    env.pop("KE_TEMPORAL_DIAG", None)
    env.pop("KE_TEMPORAL_RESOLVE", None)
    print("START", name, flush=True)
    output.mkdir(parents=True, exist_ok=True)
    with (output/(name+".log")).open("w") as log:
        result = subprocess.run(command, cwd=game, env=env, stdout=log, stderr=subprocess.STDOUT)
    print("END", name, "exit", result.returncode, flush=True)
    if result.returncode:
        raise SystemExit(result.returncode)
