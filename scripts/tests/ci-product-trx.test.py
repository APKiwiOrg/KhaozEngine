#!/usr/bin/env python3
"""Check that Product CI recognises VSTest storage names and enforces its test roster."""
from contextlib import redirect_stdout
import io
from pathlib import Path
import runpy
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
validate = runpy.run_path(str(ROOT / 'scripts/ci-product-trx.py'))['validate']


class ProductTrxTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='product-trx-')
        self.addCleanup(temporary.cleanup)
        self.result = Path(temporary.name) / 'suite.trx'
        self.enterContext(redirect_stdout(io.StringIO()))

    def write_result(self, assembly='engine.tests', *, total=1, executed=1):
        self.result.write_text(
            '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<TestDefinitions><UnitTest storage="bin/release/{assembly}.dll" /></TestDefinitions>'
            f'<ResultSummary><Counters total="{total}" executed="{executed}" passed="{executed}" failed="0" /></ResultSummary>'
            '</TestRun>'
        )

    def test_accepts_vstest_lowercase_storage_for_the_expected_assembly(self):
        self.write_result()
        validate([self.result], {'Engine.Tests'}, require_all=True)

    def test_refuses_case_collisions_in_the_expected_roster(self):
        self.write_result()
        with self.assertRaises(ValueError):
            validate([self.result], {'Engine.Tests', 'engine.tests'}, require_all=True)

    def test_refuses_an_unexpected_or_missing_assembly(self):
        self.write_result()
        with self.assertRaises(ValueError):
            validate([self.result], {'Other.Tests'}, require_all=True)
        with self.assertRaises(ValueError):
            validate([self.result], {'Engine.Tests', 'Other.Tests'}, require_all=True)

    def test_refuses_empty_or_wholly_skipped_suites(self):
        for total in (0, 1):
            with self.subTest(total=total):
                self.write_result(total=total, executed=0)
                with self.assertRaises(ValueError):
                    validate([self.result], {'Engine.Tests'}, require_all=False)

    def test_preserves_the_skip_policy_for_each_mode(self):
        self.write_result(total=2, executed=1)
        validate([self.result], {'Engine.Tests'}, require_all=False)
        with self.assertRaises(ValueError):
            validate([self.result], {'Engine.Tests'}, require_all=True)


if __name__ == '__main__':
    unittest.main()
