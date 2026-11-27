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
from functools import lru_cache
from pathlib import Path


LINEMARKER = re.compile(rb'^#\s+\d+\s+"((?:\\.|[^"\\])*)"((?:\s+\d+)*)\s*$')
OUTPUT_OPTIONS = {"-o", "-MF", "-MT", "-MQ", "-MJ", "-serialize-diagnostics"}
DEPENDENCY_FLAGS = {"-M", "-MM", "-MD", "-MMD", "-MG", "-MP"}


def preprocess_command(entry):
    arguments = entry["arguments"] if "arguments" in entry else shlex.split(entry["command"])
    command = []
    trailing = []
    iterator = iter(arguments)
    for argument in iterator:
        if argument == "--":
            trailing = ["--", *iterator]
            break
        if argument == "-c":
            command.append("-E")
        elif argument in OUTPUT_OPTIONS:
            next(iterator, None)
        elif argument not in DEPENDENCY_FLAGS and argument != "-P":
            if not argument.startswith(("-MF", "-MT", "-MQ", "-MJ")):
                command.append(argument)
    if "-E" not in command:
        raise ValueError("expected a Clang or GCC compile command with -c")
    return [*command, "-o", "-", *trailing]


@lru_cache(maxsize=4096)
def marker_path(directory, spelling):
    if not spelling or (spelling.startswith(b"<") and spelling.endswith(b">")):
        return None
    decoded, _ = codecs.escape_decode(spelling)
    return (directory / os.fsdecode(decoded)).resolve()


def check_translation_unit(entry, directory, source, vendor):
    command = preprocess_command(entry)
    stack = []
    reported = set()
    saw_source = False
    with subprocess.Popen(command, cwd=directory, stdout=subprocess.PIPE) as compiler:
        for line in compiler.stdout:
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

        status = compiler.wait()
    if status != 0:
        raise ValueError(f"preprocessor exited {status}")
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
