"""One filtered Metal test process per filter, serially, without retries or golden rebakes."""
import os
from pathlib import Path
import subprocess
import sys

root = Path.cwd().resolve()
out = Path(sys.argv[1]).resolve()
selection = "((FullyQualifiedName~Temporal&FullyQualifiedName~GpuTests)|FullyQualifiedName~Shadow|FullyQualifiedName~Golden)"
selection += "&(FullyQualifiedName!~Bench)&(FullyQualifiedName!~Perf)"

def guard():
    assert subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip() == str(root)
    assert subprocess.check_output(["git", "branch", "--show-current"], text=True).strip() == "fix/taa-grain-switches"

def run(token):
    guard()
    out.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, KE_GPU_TESTS="1", KE_GRAPHICS_BACKEND="metal-native", KE_TEMPORAL_DIAG=token,
               KE_GOLDEN_EVIDENCE_DIR=str(out/token))
    # These measurements never write committed goldens or shader hash tables.
    for name in ("KE_UPDATE_GOLDENS", "KE_WRITE_SHADER_CORPUS", "KE_WRITE_MSL_BASELINE",
                 "KE_WRITE_HLSL_BASELINE", "KE_WRITE_SPIRV_BASELINE"):
        env.pop(name, None)
    command = ["build-slot", "--heavy", "--label", "grain-guards-"+token, "--", "dotnet", "test",
               "KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj", "-c", "Release", "--no-build",
               "--filter", selection, "--logger", "console;verbosity=detailed",
               "--logger", "trx;LogFileName=results.trx", "--results-directory", str(out/token)]
    print("START", token, flush=True)
    with (out/(token+".log")).open("w") as log:
        result = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
    print("END", token, "exit", result.returncode, flush=True)
    if result.returncode == 75:
        raise SystemExit(75)
    return result.returncode

tokens = sys.argv[2:] or ["shadow-bilinear-3x3", "shadow-bilinear-1"]
results = {token: run(token) for token in tokens}
print("MEASUREMENT EXITS", results)
