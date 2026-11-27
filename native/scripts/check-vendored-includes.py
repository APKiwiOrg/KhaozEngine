#!/usr/bin/env python3
"""Keep first-party includes on the vendored CMake targets' SYSTEM paths."""

import os
import re
import subprocess
import sys
from pathlib import Path


SOURCE_SUFFIXES = set(
    ".c .h .cc .cp .cpp .cxx .c++ .hh .hpp .hxx .h++ "
    ".inc .inl .ipp .tcc .tpp .ccm .cppm .cxxm .c++m .ixx".split()
)
TOKENS = re.compile(
    r'(?P<ignored>//[^\n]*|/\*.*?\*/|R"(?P<delimiter>[^()\s\\]{0,16})\(.*?\)(?P=delimiter)")'
    r"""|<[^<>\n]+>|"(?:\\.|[^"\\\n])*"|'(?:\\.|[^'\\\n])*'""",
    re.DOTALL,
)
INCLUDE = re.compile(r'^[^\S\n]*#[^\S\n]*include(?:_next)?[^\S\n]*(?:"([^"\n]+)"|<([^>\n]+)>)', re.MULTILINE)


def include_paths(source):
    text = source.read_text(encoding="utf-8-sig").replace("\\\n", "")
    # Comments are whitespace to the preprocessor. Raw strings may contain sample directives.
    text = TOKENS.sub(lambda token: " " if token["ignored"] else token[0], text)
    for match in INCLUDE.finditer(text):
        yield match[1] or match[2], match[1] is not None


def bypasses_system_include(header, quoted, source, native, vendor, vendor_roots):
    path = Path(header.replace("\\", "/"))
    local = (source.parent / path).resolve()
    if local.is_relative_to(vendor):
        return True
    if quoted and local.is_file():
        return False

    # Also catch paths written from the checkout or the native targets' include roots.
    for root in (native.parent, native, native / "include", native / "src"):
        candidate = (root / path).resolve()
        if candidate.is_relative_to(vendor):
            return True

    if quoted or ".." in path.parts:
        return any((root / path).is_file() for root in vendor_roots)
    return False


def main():
    native = Path(__file__).resolve().parents[1]
    repository = native.parent
    vendor = native / "third_party"
    # The vendored targets publish the component directory or its include subdirectory.
    vendor_roots = [
        component / "include" if (component / "include").is_dir() else component
        for component in vendor.iterdir()
        if component.is_dir()
    ]
    files = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", ":(exclude)native/third_party/**"],
        cwd=repository,
    )
    failed = False
    for name in filter(None, files.split(b"\0")):
        source = repository / os.fsdecode(name)
        if source.suffix.lower() not in SOURCE_SUFFIXES or not source.is_file():
            continue
        for header, quoted in include_paths(source):
            if bypasses_system_include(header, quoted, source, native, vendor, vendor_roots):
                print(
                    f"{source.relative_to(repository)}: include {header!r} by name in angle brackets "
                    "through its vendored CMake target",
                    file=sys.stderr,
                )
                failed = True
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
