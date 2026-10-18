"""One serial capture per arm and condition. No retries, test loops or windowed clients."""
import os
from pathlib import Path
import subprocess
import sys

GAME = Path(sys.argv[1]).resolve()
OUTPUT = Path(sys.argv[2]).resolve()
ARMS = sys.argv[3:] or ["baseline", "noop", "locks-off", "lock-history-ridge", "lock-shade-release", "lock-bound-current"]
CONDITIONS = [
    ("back15", "back-edge", "1/15", "5"),
    ("day30", "back-edge", "1/15", "30"),
    ("back60", "back-edge", "1/60", "5"),
    ("ground15", "ground-edge", "1/15", "5"),
]
for arm in ARMS:
    for condition, spot, dt, day in CONDITIONS:
        if arm == "noop" and condition != "back15":
            continue
        assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], cwd=GAME, text=True).strip() == str(GAME)
        assert subprocess.check_output(["git", "branch", "--show-current"], cwd=GAME, text=True).strip() == "probe/taa-grain-engine"
        name = f"{condition}-{arm}"
        command = ["build-slot", "--heavy", "--label", f"grain-{name}", "--", "dotnet", "run", "--project",
                   "tools/SnapshotTool", "-c", "Release", "--no-build", "--", "shadowgrain", str(OUTPUT / name),
                   "--spot", spot, "--frame-time", dt, "--day-minutes", day, "--start-time", "0.49777777777777776"]
        if arm != "baseline":
            command += ["--temporal-diag", arm]
        env = dict(os.environ)
        env.pop("KE_TEMPORAL_DIAG", None)
        env["KE_GRAPHICS_BACKEND"] = "metal-native"
        OUTPUT.mkdir(parents=True, exist_ok=True)
        print("START", name, flush=True)
        with (OUTPUT / f"{name}.log").open("w") as log:
            result = subprocess.run(command, cwd=GAME, env=env, stdout=log, stderr=subprocess.STDOUT)
        print("END", name, "exit", result.returncode, flush=True)
        if result.returncode:
            raise SystemExit(result.returncode)
