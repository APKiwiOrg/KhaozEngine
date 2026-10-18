"""Serial offscreen captures. Each arm keeps its own FXAA and region masks."""
import os
from pathlib import Path
import subprocess
import sys

game, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
conditions = [("back15", "back-edge", "1/15", "5"),
              ("day30", "back-edge", "1/15", "30"),
              ("back60", "back-edge", "1/60", "5"),
              ("ground15", "ground-edge", "1/15", "5")]

def guard():
    assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], cwd=game, text=True).strip() == str(game)
    assert subprocess.check_output(["git", "branch", "--show-current"], cwd=game, text=True).strip() == "probe/taa-grain-engine"

for arm in sys.argv[3:]:
    for condition, spot, dt, day in conditions:
        guard()
        name = condition + "-" + arm.replace(",", "+")
        command = ["build-slot", "--heavy", "--label", "grain-" + name, "--",
                   "dotnet", "run", "--project", "tools/SnapshotTool", "-c", "Release", "--no-build", "--",
                   "shadowgrain", str(output / name), "--spot", spot, "--frame-time", dt,
                   "--day-minutes", day, "--start-time", "0.49777777777777776"]
        if arm != "baseline":
            command += ["--temporal-diag", arm]
        env = dict(os.environ)
        env.pop("KE_TEMPORAL_DIAG", None)
        env["KE_GRAPHICS_BACKEND"] = "metal-native"
        output.mkdir(parents=True, exist_ok=True)
        print("START", name, flush=True)
        with (output / (name + ".log")).open("w") as log:
            result = subprocess.run(command, cwd=game, env=env, stdout=log, stderr=subprocess.STDOUT)
        print("END", name, "exit", result.returncode, flush=True)
        if result.returncode:
            raise SystemExit(result.returncode)
