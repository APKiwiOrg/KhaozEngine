#!/usr/bin/env python3
"""The explicit formatter preserves coverage digests and refuses incomplete evidence."""
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / 'scripts/regenerate-water-prism-census.py'
NAMES = ['bank-stand', 'bank-along', 'bank-across', 'swim-entry', 'deep-swim',
         'deep-tread', 'flat-neighbour', 'flat-control', 'dry-control']


def invoke(trx, baseline, output, code):
    result = subprocess.run([sys.executable, str(TOOL), str(trx), '--baseline', str(baseline), '--output', str(output)],
                            text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    assert result.returncode == code, result.stdout


with tempfile.TemporaryDirectory(prefix='khaozengine-census-format-') as temporary:
    scratch = Path(temporary)
    baseline, output, trx = [scratch / name for name in ('baseline.json', 'result.json', 'results.trx')]
    baseline.write_text(json.dumps({name: {'Digest': 'fixed-' + name} for name in NAMES}))
    root = ET.Element('TestRun')
    for name in NAMES:
        rows = []
        for stage, tick in [('authority', 0), ('replay', 0), ('authority', 1)]:
            rows.append(dict(Stage=stage, Tick=tick, Work={'bases': 2}, Inputs=['same', 'same'], QueryHash='query',
                             RejectedPrisms=0, RejectedFaces=0, RejectedIntersections=0, RejectedBases=0,
                             RejectedDots=0, Spans=1, Contacts=1, Availability='Known', FullyDry=False,
                             FullySubmerged=False, PrismKernel=True))
        ET.SubElement(root, 'StdOut').text = 'CENSUS ' + json.dumps({'name': name, 'rows': rows}) + '\n' + \
            'DIGEST ' + name + ' fixed-' + name + ' traces=3\n'
    ET.ElementTree(root).write(trx)
    invoke(trx, baseline, output, 0)
    data = json.loads(output.read_text())
    assert list(data) == sorted(NAMES)
    for name in NAMES:
        assert data[name]['Digest'] == 'fixed-' + name
        assert data[name]['Census']['Repeats'] == {
            'WithinTraces': 1, 'WithinStep': 1, 'TraceQueries': 0, 'ReplayHits': 2, 'NextTickHits': 2}
        assert len(data[name]['Census']['Traces']) == 3
        assert data[name]['Census']['Traces'][0]['Intersections'] == 2
        assert data[name]['Census']['Traces'][0]['WithinTraceRepeats'] == 1
    original = output.read_bytes()
    invoke(trx, baseline, output, 0)
    assert output.read_bytes() == original and b'\r' not in original
    # A changed coverage digest must fail before modifying the requested output.
    first = root.find('StdOut')
    first.text = first.text.replace('fixed-bank-stand', 'changed-bank-stand')
    ET.ElementTree(root).write(trx)
    invoke(trx, baseline, output, 2)
    assert output.read_bytes() == original
    # Missing one case also refuses, retaining the previously trusted golden.
    root.remove(first)
    ET.ElementTree(root).write(trx)
    invoke(trx, baseline, output, 2)
    assert output.read_bytes() == original
print('census formatter passed counts, stable LF output, unchanged digests and incomplete-evidence refusal')
