#!/usr/bin/env python3
"""Check that the compiler opens vendored headers as system headers."""

import argparse
import codecs
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
from functools import lru_cache
from pathlib import Path


LINEMARKER = re.compile(rb'^#\s+\d+\s+"((?:\\.|[^"\\])*)"((?:\s+\d+)*)\s*$')
OUTPUT_OPTIONS = {"-o", "-MF", "-MT", "-MQ", "-MJ", "-serialize-diagnostics", "--serialize-diagnostics"}
JOINED_OUTPUT_OPTIONS = (
    "-o", "-MF", "-MT", "-MQ", "-MJ", "-serialize-diagnostics=", "--serialize-diagnostics=",
    "-ftime-trace=", "-save-temps=", "--save-temps=",
)
DROPPED_FLAGS = {
    "-M", "-MM", "-MD", "-MMD", "-MG", "-MP", "-P", "-ftime-trace", "-save-temps", "--save-temps",
}


def preprocess_command(entry, output):
    arguments = entry["arguments"] if "arguments" in entry else shlex.split(entry["command"])
    iterator = iter(arguments)
    compiler = next(iterator, None)
    if not compiler:
        raise ValueError("missing compiler in compile command")
    command = [compiler]
    trailing = []
    saw_compile = False
    skip_clang_dependency_path = False
    for argument in iterator:
        if argument == "--":
            trailing = ["--", *iterator]
            break
        if argument == "-c":
            saw_compile = True
        elif argument in OUTPUT_OPTIONS:
            if next(iterator, None) is None:
                raise ValueError(f"missing value for {argument}")
        elif argument in DROPPED_FLAGS:
            continue
        elif argument.startswith(JOINED_OUTPUT_OPTIONS) and not argument.startswith("-object-file-name="):
            continue
        elif argument.startswith("-Wp,") and any(option.startswith("-M") for option in argument.split(",")[1:]):
            continue
        elif argument == "-Xclang" or argument.startswith("-Xclang="):
            if argument == "-Xclang":
                option = next(iterator, None)
                if option is None:
                    raise ValueError("missing -Xclang argument")
                forwarded = [argument, option]
            else:
                option = argument.removeprefix("-Xclang=")
                forwarded = [argument]

            # The dependency path is itself passed through another -Xclang.
            if skip_clang_dependency_path:
                skip_clang_dependency_path = False
            elif option == "-dependency-file":
                skip_clang_dependency_path = True
            elif not option.startswith("-dependency-file="):
                command.extend(forwarded)
        else:
            command.append(argument)
    if skip_clang_dependency_path:
        raise ValueError("missing -Xclang dependency path")
    if not saw_compile:
        raise ValueError("expected a Clang or GCC compile command with -c")
    return [*command, "-E", "-o", str(output), *trailing]


@lru_cache(maxsize=4096)
def marker_path(directory, spelling):
    if not spelling or (spelling.startswith(b"<") and spelling.endswith(b">")):
        return None
    decoded, _ = codecs.escape_decode(spelling)
    return (directory / os.fsdecode(decoded)).resolve()


def check_translation_unit(entry, directory, source, vendor):
    with tempfile.TemporaryDirectory(prefix="khaoz-include-check-") as scratch:
        output = Path(scratch) / "preprocessed"
        command = preprocess_command(entry, output)
        environment = {**os.environ, "TMPDIR": scratch, "TMP": scratch, "TEMP": scratch}
        status = subprocess.run(command, cwd=directory, env=environment).returncode
        if status != 0:
            raise ValueError(f"preprocessor exited {status}")
        with output.open("rb") as lines:
            return check_linemarkers(lines, directory, source, vendor)


def check_linemarkers(lines, directory, source, vendor):
    stack = []
    reported = set()
    saw_source = False
    for line in lines:
        match = LINEMARKER.fullmatch(line)
        if match is None:
            continue
        path = marker_path(directory, match[1])
        flags = match[2].split()
        saw_source |= path == source

        # Flag 1 enters a file. Flag 2 returns to its includer.
        if b"2" in flags and stack:
            stack.pop()
        if b"1" in flags or not stack:
            stack.append(path)
        else:
            stack[-1] = path

        if b"3" in flags or path is None or not path.is_relative_to(vendor) or path in reported:
            continue
        reported.add(path)
        print(f"{source}: vendored header is not a system header: {path}", file=sys.stderr)
        chain = " -> ".join(str(item) for item in stack if item is not None)
        print(f"  include chain: {chain}", file=sys.stderr)

    if not saw_source:
        raise ValueError("preprocessor did not emit linemarkers for the source file")
    return not reported


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("-p", required=True, type=Path, metavar="BUILD_DIR")
    build = parser.parse_args().p.resolve()
    vendor = (Path(__file__).resolve().parents[1] / "third_party").resolve()

    subject = build / "compile_commands.json"
    try:
        entries = json.loads(subject.read_text(encoding="utf-8"))
        failed = False
        checked = 0
        for entry in entries:
            directory = (build / entry["directory"]).resolve()
            source = (directory / entry["file"]).resolve()
            if source.is_relative_to(vendor):
                continue
            subject = source
            checked += 1
            if not check_translation_unit(entry, directory, source, vendor):
                failed = True
        if checked == 0:
            raise ValueError("compilation database contains no first-party translation units")
    except (OSError, KeyError, TypeError, ValueError) as error:
        print(f"{subject}: {error}", file=sys.stderr)
        return 1
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
