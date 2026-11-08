#!/usr/bin/env python3
"""Run the selector against controlled diffs and immutable call inputs."""
import json
import importlib.util
import os
import pathlib
import subprocess
import sys
import tempfile
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

    def test_items_inputs_select_only_the_hosted_items_suite(self):
        for path in ['tools/Items.Benchmark.Tests/ItemsBenchmarkAcceptanceTests.cs',
                     'KhaozEngine.Benchmarks/Items/ItemsBenchmarkRunner.cs',
                     'KhaozEngine.Benchmarks/KhaozEngine.Benchmarks.csproj',
                     'KhaozEngine.Benchmarks/Baselines/items-sqlite-v1-seed915.json',
                     'test-shared/ItemsBenchmarkFields.cs',
                     'test-shared/ItemsBenchmarkFixtureDirectory.cs',
                     'KhaozEngine.Server.Tests/AllocSensitiveCollection.cs']:
            self.assertEqual(self.select('--event', 'push', '--suite', 'items', '--ref', A,
                                         '--changed', path)['include'], [dict(ref=A, os='ubuntu-26.04')])
            self.assertEqual(self.select('--event', 'push', '--suite', 'renderer', '--ref', A,
                                         '--changed', path), {'include': []})
        self.assertEqual(self.select('--event', 'pull_request', '--suite', 'items', '--ref', A,
                                     '--changed', 'KhaozEngine.Audio/AudioBus.cs'), {'include': []})

    def test_nav_inputs_select_only_the_hosted_research_suite(self):
        # The snapshot restore reflects over PhysicsNavBake's private constructor and reads the capture
        # types, so a change to any PhysicsNav source must run the research facts.
        for path in ['tools/NavCapture.Research.Tests/Research/NavCaptureSnapshot.cs',
                     'KhaozEngine.Movement/AssemblyInfo.cs', 'KhaozEngine.Movement/PhysicsNavBake.cs',
                     'KhaozEngine.Movement/PhysicsNavBake.Profiles.cs', 'KhaozEngine.Movement/PhysicsNavColumns.cs']:
            self.assertEqual(self.select('--event', 'push', '--suite', 'nav', '--ref', A,
                                         '--changed', path)['include'], [dict(ref=A, os='ubuntu-26.04')])
            for other in ['renderer', 'items']:
                self.assertEqual(self.select('--event', 'push', '--suite', other, '--ref', A,
                                             '--changed', path), {'include': []})
        for path in ['KhaozEngine.Movement/GroundNavigation.cs', 'KhaozEngine.Audio/AudioBus.cs',
                     'tools/Items.Benchmark.Tests/ItemsBenchmarkAcceptanceTests.cs']:
            self.assertEqual(self.select('--event', 'pull_request', '--suite', 'nav', '--ref', A,
                                         '--changed', path), {'include': []})

    def test_cpu_dispatch_and_shared_inputs_select_explicit_projects(self):
        for suite in ['items', 'nav', 'coverage']:
            self.assertEqual(self.select('--event', 'workflow_dispatch', '--suite', suite, '--ref', A)['include'],
                             [dict(ref=A, os='ubuntu-26.04')])
            for path in ['Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
                     '.testhealth-baseline', '.filesize-baseline', 'nuget.config', 'global.json',
                     '.gitattributes', '.config/dotnet-tools.json', 'scripts/build-release-tests.sh',
                     '.github/workflows/tooling-validation.yml']:
                self.assertEqual(len(self.select('--event', 'push', '--suite', suite, '--ref', A,
                                             '--changed', path)['include']), 1)
            self.assertEqual(len(self.select('--event', 'push', '--suite', suite, '--ref', A,
                                             '--unknown-diff')['include']), 1)

    def test_items_trx_gate_requires_the_named_pass_without_skips(self):
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        marker = '      - name: Require named Items acceptance pass\n'
        self.assertIn(marker, workflow)
        block = workflow.split(marker, 1)[1]
        body = block.split('        run: |\n', 1)[1].split('\n      - ', 1)[0]
        commands = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
        name = ('KhaozEngine.Tests.Benchmarks.ItemsBenchmarkAcceptanceTests.'
                'A_quick_run_measures_every_budget_and_coalesces_the_crafts_into_ONE_commit')
        with tempfile.TemporaryDirectory() as directory:
            results = pathlib.Path(directory) / 'TestResults'
            results.mkdir()
            for total, passed, skipped, test_name, outcome, success in [
                    (1, 1, 0, name, 'Passed', True), (0, 0, 0, name, 'Passed', False),
                    (1, 0, 1, name, 'NotExecuted', False), (1, 1, 0, 'Wrong.fact', 'Passed', False),
                    (1, 0, 0, name, 'Failed', False)]:
                (results / 'items.trx').write_text(
                    f'<TestRun><ResultSummary><Counters total="{total}" passed="{passed}" '
                    f'notExecuted="{skipped}" failed="{int(outcome == "Failed")}" /></ResultSummary>'
                    f'<Results><UnitTestResult testName="{test_name}" outcome="{outcome}" /></Results></TestRun>')
                result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', commands],
                                        cwd=directory, capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode == 0, success, result.stderr)

    def test_workflow_explicit_routes_run_only_the_requested_suite(self):
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        block = workflow.split('      - id: select\n', 1)[1].split('\n  renderer:', 1)[0]
        body = block.split('        run: |\n', 1)[1]
        commands = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
        with tempfile.TemporaryDirectory() as directory:
            output = pathlib.Path(directory) / 'output'
            for event, suite in [('workflow_dispatch', 'items'), ('workflow_dispatch', 'nav'),
                                 ('workflow_dispatch', 'coverage'), ('workflow_call', 'coverage'),
                                 ('workflow_dispatch', 'renderer'), ('workflow_call', 'renderer')]:
                output.write_text('')
                result = subprocess.run(['bash', '-c', commands], cwd=ROOT, capture_output=True, text=True,
                                        env=dict(os.environ, SELECTION_EVENT=event, SUITE=suite,
                                                 REQUESTED_SHA=A, CALL_SHA=B,
                                                 TRUSTED_SHA=A if event == 'workflow_call' else '',
                                                 GITHUB_OUTPUT=str(output)), check=False)
                self.assertEqual(result.returncode, 0, result.stderr)
                fields = dict(line.split('=', 1) for line in output.read_text().splitlines())
                self.assertEqual(fields[suite], 'true')
                for other in {'renderer', 'items', 'nav', 'coverage'} - {suite}:
                    self.assertEqual(fields[other], 'false')
                    other_matrix = 'matrix' if other == 'renderer' else other + '-matrix'
                    self.assertEqual(json.loads(fields[other_matrix]), {'include': []})
            result = subprocess.run(['bash', '-c', commands], cwd=ROOT, capture_output=True, text=True,
                                    env=dict(os.environ, SELECTION_EVENT='workflow_call', SUITE='unknown',
                                             REQUESTED_SHA=A, CALL_SHA=B, TRUSTED_SHA=A,
                                             GITHUB_OUTPUT=str(output)), check=False)
            self.assertNotEqual(result.returncode, 0)

    def test_nav_trx_gate_requires_every_named_pass_without_skips(self):
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        marker = '      - name: Require named navigation research passes\n'
        self.assertIn(marker, workflow)
        body = workflow.split(marker, 1)[1].split('        run: |\n', 1)[1].split('\n      - ', 1)[0]
        commands = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
        names = ['KhaozEngine.Tests.Movement.Research.' + name for name in [
            'NavCaptureResearchTests.OneCapturePreservesFixtureColumnsAndSeparatesStages',
            'NavCaptureSnapshotTests.RestoredSnapshotRetainsEveryBitWithoutSamplingPhysics',
            'NavCaptureSnapshotTests.RestoredFixtureBuildsTheSameCanonicalBakeAsFreshCapture']]
        cases = [(names, ['Passed'] * 3, True), ([], [], False),
                 (names, ['Passed', 'NotExecuted', 'Passed'], False),
                 (names, ['Passed', 'Failed', 'Passed'], False),
                 ([*names[:2], 'Wrong.fact'], ['Passed'] * 3, False),
                 ([names[0]] * 3, ['Passed'] * 3, False)]
        with tempfile.TemporaryDirectory() as directory:
            results = pathlib.Path(directory) / 'TestResults'
            results.mkdir()
            for test_names, outcomes, success in cases:
                counters = (f'total="{len(outcomes)}" passed="{outcomes.count("Passed")}" '
                            f'notExecuted="{outcomes.count("NotExecuted")}" failed="{outcomes.count("Failed")}"')
                rows = ''.join(f'<UnitTestResult testName="{name}" outcome="{outcome}" />'
                               for name, outcome in zip(test_names, outcomes))
                (results / 'nav.trx').write_text(
                    f'<TestRun><ResultSummary><Counters {counters} /></ResultSummary><Results>{rows}</Results></TestRun>')
                result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', commands],
                                        cwd=directory, capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode == 0, success, result.stderr)

    def test_workflow_push_and_pr_watch_every_cpu_selector_input(self):
        spec = importlib.util.spec_from_file_location('selector', SCRIPT)
        selector = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(selector)
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        push = workflow.split('  push:\n', 1)[1].split('  pull_request:\n', 1)[0]
        pr = workflow.split('  pull_request:\n', 1)[1].split('  workflow_dispatch:\n', 1)[0]
        for suite in ['items', 'nav', 'coverage']:
            for pattern in selector.watched_paths(suite):
                self.assertIn(f"      - '{pattern}'", push)
                self.assertIn(f"      - '{pattern}'", pr)

    def test_coverage_selects_headless_tools_and_runtime_dependencies_only(self):
        for path in ['tools/Coverage.Verify/Program.cs', 'tools/Coverage.Verify.Tests/CoverageVerifyProcessTests.cs',
                     'KhaozEngine.Render3D/Coverage/Table/CoverageRecessionTableBinary.cs',
                     'KhaozEngine.Render3D/Coverage/Gpu/CoverageCarryBatch.cs',
                     'KhaozEngine.Render3D/Models/GltfLoader.Coverage.cs',
                     'KhaozEngine.Render3D/Scene3D.CoveragePrepare.cs',
                     'KhaozEngine.Attachments/EquipmentCoverageJson.cs',
                     'KhaozEngine.Attachments.Render3D/HostCoverageBinding.cs']:
            self.assertEqual(self.select('--event', 'push', '--suite', 'coverage', '--ref', A,
                                         '--changed', path)['include'], [dict(ref=A, os='ubuntu-26.04')])
            for other in ['renderer', 'items', 'nav']:
                self.assertEqual(self.select('--event', 'push', '--suite', other, '--ref', A,
                                             '--changed', path), {'include': []})
        self.assertEqual(self.select('--event', 'push', '--suite', 'coverage', '--ref', A,
                                     '--changed', 'KhaozEngine.Audio/AudioBus.cs'), {'include': []})

    def test_coverage_trx_gate_requires_nonempty_process_evidence_without_skips(self):
        workflow = (ROOT / '.github/workflows/tooling-validation.yml').read_text()
        body = workflow.split('      - name: Require nonempty coverage passes without skips\n', 1)[1]
        body = body.split('        run: |\n', 1)[1].split('\n      - ', 1)[0]
        commands = '\n'.join(line[10:] for line in body.splitlines() if line.startswith('          '))
        name = 'KhaozEngine.Tests.CoverageTooling.CoverageVerifyProcessTests.Process_fact'
        cases = [([name], ['Passed'], True), ([], [], False),
                 ([name], ['NotExecuted'], False), ([name], ['Failed'], False),
                 (['Wrong.fact'], ['Passed'], False)]
        with tempfile.TemporaryDirectory() as directory:
            results = pathlib.Path(directory) / 'TestResults'
            results.mkdir()
            for names, outcomes, success in cases:
                counts = (f'total="{len(outcomes)}" passed="{outcomes.count("Passed")}" '
                          f'notExecuted="{outcomes.count("NotExecuted")}" failed="{outcomes.count("Failed")}"')
                rows = ''.join(f'<UnitTestResult testName="{n}" outcome="{o}" />' for n, o in zip(names, outcomes))
                (results / 'coverage.trx').write_text(
                    f'<TestRun><ResultSummary><Counters {counts} /></ResultSummary><Results>{rows}</Results></TestRun>')
                result = subprocess.run(['pwsh', '-NoProfile', '-NonInteractive', '-Command', commands],
                                        cwd=directory, capture_output=True, text=True, check=False)
                self.assertEqual(result.returncode == 0, success, result.stderr)


if __name__ == '__main__':
    unittest.main()
