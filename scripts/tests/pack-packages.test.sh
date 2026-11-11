#!/bin/sh
# #1765. Both standard pack entrypoints include the isolated consumer tool and preserve pack arguments.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
fixture=$(mktemp -d)
trap 'rm -rf "$fixture"' EXIT
mkdir -p "$fixture/bin"
export PACK_COMMANDS="$fixture/commands"
cat > "$fixture/bin/dotnet" <<'STUB'
#!/bin/sh
printf '%s\n' "$*" >> "$PACK_COMMANDS"
exit "${PACK_FAIL:-0}"
STUB
chmod +x "$fixture/bin/dotnet"
export PATH="$fixture/bin:$PATH"
sh "$root/scripts/pack-packages.sh" -c Release -o artifact-output --no-build
cat > "$fixture/expected" <<'EXPECTED'
pack KhaozEngine.slnx -c Release -o artifact-output --no-build
pack tools/Coverage.Verify/Coverage.Verify.csproj -c Release -o artifact-output --no-build
EXPECTED
cmp "$fixture/expected" "$PACK_COMMANDS"
: > "$PACK_COMMANDS"
if PACK_FAIL=17 sh "$root/scripts/pack-packages.sh" -c Release; then exit 1; else code=$?; fi
[ "$code" = 17 ]
[ "$(wc -l < "$PACK_COMMANDS" | tr -d ' ')" = 1 ]
printf '%s\n' 'pack-packages: argument forwarding, tool inventory and failure propagation passed'
