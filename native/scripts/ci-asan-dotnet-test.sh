#!/bin/sh
# CI only: runs the native core C# tests against the ASan-built library, from the repository root. The
# built KhaozEngine.Gpu.Core.Tests output must already hold the ASan-built libkhaoz_native.so and the two
# fixture libraries.
#
# The ASan runtime is preloaded into the test host alone, through the run settings, and not into the
# dotnet CLI that launches it. The CLI and MSBuild load none of the instrumented code, and on the hosted
# ubuntu-26.04 runner the CLI aborted under the preload in glibc's free before any test host started.
# The test host is the process that loads libkhaoz_native, so this keeps the managed and native boundary
# under the sanitizer. Each test host process writes ASan exit stats to its own log, which proves the
# runtime ran there. Leak detection is off because the .NET runtime's allocations at exit are not the
# library's.
set -eu

[ "$#" -eq 1 ] || { echo "usage: $0 <results-dir>" >&2; exit 2; }
results=$1

runtime=$(clang -print-file-name="libclang_rt.asan-$(uname -m).so")
[ -f "$runtime" ] || runtime="$(clang -print-runtime-dir)/libclang_rt.asan.so"
test -f "$runtime"
echo "ASan runtime: $runtime"

# An uninstrumented copy would pass every test without exercising the sanitizer.
library=KhaozEngine.Gpu.Core.Tests/bin/Release/net10.0/libkhaoz_native.so
imports=$(nm -D --undefined-only "$library" | grep -c ' __asan_' || true)
echo "$library: $imports ASan runtime imports"
if [ "$imports" -eq 0 ]; then
    echo "::error::$library is not the ASan-instrumented build"
    exit 1
fi

logs="$results/asan"
rm -rf "$logs"
mkdir -p "$logs"
settings="$results/asan.runsettings"
cat > "$settings" <<SETTINGS
<RunSettings>
  <RunConfiguration>
    <EnvironmentVariables>
      <LD_PRELOAD>$runtime</LD_PRELOAD>
      <ASAN_OPTIONS>detect_leaks=0:atexit=1:log_path=$logs/testhost</ASAN_OPTIONS>
    </EnvironmentVariables>
  </RunConfiguration>
</RunSettings>
SETTINGS

status=0
dotnet test KhaozEngine.Gpu.Core.Tests/bin/Release/net10.0/KhaozEngine.Gpu.Core.Tests.dll \
    --settings "$settings" --filter "Category!=Diagnostic&Category!=LiveSocket" \
    --logger "trx;LogFileName=asan.trx" --results-directory "$results" || status=$?
# Printed before the log check, so a failure before any test host starts reads as that failure.
echo "dotnet test exit status: $status"

set -- "$logs"/testhost.*
if [ ! -f "$1" ]; then
    echo "::error::the ASan runtime wrote no log, so it did not run in the test host"
    exit 1
fi
for log; do
    echo "== $log"
    head -n 6 "$log"
done
if grep -l "ERROR: AddressSanitizer" "$@"; then
    echo "::error::AddressSanitizer reported an error in the test host"
    exit 1
fi
[ "$status" -eq 0 ] || exit "$status"
python3 native/scripts/check-required-tests.py "$results/asan.trx"
