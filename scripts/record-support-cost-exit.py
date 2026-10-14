#!/usr/bin/env python3
"""Finalize the one-shot artifact after the dotnet process has returned."""
import datetime
import json
import os
from pathlib import Path
import re
import sys
import tempfile


def finalize(path, status):
    if status == 75:
        return
    if status < 0 or status > 255:
        raise ValueError("Exit status must be between 0 and 255")
    root = json.loads(path.read_text())
    pending = root.get("pendingFinalization")
    if not isinstance(pending, dict):
        raise ValueError("Artifact has no pending sample section")
    section = root.get(pending.get("Section"))
    if not isinstance(section, dict) or section.get("RunId") != pending.get("RunId"):
        raise ValueError("Pending invocation does not match the sample section")
    if not section.get("Samples") or section.get("ProcessExitStatus") is not None:
        raise ValueError("Missing samples or already finalized invocation")
    # Python 3.9 accepts only three or six fraction digits. .NET emits up to seven and trims zeros.
    timestamp = section["CapturedAtUtc"].replace("Z", "+00:00")
    timestamp = re.sub(r"\.(\d+)(?=[+-]\d{2}:\d{2}$)",
                       lambda match: "." + (match.group(1) + "000000")[:6], timestamp)
    captured = datetime.datetime.fromisoformat(timestamp)
    age = (datetime.datetime.now(datetime.timezone.utc) - captured).total_seconds()
    if age < -5 or age > 900:
        raise ValueError("Sample section is stale")
    section["ProcessExitStatus"] = status
    section["FinalizedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    del root["pendingFinalization"]
    temporary = tempfile.NamedTemporaryFile(mode="w", dir=path.parent, prefix=path.name + ".", delete=False)
    try:
        with temporary:
            json.dump(root, temporary, indent=2, allow_nan=False)
            temporary.write("\n")
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary.name, path)
    finally:
        if os.path.exists(temporary.name):
            os.unlink(temporary.name)


if __name__ == "__main__":
    try:
        if len(sys.argv) != 3:
            raise ValueError("Usage: record-support-cost-exit.py <artifact> <exit_status>")
        finalize(Path(sys.argv[1]), int(sys.argv[2]))
    except (OSError, ValueError, KeyError, TypeError) as error:
        sys.exit(str(error))
