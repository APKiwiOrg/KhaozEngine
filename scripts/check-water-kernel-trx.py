#!/usr/bin/env python3
"""Fail closed unless a water kernel TRX contains a complete successful execution."""
import argparse
from pathlib import Path
import xml.etree.ElementTree as ET


def validate(path):
    counters = ET.parse(path).findall('.//{*}Counters')
    if len(counters) != 1:
        raise ValueError('Expected exactly one TRX counter record.')
    values = counters[0].attrib
    total, executed, failed = (int(values.get(name, '')) for name in ('total', 'executed', 'failed'))
    if total <= 0 or executed != total or failed != 0:
        raise ValueError(f'Incomplete water kernel execution: total={total}, executed={executed}, failed={failed}.')
    return total, executed, failed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('trx', type=Path)
    args = parser.parse_args()
    try:
        total, executed, failed = validate(args.trx)
    except (OSError, ET.ParseError, ValueError) as error:
        parser.error(str(error))
    print(f'Water kernel TRX: executed={executed}, total={total}, failed={failed}.')


if __name__ == '__main__':
    main()
