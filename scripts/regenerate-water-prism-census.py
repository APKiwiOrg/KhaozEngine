#!/usr/bin/env python3
"""Explicitly regenerate work counts from a complete census TRX, preserving output digests."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
BASELINE = ROOT / 'KhaozEngine.TileWorld.Physics.Tests/Fixtures/WaterPrismCensusBaseline.json'
NAMES = {'bank-stand', 'bank-along', 'bank-across', 'swim-entry', 'deep-swim',
         'deep-tread', 'flat-neighbour', 'flat-control', 'dry-control'}
FIELDS = ('RejectedPrisms', 'RejectedFaces', 'RejectedIntersections', 'RejectedBases', 'RejectedDots',
          'Spans', 'Contacts', 'Availability', 'FullyDry', 'FullySubmerged', 'PrismKernel')


def inputs(rows):
    return [value for row in rows for value in row['Inputs']]


def snapshot(rows):
    first = [row for row in rows if row['Stage'] == 'authority' and row['Tick'] == 0]
    replay = [row for row in rows if row['Stage'] == 'replay']
    following = [row for row in rows if row['Stage'] == 'authority' and row['Tick'] == 1]
    if not first or not replay or not following:
        raise ValueError('Every case must contain authority tick 0, replay and authority tick 1.')
    values = inputs(first)
    known = set(values)
    return {
        'Traces': [dict(Stage=row['Stage'], Tick=row['Tick'], Work=dict(sorted(row['Work'].items())),
                        Intersections=len(row['Inputs']),
                        WithinTraceRepeats=len(row['Inputs']) - len(set(row['Inputs'])),
                        **{key: row[key] for key in FIELDS}) for row in rows],
        'Repeats': dict(WithinTraces=sum(len(row['Inputs']) - len(set(row['Inputs'])) for row in first),
                        WithinStep=len(values) - len(known),
                        TraceQueries=len(first) - len({row['QueryHash'] for row in first}),
                        ReplayHits=sum(value in known for value in inputs(replay)),
                        NextTickHits=sum(value in known for value in inputs(following))),
    }


def regenerate(trx, baseline):
    cases, digests = {}, {}
    for output in ET.parse(trx).findall('.//{*}StdOut'):
        for line in (output.text or '').splitlines():
            line = line.strip()
            if line.startswith('CENSUS '):
                case = json.loads(line[7:])
                name = case['name']
                if name in cases:
                    raise ValueError(f'Duplicate census case {name}.')
                cases[name] = case['rows']
            elif line.startswith('DIGEST '):
                _, name, digest, _ = line.split()
                if name in digests:
                    raise ValueError(f'Duplicate digest for {name}.')
                digests[name] = digest
    if set(cases) != NAMES or set(digests) != NAMES or set(baseline) != NAMES:
        raise ValueError('A complete census build must emit exactly the nine documented cases and digests.')
    result = {}
    for name in sorted(NAMES):
        if digests[name] != baseline[name]['Digest']:
            raise ValueError(f'{name} changed its coverage digest. Work-count regeneration cannot accept output changes.')
        result[name] = dict(Digest=digests[name], Census=snapshot(cases[name]))
    lines = ['{']
    for index, (name, case) in enumerate(result.items()):
        lines.extend(['  ' + json.dumps(name) + ': {', '    "Digest": ' + json.dumps(case['Digest']) + ',',
                      '    "Census": {', '      "Traces": ['])
        traces = case['Census']['Traces']
        lines.extend('        ' + json.dumps(row, separators=(',', ':')) + (',' if i + 1 < len(traces) else '')
                     for i, row in enumerate(traces))
        lines.extend(['      ],', '      "Repeats": ' + json.dumps(case['Census']['Repeats'], separators=(',', ':')),
                      '    }', '  }' + (',' if index + 1 < len(result) else '')])
    return '\n'.join(lines + ['}']) + '\n'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trx', type=Path)
    parser.add_argument('--baseline', type=Path, default=BASELINE, help='Existing trusted coverage digests.')
    parser.add_argument('--output', type=Path, default=BASELINE)
    args = parser.parse_args()
    try:
        content = regenerate(args.trx, json.loads(args.baseline.read_text()))
    except (ValueError, KeyError, TypeError, OSError, ET.ParseError) as error:
        parser.error(str(error))
    args.output.write_text(content, encoding='utf-8', newline='\n')
    print(args.output)


if __name__ == '__main__':
    main()
