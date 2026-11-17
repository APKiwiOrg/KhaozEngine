#!/usr/bin/env python3
"""Fail closed unless the water kernel TRX files hold one complete successful execution of each corpus assembly."""
import argparse
from pathlib import Path
import re
import xml.etree.ElementTree as ET


# The corpus spans two test assemblies. Tests that change floating point controls run in their own process, so a
# class split between them keeps its default rows in the first and its changed-control rows in the second.
REQUIRED_CLASSES = {
    'KhaozEngine.TileWorld.Physics.Tests': (
        'ContactQueryCostOracleTests', 'WaterQueryCostMutationTests', 'WaterCoverageComparisonTests',
        'WaterKernelFrozenReferenceTests', 'WaterProjectionWindowTests', 'WaterProjectionMutationTests',
        'WaterProjectionMapTests', 'WaterSharedProjectionTests', 'WaterSharedProjectionCoverageTests'),
    'KhaozEngine.TileWorld.Physics.FloatingPoint.Tests': (
        'MovingShoreProjectionOracleTests', 'WaterProjectionWindowTests', 'WaterProjectionMutationTests',
        'WaterSharedProjectionTests'),
}


def read(path):
    """(assembly, (total, executed, failed)) for one TRX, which must be one complete successful run of a corpus
    assembly that names every class that assembly's roster requires."""
    root = ET.parse(path)
    counters = root.findall('.//{*}Counters')
    if len(counters) != 1:
        raise ValueError(f'{path}: expected exactly one TRX counter record.')
    values = counters[0].attrib
    total, executed, failed = (int(values.get(name, '')) for name in ('total', 'executed', 'failed'))
    if total <= 0 or executed != total or failed != 0:
        raise ValueError(f'{path}: incomplete water kernel execution: total={total}, executed={executed}, failed={failed}.')
    # A TRX names its assembly through each test's dll path, which the Windows runner writes lower-cased.
    storages = {unit.get('storage', '') for unit in root.findall('.//{*}UnitTest')}
    if len(storages) != 1:
        raise ValueError(f'{path}: names {len(storages)} test assemblies, expected one.')
    stem = re.split(r'[\\/]', storages.pop())[-1]
    stem = stem[:-4] if stem.lower().endswith('.dll') else stem
    assembly = next((name for name in REQUIRED_CLASSES if name.casefold() == stem.casefold()), None)
    if assembly is None:
        raise ValueError(f'{path}: {stem} is not a water kernel corpus assembly.')
    classes = {method.get('className', '').split('.')[-1] for method in root.findall('.//{*}TestMethod')}
    missing = set(REQUIRED_CLASSES[assembly]) - classes
    if missing:
        raise ValueError(f'{path}: missing water query oracle classes in {assembly}: ' + ', '.join(sorted(missing)))
    return assembly, (total, executed, failed)


def validate(paths):
    """Totals per corpus assembly. Every assembly must arrive exactly once."""
    runs = {}
    for path in paths:
        assembly, totals = read(path)
        if assembly in runs:
            raise ValueError(f'{assembly} has more than one TRX file.')
        runs[assembly] = totals
    absent = set(REQUIRED_CLASSES) - set(runs)
    if absent:
        raise ValueError('No TRX file for ' + ', '.join(sorted(absent)) + '.')
    return runs


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trx', type=Path, nargs='+', help='One TRX file per corpus assembly.')
    args = parser.parse_args()
    try:
        runs = validate(args.trx)
    except (OSError, ET.ParseError, ValueError) as error:
        parser.error(str(error))
    for assembly, (total, executed, failed) in sorted(runs.items()):
        print(f'Water kernel TRX {assembly}: executed={executed}, total={total}, failed={failed}.')


if __name__ == '__main__':
    main()
