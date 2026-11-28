#!/usr/bin/env python3
"""Check which projects ordinary CI compiles outside the solution, and which a diff reaches."""
import importlib.util
import pathlib
import subprocess
import sys
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/outside-solution-projects.py'
sys.dont_write_bytecode = True   # loading the script must leave nothing beside it
spec = importlib.util.spec_from_file_location('outside_solution_projects', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

TOOLING = 'tools/Tooling.Tests/Tooling.Tests.csproj'
PROBE = 'tools/Probe/Probe.csproj'

# Written by hand, apart from the discovery it checks. A project added outside the solution fails the roster
# fact by name, and adding it here is the moment its author learns ordinary CI now compiles it.
ROSTER = [
    'KhaozEngine.MapDoc.OracleHarness.Tests/KhaozEngine.MapDoc.OracleHarness.Tests.csproj',
    'native/KhaozEngine.Native.csproj',
    'native/smoke/NativeSmoke.csproj',
    'tools/Coverage.Verify.Tests/Coverage.Verify.Tests.csproj',
    'tools/Coverage.Verify/Coverage.Verify.csproj',
    'tools/Items.Benchmark.Tests/Items.Benchmark.Tests.csproj',
    'tools/NavCapture.Research.Tests/NavCapture.Research.Tests.csproj',
    'tools/Physics.Rebase.Perf.Tests/Physics.Rebase.Perf.Tests.csproj',
    'tools/Renderer.Perf.Tests/Renderer.Perf.Tests.csproj',
    'tools/Renderer.Perf.Tooling.Tests/Renderer.Perf.Tooling.Tests.csproj',
    'tools/Renderer.Perf.Verify/Renderer.Perf.Verify.csproj',
    'tools/WindowsConsoleProbe/Driver/WindowsConsoleDriver.csproj',
    'tools/WindowsConsoleProbe/Probe/WindowsConsoleProbe.csproj',
]


class DiscoveryTests(unittest.TestCase):
    def test_only_tracked_projects_the_solution_does_not_list_are_found(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            for path in ['Engine/Engine.csproj', TOOLING, 'tools/Scratch/Scratch.csproj']:
                (root / path).parent.mkdir(parents=True)
                (root / path).write_text('<Project />\n')
            # A solution written on Windows spells its paths with backslashes.
            (root / module.SOLUTION).write_text(
                '<Solution><Folder Name="/src/"><Project Path="Engine\\Engine.csproj" /></Folder></Solution>\n')
            subprocess.run(['git', 'init', '--quiet'], cwd=root, check=True)
            subprocess.run(['git', 'add', '--', 'Engine/Engine.csproj', TOOLING, module.SOLUTION], cwd=root, check=True)
            self.assertEqual(module.outside(root), [TOOLING])

    def test_the_projects_found_in_this_repository_are_the_roster(self):
        result = subprocess.run([sys.executable, str(SCRIPT), 'list'], capture_output=True, text=True, check=True)
        self.assertEqual(result.stdout.split(), sorted(ROSTER))


class ReachTests(unittest.TestCase):
    def reached(self, changed, affected=()):
        return [project for project, _ in module.reached([PROBE, TOOLING], changed, affected)]

    def test_a_diff_the_tool_names_no_outside_project_for_reaches_nothing(self):
        self.assertEqual(self.reached(['docs/INDEX.md', 'Engine/Scene.cs'], ['/work/Engine/Engine.csproj']), [])

    def test_an_edited_project_file_reaches_that_project_when_the_tool_reports_nothing(self):
        self.assertEqual(self.reached([TOOLING]), [TOOLING])

    def test_a_project_the_tool_names_is_reached_however_the_tool_spells_the_root(self):
        for line in ['/home/runner/work/Engine/Engine/' + TOOLING, 'D:\\a\\Engine\\Engine\\' + TOOLING.replace('/', '\\')]:
            self.assertEqual(self.reached(['Engine/Scene.cs'], [line]), [TOOLING])
        # Another project whose path only ends with the same file name is not this one.
        self.assertEqual(self.reached(['Engine/Scene.cs'], ['/work/Other.' + TOOLING.rsplit('/', 1)[1]]), [])


if __name__ == '__main__':
    unittest.main()
