#!/usr/bin/env python3
"""Fail unless the device-gated native core facts ran and passed in a TRX result file.

The golden and the capability parity facts skip themselves when no device is present, so a green
test run alone does not show that a lavapipe leg exercised them.
"""

import sys
import xml.etree.ElementTree as ET

NAMESPACE = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
REQUIRED = (
    "KhaozEngine.Tests.GpuCore.CoreGoldenTests.",
    "KhaozEngine.Tests.GpuCore.CoreCapabilityParityTests.",
)


def main():
    if len(sys.argv) != 2:
        print(f"usage: {sys.argv[0]} <results.trx>", file=sys.stderr)
        return 2
    results = ET.parse(sys.argv[1]).getroot().findall(".//t:UnitTestResult", NAMESPACE)
    failed = False
    for prefix in REQUIRED:
        outcomes = {result.get("testName"): result.get("outcome")
                    for result in results if result.get("testName", "").startswith(prefix)}
        for name, outcome in sorted(outcomes.items()):
            print(f"{outcome}: {name}")
        if not outcomes or any(outcome != "Passed" for outcome in outcomes.values()):
            print(f"::error::every {prefix}* fact must run and pass on this leg", file=sys.stderr)
            failed = True
    return int(failed)


if __name__ == "__main__":
    sys.exit(main())
