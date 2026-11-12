#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

SCRIPT = Path(__file__).resolve().parents[1] / 'check-water-kernel-trx.py'
spec = importlib.util.spec_from_file_location('water_kernel_trx', SCRIPT)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class WaterKernelTrxTests(unittest.TestCase):
    def validate(self, attributes):
        with tempfile.TemporaryDirectory(prefix='water-kernel-trx-') as directory:
            path = Path(directory) / 'results.trx'
            root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
            ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', attributes)
            ET.ElementTree(root).write(path)
            return guard.validate(path)

    def test_complete_execution(self):
        self.assertEqual((7, 7, 0), self.validate(dict(total='7', executed='7', failed='0')))

    def test_empty_execution(self):
        with self.assertRaises(ValueError):
            self.validate(dict(total='0', executed='0', failed='0'))

    def test_incomplete_execution(self):
        with self.assertRaises(ValueError):
            self.validate(dict(total='7', executed='6', failed='0'))

    def test_failed_execution(self):
        with self.assertRaises(ValueError):
            self.validate(dict(total='7', executed='7', failed='1'))

    def test_missing_and_malformed_counters(self):
        for attributes in [dict(total='7', executed='7'), dict(total='many', executed='7', failed='0')]:
            with self.subTest(attributes=attributes), self.assertRaises(ValueError):
                self.validate(attributes)


if __name__ == '__main__':
    unittest.main()
