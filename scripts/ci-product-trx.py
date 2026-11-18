#!/usr/bin/env python3
"""Check that every expected test assembly produced one nonempty, passing TRX."""
import argparse
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET


def validate(paths, expected, require_all):
    seen = set()
    for path in paths:
        root = ET.parse(path)
        assemblies = {PurePosixPath(test.attrib['storage'].replace('\\', '/')).stem
                      for test in root.findall('.//{*}UnitTest')}
        counters = root.findall('.//{*}Counters')
        if len(assemblies) != 1 or len(counters) != 1:
            raise ValueError(f'{path.name}: expected one test assembly and one set of TRX counters')
        assembly, = assemblies
        if assembly not in expected or assembly in seen:
            raise ValueError(f'{path.name}: unexpected or repeated test assembly {assembly}')
        seen.add(assembly)
        values = counters[0].attrib
        total, executed, passed, failed = (int(values[key]) for key in ('total', 'executed', 'passed', 'failed'))
        print(f'{assembly}: passed={passed}, executed={executed}, total={total}, failed={failed}')
        if not 0 < passed == executed <= total or failed != 0 or (require_all and executed != total):
            raise ValueError(f'{path.name}: incomplete test execution')
    if expected - seen:
        raise ValueError('Missing test results: ' + ', '.join(sorted(expected - seen)))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--require-all', action='store_true', help='Every fact must execute')
    parser.add_argument('results', type=Path)
    parser.add_argument('assemblies', nargs='+')
    args = parser.parse_args()
    paths = sorted(args.results.glob('*.trx')) if args.results.is_dir() else [args.results]
    try:
        validate(paths, set(args.assemblies), args.require_all)
    except (ET.ParseError, KeyError, OSError, ValueError) as error:
        parser.error(str(error))


if __name__ == '__main__':
    main()
