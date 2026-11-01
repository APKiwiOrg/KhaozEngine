#!/usr/bin/env python3
"""Run the selector against controlled diffs and immutable call inputs."""
import json
import os
import pathlib
import subprocess
import sys
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/renderer-tooling-selection.py'
A, B = 'a' * 40, 'b' * 40


class SelectionTests(unittest.TestCase):
    def select(self, *args, success=True):
        result = subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True, text=True, check=False)
        self.assertEqual(result.returncode == 0, success, result.stderr)
        return json.loads(result.stdout) if success else None

    def test_push_selects_only_renderer_inputs(self):
        for path in ['tools/Renderer.Perf.Tooling.Tests/Gpu/RendererPerf/RendererPerfUnit.cs',
                     'KhaozEngine.Render.Tests/Gpu/BackendProviderScope.cs',
                     'test-shared/AllocationMeasurement.cs', '.testhealth-baseline']:
            self.assertEqual(len(self.select('--event', 'push', '--ref', A, '--changed', path)['include']), 3)
        self.assertEqual(self.select('--event', 'pull_request', '--ref', A,
                                     '--changed', 'KhaozEngine.Audio/AudioBus.cs'), {'include': []})

    def test_call_validates_both_immutable_refs_and_deduplicates_equal_refs(self):
        rows = self.select('--event', 'workflow_call', '--suite', 'renderer', '--ref', B, '--trusted-ref', A)['include']
        self.assertEqual(len(rows), 6)
        self.assertEqual({row['ref'] for row in rows}, {A, B})
        self.assertEqual({row['os'] for row in rows}, {'ubuntu-26.04', 'windows-latest', 'macos-26'})
        self.assertEqual(len(self.select('--event', 'workflow_call', '--suite', 'renderer', '--ref', A, '--trusted-ref', A)['include']), 3)

    def test_explicit_inputs_refuse_mutable_refs_and_unknown_suites(self):
        for ref in ['main', '', 'a' * 39]:
            self.select('--event', 'workflow_call', '--suite', 'renderer', '--ref', ref, success=False)
        self.select('--event', 'workflow_call', '--suite', 'renderer', '--ref', A, '--trusted-ref', 'main', success=False)
        self.select('--event', 'workflow_call', '--suite', 'renderer', '--ref', A, success=False)
        self.select('--event', 'workflow_dispatch', '--suite', 'engine', '--ref', A, success=False)

    def test_reusable_completion_refuses_skipped_failed_or_cancelled_renderer(self):
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        marker = '      - name: Require completed tooling validation\n'
        self.assertIn(marker, workflow)
        block = workflow.split(marker, 1)[1]
        body = block.split('        run: |\n', 1)[1]
        commands = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
        for outcome in ['failed', 'cancelled', 'skipped', 'success']:
            result = subprocess.run(['bash', '-e', '-c', commands], capture_output=True, text=True,
                                    env=dict(os.environ, REQUIRED='true',
                                             SELECT_RESULT='success', RENDERER_RESULT=outcome), check=False)
            self.assertEqual(result.returncode, 0 if outcome == 'success' else 1, result.stderr)

    def test_unknown_diff_runs_renderer_conservatively(self):
        self.assertEqual(len(self.select('--event', 'push', '--ref', A, '--unknown-diff')['include']), 3)


if __name__ == '__main__':
    unittest.main()
