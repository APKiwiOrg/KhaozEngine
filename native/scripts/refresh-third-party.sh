#!/bin/sh
set -eu

third_party=$(CDPATH= cd -- "$(dirname -- "$0")/../third_party" && pwd)
manifest="$third_party/README.md"

fail() {
    printf '%s\n' "refresh-third-party: $*" >&2
    exit 1
}

check_sha256() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum -c -
    else
        shasum -a 256 -c -
    fi
}

checksums=$(awk 'length($1) == 64 && $1 !~ /[^0-9a-f]/ && NF == 2 { print $1 "  " $2 }' "$manifest")
[ -n "$checksums" ] || fail "no file hashes in the manifest"

[ "$#" -eq 1 ] || fail "usage: $0 <component|--check>"
name=$1
if [ "$name" = --check ]; then
    cd "$third_party"
    expected=$(printf '%s\n' "$checksums" | awk '{ print "./" $2 }' | LC_ALL=C sort)
    actual=$(find . ! -type d ! -path './README.md' -print)
    actual=$(printf '%s\n' "$actual" | LC_ALL=C sort)
    [ "$expected" = "$actual" ] || fail "vendored file list differs from the manifest"
    if find . -type l -print | grep -q .; then
        fail "vendored files must not be symbolic links"
    fi
    printf '%s\n' "$checksums" | check_sha256
    exit
fi

pin=$(awk -F ' *[|] *' -v name="$name" '
    $2 == name && $4 ~ /^https:\/\/github\.com\// { print $4, $5, $7 }
' "$manifest")
[ -n "$pin" ] || fail "unknown component '$name'"
read -r repo tag archive_sha256 <<EOF
$pin
EOF

selected=$(printf '%s\n' "$checksums" | awk -v prefix="$name/" 'index($2, prefix) == 1')
[ -n "$selected" ] || fail "no file hashes for '$name'"

staging=$(mktemp -d)
trap 'rm -rf -- "$staging"' 0
trap 'exit 1' HUP INT TERM
cd "$staging"
curl --proto '=https' --proto-redir '=https' --fail --silent --show-error --location \
    "https://codeload.github.com/${repo#https://github.com/}/tar.gz/refs/tags/$tag" -o source.tar.gz
printf '%s  %s\n' "$archive_sha256" source.tar.gz | check_sha256

tar -tzf source.tar.gz > archive-files
IFS= read -r root < archive-files
case "$root" in
    */) ;;
    *) fail "expected a top-level directory in the archive" ;;
esac

# Extract only the recorded files from the verified archive.
set --
while read -r digest path; do
    set -- "$@" "$root${path#*/}"
done <<EOF
$selected
EOF
mkdir "$name"
tar -xzf source.tar.gz -C "$name" --strip-components 1 "$@"
printf '%s\n' "$selected" | check_sha256

mkdir -p "$third_party/$name"
cp -R "$name/." "$third_party/$name/"
