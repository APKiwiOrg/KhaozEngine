#!/usr/bin/env python3
"""Execute the GPU workflow's Bash boundary with a recording dotnet substitute, never a device."""

import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import unittest


WORKFLOW = (Path(__file__).resolve().parents[2] / ".github/workflows/cross-platform-gpu.yml").read_text()
RENDER = "KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj"
FILTER = "(FullyQualifiedName~Dq|FullyQualifiedName~Coverage)&FullyQualifiedName!~Cost with spaces;$(touch injected)"
SYNC_FILTER = "FullyQualifiedName~Golden|FullyQualifiedName~Compute|FullyQualifiedName~FoliageGpuTests"
SERIAL_SETTINGS = "-- RunConfiguration.MaxCpuCount=1 xUnit.ParallelizeTestCollections=false"
NO_TESTS = "RunConfiguration.TreatNoTestsAsError=true"
BACKENDS = ("metal-native", "direct3d11-native", "vulkan-native")

# This substitute is the external process boundary. It records exactly what the real inline Bash passes.
# Zero matches model the already established VSTest contract, not a new native or SDK proof.
DOTNET = r'''#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
with open("calls.jsonl", "a") as out:
    out.write(json.dumps(args) + "\n")
if any(args[i:i+2] == ["--logger", "trx"] for i in range(len(args) - 1)):
    results = pathlib.Path(args[args.index("--results-directory") + 1])
    results.mkdir(parents=True, exist_ok=True)
    (results / "fixture.trx").write_text('<TestRun><Results><UnitTestResult testName="Fixture.Row" outcome="Passed" /></Results></TestRun>')
print("fixture console output")
exit_code = int(os.environ.get("FIXTURE_EXIT", "0"))
if os.environ.get("FIXTURE_ZERO_MATCH") == "1" and "--" in args:
    if "RunConfiguration.TreatNoTestsAsError=true" in args[args.index("--") + 1:]:
        exit_code = 17
sys.exit(exit_code)
'''


def step(job, name):
    """Read only a named workflow step. No copied runner implementation or YAML dependency."""
    body = re.search(rf"^  {re.escape(job)}:\n(.*?)(?=^  [\w-]+:|\Z)", WORKFLOW, re.M | re.S)
    if body is None:
        raise AssertionError(f"missing job {job}")
    block = re.search(rf"^      - name: {re.escape(name)}\n(.*?)(?=^      - |\Z)", body[1], re.M | re.S)
    if block is None:
        raise AssertionError(f"missing step {job}/{name}")
    return block[1]


def scalar(block, key, indent):
    match = re.search(rf"^{' ' * indent}{re.escape(key)}: (.+)$", block, re.M)
    if match is None:
        raise AssertionError(f"missing {key}")
    return match[1]


class GpuTestEvidence(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="gpu-test-evidence-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "bin").mkdir()
        command = self.root / "bin/dotnet"
        command.write_text(DOTNET)
        command.chmod(0o755)

    def run_step(self, backend="metal-native", targeted=True, sync=False, **extra_env):
        block = step("gpu-vulkan-sync", "Run the golden and compute suite under synchronisation validation") if sync else step("gpu-golden", "Run test suite")
        script = re.search(r"^        run: \|\n((?:          .*\n|\n)*)", block, re.M)
        self.assertIsNotNone(script, "the test must execute the actual inline Bash")
        env = dict(os.environ, PATH=str(self.root / "bin") + os.pathsep + os.environ["PATH"])
        env.update({
            "KE_GRAPHICS_BACKEND": backend,
            "KE_TEST_SHARD": "",
            "KE_TEST_ISOLATE_RENDER": "",
            "KE_RENDER_TEST_FILTER": FILTER if targeted else "",
            "KE_TEST_FILTER": FILTER if targeted else "Category!=LiveSocket",
            "KE_TEST_LOGGER_ARGS": "--logger console;verbosity=detailed",
            "KE_TEST_BUILD_ARGS": "-m:1 -p:TestTfmsInParallel=false" if backend == "vulkan-native" else "",
            "KE_TEST_EXTRA_ARGS": SERIAL_SETTINGS if backend == "vulkan-native" else "",
        })
        env.update(extra_env)
        previous_calls = len((self.root / "calls.jsonl").read_text().splitlines()) if (self.root / "calls.jsonl").exists() else 0
        result = subprocess.run(["bash", "-e", "-o", "pipefail", "-c", textwrap.dedent(script[1])],
                                cwd=self.root, env=env, capture_output=True, text=True)
        calls = [json.loads(line) for line in (self.root / "calls.jsonl").read_text().splitlines()]
        self.assertEqual(len(calls), previous_calls + 1, "each selected mode must make exactly one test invocation")
        return result, calls[-1]

    def test_filtered_backend_arguments_and_actual_trx(self):
        for backend in BACKENDS:
            with self.subTest(backend=backend):
                result, args = self.run_step(backend)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(args[:2], ["test", RENDER])
                self.assertEqual(args[args.index("--filter") + 1], FILTER)
                self.assertFalse((self.root / "injected").exists(), "filter must stay literal shell data")
                self.assertEqual(args.count("--"), 1, "runsettings need exactly one separator")
                self.assertEqual(args.count(NO_TESTS), 1)
                self.assertIn(NO_TESTS, args[args.index("--") + 1:])
                self.assertIn("console;verbosity=detailed", args[:args.index("--")])
                self.assertIn("trx", args[:args.index("--")])
                if backend == "vulkan-native":
                    self.assertIn("-m:1", args)
                    self.assertIn("-p:TestTfmsInParallel=false", args)
                    self.assertEqual(args[args.index("--") + 1:],
                                     ["RunConfiguration.MaxCpuCount=1", "xUnit.ParallelizeTestCollections=false", NO_TESTS])
                self.assertTrue((self.root / "TestResults/targeted-render/fixture.trx").is_file())
                self.assertIn("fixture console output", (self.root / "TestResults/suite-output.log").read_text())

    def test_zero_match_refuses_each_targeted_backend(self):
        for backend in BACKENDS:
            with self.subTest(backend=backend):
                result, _ = self.run_step(backend, FIXTURE_ZERO_MATCH="1")
                self.assertEqual(result.returncode, 17, result.stdout + result.stderr)

    def test_failed_targeted_suite_retains_trx_and_exit(self):
        result, _ = self.run_step(FIXTURE_EXIT="42")
        self.assertEqual(result.returncode, 42, result.stdout + result.stderr)
        self.assertTrue((self.root / "TestResults/targeted-render/fixture.trx").is_file())

    def test_unfiltered_control_does_not_refuse_intentional_empty_assemblies(self):
        for backend in ("metal-native", "vulkan-native"):
            with self.subTest(backend=backend):
                result, args = self.run_step(backend, targeted=False, FIXTURE_ZERO_MATCH="1")
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertEqual(args[:3], ["test", "-c", "Release"])
                self.assertNotIn(NO_TESTS, args)
                self.assertNotIn("trx", args)
                if backend == "vulkan-native":
                    self.assertEqual(args[args.index("--"):], SERIAL_SETTINGS.split())

    def assert_upload_contract(self, job, name, path, artifact, condition):
        block = step(job, name)
        self.assertEqual(scalar(block, "if", 8), condition)
        self.assertEqual(scalar(block, "path", 10), path)
        self.assertEqual(scalar(block, "name", 10), artifact)
        self.assertEqual(scalar(block, "if-no-files-found", 10), "error")
        self.assertEqual(scalar(block, "retention-days", 10), "7")
        # Unrelated TRX must not satisfy this action's required file pattern.
        (self.root / "TestResults").mkdir(exist_ok=True)
        (self.root / "TestResults/side-check.trx").write_text("unrelated")
        self.assertEqual(list(self.root.glob(path)), [])
        return path

    def test_targeted_upload_rejects_missing_trx_and_selects_only_its_results(self):
        path = self.assert_upload_contract("gpu-golden", "Upload targeted render TRX",
                                          "TestResults/targeted-render/*.trx", "targeted-render-trx-${{ matrix.backend }}",
                                          "always() && github.event_name == 'workflow_dispatch' && inputs.renderTestFilter != ''")
        self.run_step()
        self.assertEqual([p.name for p in self.root.glob(path)], ["fixture.trx"])

    def test_sync_keeps_fixed_selection_empty_assembly_policy_and_failure_exit(self):
        result, args = self.run_step(sync=True, FIXTURE_ZERO_MATCH="1")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(args[:3], ["test", "-c", "Release"])
        self.assertEqual(args[args.index("--filter") + 1], SYNC_FILTER)
        self.assertIn("console;verbosity=detailed", args)
        self.assertIn("trx", args)
        self.assertNotIn(NO_TESTS, args)
        self.assertTrue((self.root / "TestResults/sync-trx/fixture.trx").is_file())
        self.assertIn("fixture console output", (self.root / "TestResults/sync-validation.log").read_text())
        result, _ = self.run_step(sync=True, FIXTURE_EXIT="42")
        self.assertEqual(result.returncode, 42, result.stdout + result.stderr)

    def test_sync_upload_rejects_missing_trx_and_selects_only_its_results(self):
        path = self.assert_upload_contract("gpu-vulkan-sync", "Upload synchronisation validation TRX",
                                          "TestResults/sync-trx/*.trx", "vulkan-sync-trx", "always()")
        self.run_step(sync=True)
        self.assertEqual([p.name for p in self.root.glob(path)], ["fixture.trx"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
