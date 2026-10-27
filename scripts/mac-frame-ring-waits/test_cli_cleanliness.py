"""Direct command-line helpers must not dirty their own frozen source directory."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from test_capacity import capacity_fixture


class CliCleanlinessTests(unittest.TestCase):
    def copy_helpers(self, root):
        helpers = root / 'helpers'
        helpers.mkdir()
        for name in ('analyze.py', 'collect.py', 'capacity.py', 'archive.py'):
            shutil.copy2(Path(__file__).with_name(name), helpers / name)
        return helpers

    def invoke(self, helpers, name, *arguments):
        environment = dict(os.environ)
        for key in ('PYTHONDONTWRITEBYTECODE', 'PYTHONPYCACHEPREFIX', 'PYTHONPATH'):
            environment.pop(key, None)
        return subprocess.run(
            [sys.executable, str(helpers / name), *map(str, arguments)],
            cwd=helpers.parent, env=environment, capture_output=True, text=True, timeout=30,
        )

    def assert_source_untouched(self, helpers):
        self.assertEqual(
            {'analyze.py', 'collect.py', 'capacity.py', 'archive.py'},
            {str(path.relative_to(helpers)) for path in helpers.rglob('*')},
            'The command created files in the frozen helper source directory',
        )

    def test_collector_help_does_not_contaminate_its_later_source_gate(self):
        with tempfile.TemporaryDirectory(prefix='ring-cli-') as directory:
            helpers = self.copy_helpers(Path(directory))
            result = self.invoke(helpers, 'collect.py', '--help')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assert_source_untouched(helpers)

    def test_capacity_analysis_does_not_contaminate_the_next_collection(self):
        for slots in (3, 6):
            with self.subTest(recording_slots=slots), tempfile.TemporaryDirectory(prefix='ring-cli-') as directory:
                root = Path(directory)
                helpers = self.copy_helpers(root)
                report, proof = capacity_fixture(slots)
                raw = json.dumps(report).encode()
                proof['resultSha256'] = hashlib.sha256(raw).hexdigest()
                (root / 'input.json').write_bytes(raw)
                (root / 'collection.json').write_text(json.dumps(proof))
                result = self.invoke(
                    helpers, 'analyze.py', root / 'input.json', root / 'output.json',
                    '--mode', '1', '--pid', '123', '--collection', root / 'collection.json',
                    '--expected-recording-slots', slots,
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual('valid', json.loads((root / 'output.json').read_text())['status'])
                self.assert_source_untouched(helpers)

    def test_archive_help_leaves_the_frozen_helper_directory_unchanged(self):
        with tempfile.TemporaryDirectory(prefix='ring-cli-') as directory:
            helpers = self.copy_helpers(Path(directory))
            result = self.invoke(helpers, 'archive.py', '--help')
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assert_source_untouched(helpers)


if __name__ == '__main__':
    unittest.main()
