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

COMPLETE = dict(total='7', executed='7', failed='0')
DEFAULT, FLOATING = 'KhaozEngine.TileWorld.Physics.Tests', 'KhaozEngine.TileWorld.Physics.FloatingPoint.Tests'


class WaterKernelTrxTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='water-kernel-trx-')
        self.addCleanup(self.directory.cleanup)
        self.files = 0

    def trx(self, assembly, attributes=COMPLETE, classes=None, storages=None):
        self.files += 1
        path = Path(self.directory.name) / f'results-{self.files}.trx'
        root = ET.Element('TestRun', xmlns='http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        ET.SubElement(ET.SubElement(root, 'ResultSummary'), 'Counters', attributes)
        definitions = ET.SubElement(root, 'TestDefinitions')
        names = guard.REQUIRED_CLASSES.get(assembly, ()) if classes is None else classes
        storages = storages or [f'/runner/work/{assembly}/bin/Release/net10.0/{assembly}.dll']
        for index, name in enumerate(names):
            unit = ET.SubElement(definitions, 'UnitTest', storage=storages[index % len(storages)])
            ET.SubElement(unit, 'TestMethod', className='Tests.' + name)
        ET.ElementTree(root).write(path)
        return path

    def validate(self, *paths):
        return guard.validate(paths)

    def test_complete_execution_of_both_assemblies(self):
        self.assertEqual({DEFAULT: (7, 7, 0), FLOATING: (7, 7, 0)}, self.validate(self.trx(DEFAULT), self.trx(FLOATING)))

    def test_a_lower_cased_windows_storage_path_names_its_assembly(self):
        lower = [f'd:\\a\\bin\\{FLOATING.lower()}.dll']
        self.assertIn(FLOATING, self.validate(self.trx(DEFAULT), self.trx(FLOATING, storages=lower)))

    def test_empty_incomplete_and_failed_executions(self):
        for attributes in [dict(total='0', executed='0', failed='0'), dict(total='7', executed='6', failed='0'),
                           dict(total='7', executed='7', failed='1')]:
            for broken in (DEFAULT, FLOATING):
                with self.subTest(attributes=attributes, broken=broken), self.assertRaises(ValueError):
                    self.validate(*(self.trx(a, attributes if a == broken else COMPLETE) for a in (DEFAULT, FLOATING)))

    def test_missing_and_malformed_counters(self):
        for attributes in [dict(total='7', executed='7'), dict(total='many', executed='7', failed='0')]:
            with self.subTest(attributes=attributes), self.assertRaises(ValueError):
                self.validate(self.trx(DEFAULT, attributes), self.trx(FLOATING))

    def test_each_assembly_arrives_exactly_once(self):
        for paths in [(DEFAULT,), (FLOATING,), (DEFAULT, DEFAULT, FLOATING), (DEFAULT, FLOATING, FLOATING)]:
            with self.subTest(paths=paths), self.assertRaises(ValueError):
                self.validate(*(self.trx(a) for a in paths))

    def test_an_unexpected_or_mixed_assembly_is_refused(self):
        other = self.trx('KhaozEngine.TileWorld.Tests', classes=guard.REQUIRED_CLASSES[DEFAULT])
        with self.assertRaises(ValueError):
            self.validate(self.trx(DEFAULT), self.trx(FLOATING), other)
        mixed = self.trx(DEFAULT, storages=[f'/bin/{DEFAULT}.dll', f'/bin/{FLOATING}.dll'])
        with self.assertRaises(ValueError):
            self.validate(mixed, self.trx(FLOATING))

    def test_a_positive_execution_cannot_hide_an_unselected_oracle_class(self):
        self.assertEqual({
            DEFAULT: ('ContactQueryCostOracleTests', 'WaterQueryCostMutationTests', 'WaterCoverageComparisonTests',
                      'WaterKernelFrozenReferenceTests', 'WaterProjectionWindowTests', 'WaterProjectionMutationTests',
                      'WaterProjectionMapTests', 'WaterSharedProjectionTests', 'WaterSharedProjectionCoverageTests',
                      'WaterBoundaryWadeCertificateTests'),
            FLOATING: ('MovingShoreProjectionOracleTests', 'WaterProjectionWindowTests', 'WaterProjectionMutationTests',
                       'WaterSharedProjectionTests', 'WaterBoundaryWadeCertificateTests')}, guard.REQUIRED_CLASSES)
        for assembly, roster in guard.REQUIRED_CLASSES.items():
            other = FLOATING if assembly == DEFAULT else DEFAULT
            for missing in roster:
                with self.subTest(assembly=assembly, missing=missing), self.assertRaises(ValueError):
                    self.validate(self.trx(assembly, classes=tuple(name for name in roster if name != missing)),
                                  self.trx(other))


if __name__ == '__main__':
    unittest.main()
